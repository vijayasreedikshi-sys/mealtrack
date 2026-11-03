using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>
/// Seller module: cooking requirements, assigned schedules, preparation updates and history.
/// Every query is scoped to seller_assignments for the logged-in seller, so a seller can
/// never see or update a meal they are not assigned to.
/// </summary>
[Authorize(Roles = Roles.Seller)]
[Route("seller")]
public class SellerController(Db db, CountService counts, NotificationService notifications) : AppController
{
    // Assigned, published meals with live totals (valid counts of active customers only).
    internal const string AssignmentSql =
        "SELECT s.id AS schedule_id, s.schedule_date, mt.meal_name, mt.sort_order, f.food_name, f.unit, s.cutoff_time, " +
        "       a.can_view_customers, COALESCE(SUM(c.quantity), 0) AS total_qty, COUNT(c.id) AS submitters, " +
        "       fp.prepared_quantity, fp.wasted_quantity, COALESCE(fp.preparation_status, 'not_started') AS preparation_status " +
        "FROM seller_assignments a " +
        "JOIN food_schedule s ON s.id = a.food_schedule_id AND s.is_published = 1 " +
        "JOIN meal_types mt ON mt.id = s.meal_type_id " +
        "JOIN food_items f ON f.id = s.food_item_id " +
        "LEFT JOIN food_preparation fp ON fp.food_schedule_id = s.id AND fp.seller_id = a.seller_id " +
        "LEFT JOIN (customer_meal_counts c " +
        "           JOIN users cu ON cu.id = c.customer_id AND cu.status='active' AND cu.role='customer') " +
        "      ON c.food_schedule_id = s.id AND c.submission_status IN ('submitted','locked') " +
        "WHERE a.seller_id = @sellerId ";

    internal const string AssignmentGroupBy =
        "GROUP BY s.id, s.schedule_date, mt.meal_name, mt.sort_order, f.food_name, f.unit, s.cutoff_time, " +
        "         a.can_view_customers, fp.prepared_quantity, fp.wasted_quantity, fp.preparation_status " +
        "ORDER BY s.schedule_date, mt.sort_order";

    internal const string PrepSql =
        "SELECT p.id, p.seller_id, u.name AS seller_name, p.food_schedule_id, s.schedule_date, mt.meal_name, f.food_name, " +
        "       p.required_quantity, p.prepared_quantity, p.consumed_quantity, p.remaining_quantity, p.wasted_quantity, " +
        "       p.preparation_status, p.remarks, p.updated_at " +
        "FROM food_preparation p " +
        "JOIN users u ON u.id = p.seller_id " +
        "JOIN food_schedule s ON s.id = p.food_schedule_id " +
        "JOIN meal_types mt ON mt.id = s.meal_type_id " +
        "JOIN food_items f ON f.id = s.food_item_id ";

    private static readonly string[] PrepStatuses = ["not_started", "preparing", "completed"];

    private Task<List<AssignmentRow>> AssignmentsAsync(DateTime start, DateTime end) => db.QueryAsync<AssignmentRow>(
        AssignmentSql + "AND s.schedule_date BETWEEN @start AND @end " + AssignmentGroupBy,
        new { sellerId = Me.Id, start = start.Date, end = end.Date });

    private async Task<AssignmentRow?> AssignmentAsync(int scheduleId) => (await db.QueryAsync<AssignmentRow>(
        AssignmentSql + "AND s.id = @scheduleId " + AssignmentGroupBy, new { sellerId = Me.Id, scheduleId })).FirstOrDefault();

    [HttpGet("")]
    public async Task<IActionResult> Dashboard(string? date)
    {
        var selected = ParseDate(date) ?? DateTime.Today;
        var today = DateTime.Today;
        var week = await AssignmentsAsync(today.AddDays(1), today.AddDays(7));
        var recent = await db.QueryAsync<PrepRecord>(
            PrepSql + "WHERE p.seller_id=@id AND s.schedule_date BETWEEN @from AND @to", new { id = Me.Id, from = today.AddDays(-7), to = today.AddDays(-1) });
        return View(new SellerDashboardVm
        {
            Selected = selected,
            Rows = await AssignmentsAsync(selected, selected),
            Upcoming = week,
            RecentPrepared = recent.Sum(r => r.PreparedQuantity),
            RecentWasted = recent.Sum(r => r.WastedQuantity),
        });
    }

    [HttpGet("requirements")]
    public async Task<IActionResult> Requirements(string? date)
    {
        var selected = ParseDate(date) ?? DateTime.Today;
        var rows = await AssignmentsAsync(selected, selected);
        var breakdown = new Dictionary<int, List<CustomerQty>>();
        foreach (var r in rows.Where(r => r.CanViewCustomers))
            breakdown[r.ScheduleId] = await counts.CustomerBreakdownAsync(r.ScheduleId);
        return View(new RequirementsVm { Selected = selected, Rows = rows, Breakdown = breakdown });
    }

    [HttpGet("assignments")]
    public async Task<IActionResult> Assignments(string? week)
    {
        var ws = WeekStart(ParseDate(week) ?? DateTime.Today);
        var rows = await AssignmentsAsync(ws, ws.AddDays(13));
        return View(new AssignmentsVm { WeekStart = ws, Grouped = rows.GroupBy(r => r.ScheduleDate).ToList() });
    }

    // ---------------- preparation updates ----------------
    [HttpGet("prep/{scheduleId:int}")]
    public async Task<IActionResult> Prep(int scheduleId)
    {
        var a = await AssignmentAsync(scheduleId);
        if (a is null) return StatusCode(StatusCodes.Status403Forbidden);
        var existing = await db.QueryOneAsync<PrepRecord>(
            PrepSql + "WHERE p.food_schedule_id=@scheduleId AND p.seller_id=@id", new { scheduleId, id = Me.Id });
        var vm = new PrepFormVm { Assignment = a, Existing = existing };
        if (existing is not null)
        {
            vm.Prepared = existing.PreparedQuantity;
            vm.Consumed = existing.ConsumedQuantity;
            vm.Remaining = existing.RemainingQuantity;
            vm.Wasted = existing.WastedQuantity;
            vm.Status = existing.PreparationStatus;
            vm.Remarks = existing.Remarks ?? "";
        }
        else
        {
            vm.Prepared = a.TotalQty;
        }
        return View(vm);
    }

    [HttpPost("prep/{scheduleId:int}")]
    public async Task<IActionResult> PrepSave(int scheduleId, string? prepared, string? consumed, string? remaining,
        string? wasted, string? status, string? remarks)
    {
        var a = await AssignmentAsync(scheduleId);
        if (a is null) return StatusCode(StatusCodes.Status403Forbidden);

        var vm = new PrepFormVm
        {
            Assignment = a,
            Prepared = ParseInt(prepared, 0, 100000) ?? -1,
            Consumed = ParseInt(consumed, 0, 100000) ?? -1,
            Remaining = ParseInt(remaining, 0, 100000) ?? -1,
            Wasted = ParseInt(wasted, 0, 100000) ?? -1,
            Status = PrepStatuses.Contains(status) ? status! : "",
            Remarks = Clean(remarks, 255),
        };
        var errors = new List<string>();
        if (a.ScheduleDate > DateTime.Today) errors.Add("Preparation can be recorded on the day of the meal.");
        if (vm.Prepared < 0 || vm.Consumed < 0 || vm.Remaining < 0 || vm.Wasted < 0)
            errors.Add("Quantities must be whole numbers of 0 or more.");
        else
        {
            if (vm.Consumed + vm.Remaining > vm.Prepared) errors.Add("Consumed + remaining can't be more than prepared.");
            if (vm.Wasted > vm.Remaining) errors.Add("Wasted can't be more than the remaining quantity.");
        }
        if (vm.Status == "") errors.Add("Choose a preparation status.");
        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            vm.Existing = await db.QueryOneAsync<PrepRecord>(
                PrepSql + "WHERE p.food_schedule_id=@scheduleId AND p.seller_id=@id", new { scheduleId, id = Me.Id });
            return View("Prep", vm);
        }

        // The required quantity is a snapshot of the automatic total at the time of saving.
        var required = await counts.ScheduleTotalAsync(scheduleId);
        await db.ExecuteAsync(
            "INSERT INTO food_preparation (seller_id, food_schedule_id, required_quantity, prepared_quantity, consumed_quantity, " +
            "  remaining_quantity, wasted_quantity, preparation_status, remarks, updated_at) " +
            "VALUES (@sellerId, @scheduleId, @required, @Prepared, @Consumed, @Remaining, @Wasted, @Status, @remarks, @now) " +
            "ON DUPLICATE KEY UPDATE required_quantity=@required, prepared_quantity=@Prepared, consumed_quantity=@Consumed, " +
            "  remaining_quantity=@Remaining, wasted_quantity=@Wasted, preparation_status=@Status, remarks=@remarks, updated_at=@now",
            new { sellerId = Me.Id, scheduleId, required, vm.Prepared, vm.Consumed, vm.Remaining, vm.Wasted, vm.Status,
                  remarks = vm.Remarks == "" ? null : vm.Remarks, now = DateTime.Now });

        if (vm.Status == "completed")
        {
            var pct = vm.Prepared == 0 ? 0 : Math.Round(vm.Wasted * 100m / vm.Prepared, 1);
            await notifications.NotifyRoleAsync(Roles.Admin,
                $"{Me.Name} completed {a.MealName} ({a.FoodName}) on {a.ScheduleDate:ddd dd MMM}: required {required}, " +
                $"prepared {vm.Prepared}, wasted {vm.Wasted} ({pct}%).",
                "/admin/reports?type=seller", $"prep:{scheduleId}:{Me.Id}");
        }
        Flash("success", $"Preparation for {a.MealName} on {a.ScheduleDate:ddd dd MMM} saved.");
        return Redirect($"/seller?date={a.ScheduleDate:yyyy-MM-dd}");
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(string? start, string? end)
    {
        var endDate = ParseDate(end) ?? DateTime.Today;
        var startDate = ParseDate(start) ?? endDate.AddDays(-29);
        if (startDate > endDate) (startDate, endDate) = (endDate, startDate);
        var rows = await db.QueryAsync<PrepRecord>(
            PrepSql + "WHERE p.seller_id=@id AND s.schedule_date BETWEEN @startDate AND @endDate " +
            "ORDER BY s.schedule_date DESC, mt.sort_order", new { id = Me.Id, startDate, endDate });
        return View(new PrepHistoryVm { Start = startDate, End = endDate, Rows = rows });
    }
}
