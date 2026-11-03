using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Admin: View Food Counts (date/meal/customer filters, All / Submitted / Pending tabs).</summary>
[Authorize(Roles = Roles.Admin)]
[Route("admin/counts")]
public class AdminCountsController(Db db, CountService counts, ScheduleService schedules) : AppController
{
    private const int PerPage = 50;

    // Every (active customer × published meal) pair in range, with the count if one exists.
    private const string BaseSql =
        "FROM food_schedule s " +
        "JOIN meal_types mt ON mt.id = s.meal_type_id " +
        "JOIN food_items f ON f.id = s.food_item_id " +
        "JOIN users u ON u.role='customer' AND u.status='active' " +
        "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = u.id " +
        "WHERE s.is_published = 1 AND s.schedule_date BETWEEN @start AND @end ";

    [HttpGet("")]
    public async Task<IActionResult> Index(string? start, string? end, string? meal, string? customer, string? tab, string? page)
    {
        var today = DateTime.Today;
        var startDate = ParseDate(start) ?? today;
        var endDate = ParseDate(end) ?? startDate;
        if (startDate > endDate) (startDate, endDate) = (endDate, startDate);
        if ((endDate - startDate).TotalDays > 92) endDate = startDate.AddDays(92);
        var vm = new CountsVm
        {
            Start = startDate, End = endDate,
            Meal = ParseInt(meal, minimum: 1), Customer = ParseInt(customer, minimum: 1),
            Tab = tab is "submitted" or "pending" ? tab : "all",
            MealTypes = await schedules.MealTypesAsync(),
            Customers = await db.QueryAsync<AppUser>(
                "SELECT id, name, room_no FROM users WHERE role='customer' AND status='active' ORDER BY room_no, name"),
        };
        vm.Totals = await counts.ScheduleTotalsAsync(startDate, endDate, vm.Meal);

        var filter = "";
        if (vm.Meal is not null) filter += "AND s.meal_type_id=@meal ";
        if (vm.Customer is not null) filter += "AND u.id=@customer ";
        const string submitted = "AND c.submission_status IN ('submitted','locked') ";
        const string pending = "AND (c.id IS NULL OR c.submission_status NOT IN ('submitted','locked')) ";
        var p = new { start = startDate, end = endDate, meal = vm.Meal, customer = vm.Customer };

        vm.TabCounts = new Dictionary<string, int>
        {
            ["all"] = await db.ScalarAsync<int>("SELECT COUNT(*) " + BaseSql + filter, p),
            ["submitted"] = await db.ScalarAsync<int>("SELECT COUNT(*) " + BaseSql + filter + submitted, p),
            ["pending"] = await db.ScalarAsync<int>("SELECT COUNT(*) " + BaseSql + filter + pending, p),
        };
        var tabFilter = vm.Tab switch { "submitted" => submitted, "pending" => pending, _ => "" };
        vm.Pages = Math.Max(1, (vm.TabCounts[vm.Tab] + PerPage - 1) / PerPage);
        vm.Page = Math.Min(ParseInt(page, minimum: 1) ?? 1, vm.Pages);

        vm.Rows = await db.QueryAsync<CountRow>(
            "SELECT u.id AS customer_id, u.name AS customer_name, u.room_no, u.phone, s.id AS schedule_id, s.schedule_date, " +
            "       mt.meal_name, mt.sort_order, f.food_name, s.cutoff_time, c.quantity, c.submission_status, c.updated_at " +
            BaseSql + filter + tabFilter +
            "ORDER BY s.schedule_date, mt.sort_order, u.room_no, u.name LIMIT @limit OFFSET @offset",
            new { p.start, p.end, p.meal, p.customer, limit = PerPage, offset = (vm.Page - 1) * PerPage });
        foreach (var r in vm.Rows) r.Status = Cutoff.CountStatus(r.SubmissionStatus, r.CutoffTime);
        return View(vm);
    }
}
