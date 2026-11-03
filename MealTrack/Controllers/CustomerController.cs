using Dapper;
using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Customer module: dashboard, weekly schedule, count submission, history.</summary>
[Authorize(Roles = Roles.Customer)]
[Route("customer")]
public class CustomerController(Db db, NotificationService notifications, IOptions<AppOptions> options) : AppController
{
    private Task<List<MealType>> MealTypesAsync() =>
        db.QueryAsync<MealType>("SELECT id, meal_name, sort_order FROM meal_types ORDER BY sort_order");

    /// <summary>
    /// Published schedule rows in a date range joined with this customer's counts
    /// and last week's quantity for the same meal (for "copy last week").
    /// </summary>
    private async Task<List<MyScheduleRow>> MyScheduleAsync(DateTime start, DateTime end, int customerId)
    {
        var rows = await db.QueryAsync<MyScheduleRow>(
            "SELECT s.id, s.schedule_date, s.cutoff_time, s.notes, mt.id AS meal_type_id, mt.meal_name, " +
            "       mt.sort_order, f.food_name, f.description, f.unit, f.price, " +
            "       c.quantity, c.submission_status, c.submitted_at, c.updated_at, " +
            "       lw.quantity AS last_week_qty " +
            "FROM food_schedule s " +
            "JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "JOIN food_items f  ON f.id = s.food_item_id " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = @customerId " +
            "LEFT JOIN food_schedule s2 ON s2.schedule_date = DATE_SUB(s.schedule_date, INTERVAL 7 DAY) " +
            "     AND s2.meal_type_id = s.meal_type_id " +
            "LEFT JOIN customer_meal_counts lw ON lw.food_schedule_id = s2.id AND lw.customer_id = @customerId " +
            "     AND lw.submission_status IN ('submitted','locked') " +
            "WHERE s.is_published = 1 AND s.schedule_date BETWEEN @start AND @end " +
            "ORDER BY s.schedule_date, mt.sort_order",
            new { customerId, start = start.Date, end = end.Date });
        var now = DateTime.Now;
        foreach (var r in rows)
        {
            r.Status = Cutoff.CountStatus(r.SubmissionStatus, r.CutoffTime, now);
            r.IsOpen = r.CutoffTime > now;
            r.HasValue = r.SubmissionStatus is "submitted" or "locked";
        }
        return rows;
    }

    private static List<IGrouping<DateTime, MyScheduleRow>> GroupByDate(IEnumerable<MyScheduleRow> rows) =>
        rows.GroupBy(r => r.ScheduleDate).ToList();

    [HttpGet("")]
    public async Task<IActionResult> Dashboard()
    {
        var uid = Me.Id;
        var today = DateTime.Today;
        var ws = WeekStart(today);
        var todayRows = await MyScheduleAsync(today, today, uid);
        var weekRows = await MyScheduleAsync(ws, ws.AddDays(6), uid);
        var now = DateTime.Now;

        var nextDeadline = await db.QueryOneAsync<DeadlineRow>(
            "SELECT s.id, s.schedule_date, s.cutoff_time, mt.meal_name, f.food_name, c.submission_status " +
            "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "JOIN food_items f ON f.id = s.food_item_id " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = @uid " +
            "WHERE s.is_published = 1 AND s.cutoff_time > @now " +
            "ORDER BY s.cutoff_time LIMIT 1",
            new { uid, now });
        var upcomingPending = await db.QueryAsync<DeadlineRow>(
            "SELECT s.id, s.schedule_date, s.cutoff_time, mt.meal_name, f.food_name " +
            "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "JOIN food_items f ON f.id = s.food_item_id " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = @uid " +
            "     AND c.submission_status IN ('submitted','locked') " +
            "WHERE s.is_published = 1 AND s.cutoff_time > @now AND c.id IS NULL " +
            "ORDER BY s.cutoff_time LIMIT 6",
            new { uid, now });

        return View(new CustomerDashboardVm
        {
            TodayRows = todayRows,
            WeekTotalMeals = weekRows.Count,
            WeekSubmitted = weekRows.Count(r => r.HasValue),
            WeekPortions = weekRows.Where(r => r.HasValue).Sum(r => r.Quantity ?? 0),
            OpenUnsubmitted = weekRows.Count(r => r.IsOpen && !r.HasValue),
            NextDeadline = nextDeadline,
            UpcomingPending = upcomingPending,
            WeekStart = ws,
        });
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule(string? week)
    {
        var ws = WeekStart(ParseDate(week) ?? DateTime.Today);
        var we = ws.AddDays(6);
        var rows = await MyScheduleAsync(ws, we, Me.Id);
        var grid = Enumerable.Range(0, 7)
            .Select(i => (Day: ws.AddDays(i), Meals: new Dictionary<string, MyScheduleRow>()))
            .ToList();
        foreach (var r in rows)
            grid.First(g => g.Day == r.ScheduleDate).Meals[r.MealName] = r;

        return View(new ScheduleWeekVm
        {
            Grid = grid, MealTypes = await MealTypesAsync(), WeekStart = ws, WeekEnd = we,
            PrevWeek = ws.AddDays(-7), NextWeek = ws.AddDays(7),
        });
    }

    private async Task<DateTime> DefaultSubmitDateAsync(int uid)
    {
        var d = await db.ScalarAsync<DateTime?>(
            "SELECT MIN(s.schedule_date) FROM food_schedule s " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = @uid " +
            "     AND c.submission_status IN ('submitted','locked') " +
            "WHERE s.is_published = 1 AND s.cutoff_time > @now AND c.id IS NULL",
            new { uid, now = DateTime.Now });
        return d ?? DateTime.Today;
    }

    [HttpGet("submit")]
    public async Task<IActionResult> Submit(string? date, string? view)
    {
        var uid = Me.Id;
        view = view == "week" ? "week" : "day";
        var selected = ParseDate(date) ?? await DefaultSubmitDateAsync(uid);
        DateTime start, end;
        if (view == "week")
        {
            start = WeekStart(selected);
            end = start.AddDays(6);
        }
        else
        {
            start = end = selected;
        }
        var rows = await MyScheduleAsync(start, end, uid);
        var step = view == "week" ? 7 : 1;
        return View(new SubmitVm
        {
            Grouped = GroupByDate(rows), View = view, Selected = selected, Start = start, End = end,
            AnyOpen = rows.Any(r => r.IsOpen), MaxQty = options.Value.MaxQtyPerMeal,
            PrevDate = selected.AddDays(-step), NextDate = selected.AddDays(step),
        });
    }

    private sealed class LockedSchedule
    {
        public int Id { get; set; }
        public DateTime ScheduleDate { get; set; }
        public DateTime CutoffTime { get; set; }
        public string MealName { get; set; } = "";
    }

    private sealed class ExistingCount
    {
        public int Id { get; set; }
        public int Quantity { get; set; }
        public string SubmissionStatus { get; set; } = "";
    }

    [HttpPost("submit")]
    public async Task<IActionResult> SubmitPost()
    {
        var uid = Me.Id;
        var maxQty = options.Value.MaxQtyPerMeal;
        var form = Request.Form;
        var view = form["view"] == "week" ? "week" : "day";
        var backDate = ParseDate(form["date"]) ?? DateTime.Today;
        var now = DateTime.Now;
        int unchanged = 0, rejected = 0, invalid = 0;

        var entries = new List<(int ScheduleId, int Qty)>();
        foreach (var (key, values) in form)
        {
            if (!key.StartsWith("qty_")) continue;
            var scheduleId = ParseInt(key[4..], minimum: 1);
            var raw = (values.ToString() ?? "").Trim();
            if (scheduleId is null || raw == "") continue; // blank = not submitted (stays pending)
            var qty = ParseInt(raw, minimum: 0, maximum: maxQty);
            if (qty is null)
            {
                invalid++;
                continue;
            }
            entries.Add((scheduleId.Value, qty.Value));
        }

        var submitted = await db.InTransactionAsync(async (conn, tx) =>
        {
            var saved = new List<(LockedSchedule Sched, int Qty)>();
            foreach (var (scheduleId, qty) in entries)
            {
                // Lock the schedule row and re-check the cutoff on the server.
                var sched = await conn.QueryFirstOrDefaultAsync<LockedSchedule>(
                    "SELECT s.id, s.schedule_date, s.cutoff_time, mt.meal_name FROM food_schedule s " +
                    "JOIN meal_types mt ON mt.id = s.meal_type_id " +
                    "WHERE s.id=@scheduleId AND s.is_published=1 FOR UPDATE",
                    new { scheduleId }, tx);
                if (sched is null || sched.CutoffTime <= now)
                {
                    rejected++;
                    continue;
                }
                var existing = await conn.QueryFirstOrDefaultAsync<ExistingCount>(
                    "SELECT id, quantity, submission_status FROM customer_meal_counts " +
                    "WHERE customer_id=@uid AND food_schedule_id=@scheduleId FOR UPDATE",
                    new { uid, scheduleId }, tx);
                if (existing is null)
                {
                    var countId = await conn.ExecuteScalarAsync<long>(
                        "INSERT INTO customer_meal_counts (customer_id, food_schedule_id, quantity, " +
                        "submission_status, submitted_at, updated_at) VALUES (@uid, @scheduleId, @qty, 'submitted', @now, @now); " +
                        "SELECT LAST_INSERT_ID();",
                        new { uid, scheduleId, qty, now }, tx);
                    await conn.ExecuteAsync(
                        "INSERT INTO count_change_log (count_id, old_qty, new_qty, changed_at) VALUES (@countId, NULL, @qty, @now)",
                        new { countId, qty, now }, tx);
                }
                else if (existing.SubmissionStatus is "locked" or "cancelled")
                {
                    rejected++;
                    continue;
                }
                else if (existing.SubmissionStatus == "submitted" && existing.Quantity == qty)
                {
                    unchanged++;
                    continue;
                }
                else
                {
                    await conn.ExecuteAsync(
                        "UPDATE customer_meal_counts SET quantity=@qty, submission_status='submitted', " +
                        "submitted_at=COALESCE(submitted_at, @now), updated_at=@now WHERE id=@id",
                        new { qty, now, id = existing.Id }, tx);
                    await conn.ExecuteAsync(
                        "INSERT INTO count_change_log (count_id, old_qty, new_qty, changed_at) VALUES (@id, @oldQty, @qty, @now)",
                        new { id = existing.Id, oldQty = existing.Quantity, qty, now }, tx);
                }
                saved.Add((sched, qty));
            }
            return saved;
        });

        if (submitted.Count > 0)
        {
            var summary = string.Join(", ",
                submitted.Take(6).Select(s => $"{s.Sched.ScheduleDate:ddd dd MMM} {s.Sched.MealName}: {s.Qty}"));
            if (submitted.Count > 6) summary += $" and {submitted.Count - 6} more";
            await notifications.NotifyAsync(uid, $"Food count saved – {summary}.", "/customer/submissions");
            await notifications.NotifySellersOfUpdatesAsync(submitted.Select(s => s.Sched.Id).Distinct());
            Flash("success", $"Saved {submitted.Count} meal count(s). You can edit them until each cutoff.");
        }
        if (unchanged > 0 && submitted.Count == 0)
            Flash("info", "No changes – your counts were already submitted.");
        if (rejected > 0)
            Flash("warning", $"{rejected} meal(s) were not saved because the cutoff time has passed.");
        if (invalid > 0)
            Flash("danger", $"{invalid} value(s) were invalid. Enter a whole number from 0 to {maxQty}.");
        if (submitted.Count == 0 && unchanged == 0 && rejected == 0 && invalid == 0)
            Flash("info", "Enter a quantity (0 if you don't need the meal) for at least one meal.");

        return RedirectToAction(nameof(Submit), new { date = backDate.ToString("yyyy-MM-dd"), view });
    }

    [HttpGet("submissions")]
    public async Task<IActionResult> Submissions(string? end)
    {
        var today = DateTime.Today;
        var endDate = ParseDate(end) ?? today.AddDays(13);
        var rows = await MyScheduleAsync(today, endDate, Me.Id);
        var counts = new Dictionary<string, int> { ["pending"] = 0, ["submitted"] = 0, ["locked"] = 0, ["missed"] = 0 };
        foreach (var r in rows) counts[r.Status] = counts.GetValueOrDefault(r.Status) + 1;
        return View(new SubmissionsVm { Grouped = GroupByDate(rows), Counts = counts, End = endDate });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(string? start, string? end, string? meal)
    {
        var today = DateTime.Today;
        var endDate = ParseDate(end) ?? today;
        var startDate = ParseDate(start) ?? endDate.AddDays(-29);
        if (startDate > endDate) (startDate, endDate) = (endDate, startDate);
        var mealFilter = ParseInt(meal, minimum: 1);
        var uid = Me.Id;

        var sql =
            "SELECT s.schedule_date, s.cutoff_time, mt.meal_name, mt.sort_order, f.food_name, " +
            "       c.quantity, c.submission_status, c.submitted_at, c.updated_at, " +
            "       (SELECT COUNT(*) FROM count_change_log l WHERE l.count_id = c.id) AS changes " +
            "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "JOIN food_items f ON f.id = s.food_item_id " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = @uid " +
            "WHERE s.is_published = 1 AND s.schedule_date BETWEEN @startDate AND @endDate AND s.cutoff_time <= @now ";
        if (mealFilter is not null) sql += "AND s.meal_type_id = @mealFilter ";
        sql += "ORDER BY s.schedule_date DESC, mt.sort_order";
        var rows = await db.QueryAsync<HistoryRow>(sql, new { uid, startDate, endDate, now = DateTime.Now, mealFilter });
        foreach (var r in rows) r.Status = Cutoff.CountStatus(r.SubmissionStatus, r.CutoffTime);

        var mealTypes = await MealTypesAsync();
        var mealTotals = mealTypes
            .Select(m => (m.MealName, rows.Where(r => r.MealName == m.MealName && r.Status == "locked").Sum(r => r.Quantity ?? 0)))
            .ToList();

        var monthly = await db.QueryAsync<MonthlyRow>(
            "SELECT DATE_FORMAT(s.schedule_date, '%Y-%m') AS ym, " +
            "       DATE_FORMAT(MIN(s.schedule_date), '%b %Y') AS label, " +
            "       SUM(CASE WHEN mt.meal_name='Breakfast' THEN c.quantity ELSE 0 END) AS breakfast, " +
            "       SUM(CASE WHEN mt.meal_name='Lunch' THEN c.quantity ELSE 0 END) AS lunch, " +
            "       SUM(CASE WHEN mt.meal_name='Dinner' THEN c.quantity ELSE 0 END) AS dinner, " +
            "       SUM(c.quantity) AS total " +
            "FROM customer_meal_counts c JOIN food_schedule s ON s.id = c.food_schedule_id " +
            "JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "WHERE c.customer_id = @uid AND c.submission_status IN ('submitted','locked') " +
            "  AND s.schedule_date <= @today " +
            "GROUP BY ym ORDER BY ym DESC LIMIT 6",
            new { uid, today });

        return View(new HistoryVm
        {
            Rows = rows, Start = startDate, End = endDate, MealFilter = mealFilter, MealTypes = mealTypes,
            MealTotals = mealTotals, Missed = rows.Count(r => r.Status == "missed"), Monthly = monthly,
        });
    }
}
