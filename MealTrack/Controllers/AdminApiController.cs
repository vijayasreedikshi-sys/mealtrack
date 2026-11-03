using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MealTrack.Controllers;

/// <summary>JSON for the Chart.js charts on the admin dashboard. All numbers come from the database.</summary>
[Authorize(Roles = Roles.Admin)]
[Route("admin/api")]
public class AdminApiController(Db db, CountService counts) : Controller
{
    private sealed class DayValue
    {
        public DateTime D { get; set; }
        public decimal Required { get; set; }
        public decimal Prepared { get; set; }
        public decimal Wasted { get; set; }
    }

    private sealed class LabelValue
    {
        public string Label { get; set; } = "";
        public decimal Value { get; set; }
    }

    /// <summary>Portions per day for the last N days (incl. today), split by meal.</summary>
    [HttpGet("consumption")]
    public async Task<IActionResult> Consumption(int days = 7)
    {
        days = Math.Clamp(days, 1, 60);
        var end = DateTime.Today;
        var start = end.AddDays(-(days - 1));
        var rows = await counts.ScheduleTotalsAsync(start, end);
        var dates = Enumerable.Range(0, days).Select(i => start.AddDays(i)).ToList();
        var meals = rows.OrderBy(r => r.SortOrder).Select(r => r.MealName).Distinct().ToList();
        return Json(new
        {
            labels = dates.Select(d => d.ToString("ddd dd")),
            datasets = meals.Select(m => new
            {
                label = m,
                data = dates.Select(d => rows.Where(r => r.ScheduleDate == d && r.MealName == m).Sum(r => r.TotalQty)),
            }),
        });
    }

    [HttpGet("meal-split")]
    public async Task<IActionResult> MealSplit(int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var rows = await counts.ScheduleTotalsAsync(DateTime.Today.AddDays(-(days - 1)), DateTime.Today);
        var split = rows.GroupBy(r => (r.SortOrder, r.MealName)).OrderBy(g => g.Key.SortOrder)
            .Select(g => new { label = g.Key.MealName, value = g.Sum(r => r.TotalQty) }).ToList();
        return Json(new { labels = split.Select(s => s.label), data = split.Select(s => s.value) });
    }

    [HttpGet("top-foods")]
    public async Task<IActionResult> TopFoods(int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        var rows = await db.QueryAsync<LabelValue>(
            "SELECT f.food_name AS label, SUM(c.quantity) AS value " +
            "FROM customer_meal_counts c " +
            "JOIN users u ON u.id = c.customer_id AND u.status='active' AND u.role='customer' " +
            "JOIN food_schedule s ON s.id = c.food_schedule_id AND s.is_published = 1 " +
            "JOIN food_items f ON f.id = s.food_item_id " +
            "WHERE c.submission_status IN ('submitted','locked') AND s.schedule_date BETWEEN @start AND @end " +
            "GROUP BY f.food_name ORDER BY value DESC LIMIT 8",
            new { start = DateTime.Today.AddDays(-(days - 1)), end = DateTime.Today });
        return Json(new { labels = rows.Select(r => r.Label), data = rows.Select(r => r.Value) });
    }

    private Task<List<DayValue>> PrepByDayAsync(DateTime start, DateTime end) => db.QueryAsync<DayValue>(
        "SELECT s.schedule_date AS d, SUM(p.required_quantity) AS required, SUM(p.prepared_quantity) AS prepared, " +
        "       SUM(p.wasted_quantity) AS wasted " +
        "FROM food_preparation p JOIN food_schedule s ON s.id = p.food_schedule_id " +
        "WHERE s.schedule_date BETWEEN @start AND @end GROUP BY s.schedule_date ORDER BY s.schedule_date",
        new { start, end });

    /// <summary>Required vs prepared vs wasted per day.</summary>
    [HttpGet("preparation")]
    public async Task<IActionResult> Preparation(int days = 14)
    {
        days = Math.Clamp(days, 1, 90);
        var start = DateTime.Today.AddDays(-(days - 1));
        var rows = (await PrepByDayAsync(start, DateTime.Today)).ToDictionary(r => r.D.Date);
        var dates = Enumerable.Range(0, days).Select(i => start.AddDays(i)).ToList();
        return Json(new
        {
            labels = dates.Select(d => d.ToString("dd MMM")),
            required = dates.Select(d => rows.TryGetValue(d, out var r) ? r.Required : 0),
            prepared = dates.Select(d => rows.TryGetValue(d, out var r) ? r.Prepared : 0),
            wasted = dates.Select(d => rows.TryGetValue(d, out var r) ? r.Wasted : 0),
        });
    }

    /// <summary>Daily wastage % (wasted / prepared × 100); null on days without preparation records.</summary>
    [HttpGet("wastage")]
    public async Task<IActionResult> Wastage(int days = 30)
    {
        days = Math.Clamp(days, 1, 180);
        var start = DateTime.Today.AddDays(-(days - 1));
        var rows = (await PrepByDayAsync(start, DateTime.Today)).ToDictionary(r => r.D.Date);
        var dates = Enumerable.Range(0, days).Select(i => start.AddDays(i)).ToList();
        return Json(new
        {
            labels = dates.Select(d => d.ToString("dd MMM")),
            data = dates.Select(d => rows.TryGetValue(d, out var r) && r.Prepared > 0
                ? (decimal?)Math.Round(r.Wasted * 100m / r.Prepared, 1) : null),
        });
    }
}
