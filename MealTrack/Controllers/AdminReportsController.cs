using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Admin reports with on-screen tables and CSV / Excel export.</summary>
[Authorize(Roles = Roles.Admin)]
[Route("admin/reports")]
public class AdminReportsController(Db db, CountService counts, ScheduleService schedules, AttendanceService attendance) : AppController
{
    private sealed class PrepDay
    {
        public DateTime D { get; set; }
        public int Required { get; set; }
        public int Prepared { get; set; }
        public int Wasted { get; set; }
    }

    private sealed class CustomerLine
    {
        public string Name { get; set; } = "";
        public string? RoomNo { get; set; }
        public string Status { get; set; } = "";
        public int Submitted { get; set; }
        public int Meals { get; set; }
        public int Breakfast { get; set; }
        public int Lunch { get; set; }
        public int Dinner { get; set; }
        public int Total { get; set; }
        public decimal Cost { get; set; }
        public int NoShows { get; set; }
    }

    private sealed class SellerLine
    {
        public string Name { get; set; } = "";
        public int Meals { get; set; }
        public int Completed { get; set; }
        public int Required { get; set; }
        public int Prepared { get; set; }
        public int Consumed { get; set; }
        public int Wasted { get; set; }
    }

    private static decimal Pct(int part, int whole) => whole == 0 ? 0 : Math.Round(part * 100m / whole, 1);

    [HttpGet("")]
    public async Task<IActionResult> Index(string? type, string? start, string? end, string? format)
    {
        type = ReportsVm.Types.Any(t => t.Key == type) ? type! : "daily";
        var today = DateTime.Today;
        var endDate = ParseDate(end) ?? today;
        var startDate = ParseDate(start) ?? type switch
        {
            "monthly" => new DateTime(endDate.Year, endDate.Month, 1).AddMonths(-5),
            "weekly" => WeekStart(endDate).AddDays(-7 * 7),
            _ => endDate.AddDays(-13),
        };
        if (startDate > endDate) (startDate, endDate) = (endDate, startDate);
        if ((endDate - startDate).TotalDays > 366) startDate = endDate.AddDays(-366);

        var table = type switch
        {
            "customer" => await CustomerReportAsync(startDate, endDate),
            "seller" => await SellerReportAsync(startDate, endDate),
            "wastage" => await WastageReportAsync(startDate, endDate),
            "attendance" => await AttendanceReportAsync(startDate, endDate),
            _ => await ConsumptionReportAsync(type, startDate, endDate),
        };
        table.Key = type;
        table.Description = $"{startDate:dd MMM yyyy} – {endDate:dd MMM yyyy}";

        var fileName = $"mealtrack-{type}-{startDate:yyyyMMdd}-{endDate:yyyyMMdd}";
        if (format == "csv") return File(ToCsv(table), "text/csv", fileName + ".csv");
        if (format == "xlsx")
            return File(ToXlsx(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName + ".xlsx");
        return View(new ReportsVm { Type = type, Start = startDate, End = endDate, Table = table });
    }

    // ---------------- report builders ----------------

    /// <summary>Daily / weekly / monthly: portions per meal plus preparation and wastage.</summary>
    private async Task<ReportTable> ConsumptionReportAsync(string type, DateTime start, DateTime end)
    {
        var meals = await schedules.MealTypesAsync();
        var totals = await counts.ScheduleTotalsAsync(start, end);
        var prep = await db.QueryAsync<PrepDay>(
            "SELECT s.schedule_date AS d, SUM(p.required_quantity) AS required, SUM(p.prepared_quantity) AS prepared, " +
            "SUM(p.wasted_quantity) AS wasted FROM food_preparation p JOIN food_schedule s ON s.id = p.food_schedule_id " +
            "WHERE s.schedule_date BETWEEN @start AND @end GROUP BY s.schedule_date", new { start, end });

        Func<DateTime, DateTime> keyOf = type switch
        {
            "weekly" => WeekStart,
            "monthly" => d => new DateTime(d.Year, d.Month, 1),
            _ => d => d.Date,
        };
        string Label(DateTime k) => type switch
        {
            "weekly" => $"{k:dd MMM} – {k.AddDays(6):dd MMM yyyy}",
            "monthly" => k.ToString("MMMM yyyy"),
            _ => k.ToString("ddd, dd MMM yyyy"),
        };

        var keys = totals.Select(t => keyOf(t.ScheduleDate)).Concat(prep.Select(p => keyOf(p.D))).Distinct().Order().ToList();
        var table = new ReportTable
        {
            Title = type switch { "weekly" => "Weekly consumption", "monthly" => "Monthly consumption", _ => "Daily consumption" },
            Columns = [type switch { "weekly" => "Week", "monthly" => "Month", _ => "Date" },
                       .. meals.Select(m => m.MealName), "Total portions", "Prepared", "Wasted", "Wastage %"],
        };
        var n = meals.Count;
        table.NumericColumns = Enumerable.Range(1, n + 3).ToHashSet();
        foreach (var k in keys)
        {
            var t = totals.Where(r => keyOf(r.ScheduleDate) == k).ToList();
            var p = prep.Where(r => keyOf(r.D) == k).ToList();
            var row = new List<object?> { Label(k) };
            row.AddRange(meals.Select(m => (object?)t.Where(r => r.MealTypeId == m.Id).Sum(r => r.TotalQty)));
            int prepared = p.Sum(r => r.Prepared), wasted = p.Sum(r => r.Wasted);
            row.AddRange([t.Sum(r => r.TotalQty), prepared, wasted, Pct(wasted, prepared)]);
            table.Rows.Add(row.ToArray());
        }
        var footer = new List<object?> { "Total" };
        for (var c = 1; c <= n + 3; c++) footer.Add(table.Rows.Sum(r => Convert.ToInt32(r[c])));
        footer.Add(Pct((int)footer[n + 3]!, (int)footer[n + 2]!));
        table.Footer = footer.ToArray();
        return table;
    }

    private async Task<ReportTable> CustomerReportAsync(DateTime start, DateTime end)
    {
        var rows = await db.QueryAsync<CustomerLine>(
            "SELECT u.name, u.room_no, u.status, " +
            "  COUNT(c.id) AS submitted, " +
            "  (SELECT COUNT(*) FROM food_schedule s2 WHERE s2.is_published=1 AND s2.schedule_date BETWEEN @start AND @end " +
            "     AND s2.cutoff_time <= @now) AS meals, " +
            "  COALESCE(SUM(CASE WHEN mt.meal_name='Breakfast' THEN c.quantity END),0) AS breakfast, " +
            "  COALESCE(SUM(CASE WHEN mt.meal_name='Lunch' THEN c.quantity END),0) AS lunch, " +
            "  COALESCE(SUM(CASE WHEN mt.meal_name='Dinner' THEN c.quantity END),0) AS dinner, " +
            "  COALESCE(SUM(c.quantity),0) AS total, COALESCE(SUM(c.quantity * COALESCE(f.price,0)),0) AS cost, " +
            "  (SELECT COUNT(*) FROM customer_meal_counts c3 JOIN food_schedule s3 ON s3.id = c3.food_schedule_id " +
            "     AND s3.is_published=1 AND s3.schedule_date BETWEEN @start AND @end AND s3.schedule_date < CURDATE() " +
            "   WHERE c3.customer_id = u.id AND c3.submission_status IN ('submitted','locked') AND c3.quantity > 0 " +
            "     AND EXISTS (SELECT 1 FROM meal_attendance x WHERE x.food_schedule_id = s3.id) " +
            "     AND NOT EXISTS (SELECT 1 FROM meal_attendance a3 WHERE a3.food_schedule_id = s3.id AND a3.customer_id = u.id)) AS no_shows " +
            "FROM users u " +
            "LEFT JOIN (customer_meal_counts c " +
            "           JOIN food_schedule s ON s.id = c.food_schedule_id AND s.is_published=1 " +
            "                AND s.schedule_date BETWEEN @start AND @end AND s.cutoff_time <= @now " +
            "           JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "           JOIN food_items f ON f.id = s.food_item_id) " +
            "     ON c.customer_id = u.id AND c.submission_status IN ('submitted','locked') " +
            "WHERE u.role='customer' " +
            "GROUP BY u.id, u.name, u.room_no, u.status ORDER BY u.status, u.room_no, u.name",
            new { start, end, now = DateTime.Now });
        var table = new ReportTable
        {
            Title = "Customer-wise consumption",
            Columns = ["Customer", "Room", "Status", "Meals submitted", "Not submitted", "Breakfast", "Lunch", "Dinner", "Total portions", "Est. cost (₹)", "No-shows (applied, not came)"],
            NumericColumns = [3, 4, 5, 6, 7, 8, 9, 10],
        };
        foreach (var r in rows)
            table.Rows.Add([r.Name, r.RoomNo ?? "", r.Status, r.Submitted, Math.Max(r.Meals - r.Submitted, 0),
                            r.Breakfast, r.Lunch, r.Dinner, r.Total, Math.Round(r.Cost, 2), r.NoShows]);
        table.Footer = ["Total", "", "", rows.Sum(r => r.Submitted), table.Rows.Sum(r => (int)r[4]!),
                        rows.Sum(r => r.Breakfast), rows.Sum(r => r.Lunch), rows.Sum(r => r.Dinner),
                        rows.Sum(r => r.Total), Math.Round(rows.Sum(r => r.Cost), 2), rows.Sum(r => r.NoShows)];
        return table;
    }

    private async Task<ReportTable> SellerReportAsync(DateTime start, DateTime end)
    {
        var rows = await db.QueryAsync<SellerLine>(
            "SELECT u.name, COUNT(p.id) AS meals, COALESCE(SUM(p.preparation_status='completed'),0) AS completed, " +
            "  COALESCE(SUM(p.required_quantity),0) AS required, COALESCE(SUM(p.prepared_quantity),0) AS prepared, " +
            "  COALESCE(SUM(p.consumed_quantity),0) AS consumed, COALESCE(SUM(p.wasted_quantity),0) AS wasted " +
            "FROM users u LEFT JOIN (food_preparation p JOIN food_schedule s ON s.id = p.food_schedule_id " +
            "     AND s.schedule_date BETWEEN @start AND @end) ON p.seller_id = u.id " +
            "WHERE u.role='seller' GROUP BY u.id, u.name ORDER BY u.name", new { start, end });
        var table = new ReportTable
        {
            Title = "Seller preparation",
            Columns = ["Seller", "Meals recorded", "Completed", "Required", "Prepared", "Consumed", "Wasted", "Variance (prepared − required)", "Wastage %"],
            NumericColumns = [1, 2, 3, 4, 5, 6, 7, 8],
        };
        foreach (var r in rows)
            table.Rows.Add([r.Name, r.Meals, r.Completed, r.Required, r.Prepared, r.Consumed, r.Wasted,
                            r.Prepared - r.Required, Pct(r.Wasted, r.Prepared)]);
        int req = rows.Sum(r => r.Required), prep = rows.Sum(r => r.Prepared), waste = rows.Sum(r => r.Wasted);
        table.Footer = ["Total", rows.Sum(r => r.Meals), rows.Sum(r => r.Completed), req, prep,
                        rows.Sum(r => r.Consumed), waste, prep - req, Pct(waste, prep)];
        return table;
    }

    private async Task<ReportTable> WastageReportAsync(DateTime start, DateTime end)
    {
        var rows = await db.QueryAsync<PrepRecord>(
            SellerController.PrepSql + "WHERE s.schedule_date BETWEEN @start AND @end ORDER BY s.schedule_date, mt.sort_order",
            new { start, end });
        var table = new ReportTable
        {
            Title = "Wastage by meal",
            Columns = ["Date", "Meal", "Food", "Seller", "Status", "Required", "Prepared", "Consumed", "Remaining", "Wasted", "Wastage %"],
            NumericColumns = [5, 6, 7, 8, 9, 10],
        };
        foreach (var r in rows)
            table.Rows.Add([r.ScheduleDate.ToString("ddd, dd MMM yyyy"), r.MealName, r.FoodName, r.SellerName,
                            r.PreparationStatus.Replace('_', ' '), r.RequiredQuantity, r.PreparedQuantity,
                            r.ConsumedQuantity, r.RemainingQuantity, r.WastedQuantity, r.WastagePct]);
        int prep = rows.Sum(r => r.PreparedQuantity), waste = rows.Sum(r => r.WastedQuantity);
        table.Footer = ["Total", "", "", "", "", rows.Sum(r => r.RequiredQuantity), prep, rows.Sum(r => r.ConsumedQuantity),
                        rows.Sum(r => r.RemainingQuantity), waste, Pct(waste, prep)];
        return table;
    }

    /// <summary>Applied vs. checked in (QR) per meal. Meals with no scans are shown as "Not tracked".</summary>
    private async Task<ReportTable> AttendanceReportAsync(DateTime start, DateTime end)
    {
        var rows = await attendance.ReportRowsAsync(start, end);
        var table = new ReportTable
        {
            Title = "Attendance & no-shows",
            Columns = ["Date", "Meal", "Food", "Applied customers", "Applied portions", "Came", "No-shows",
                       "No-show portions", "Walk-ins", "Attendance %"],
            NumericColumns = [3, 4, 5, 6, 7, 8, 9],
        };
        foreach (var r in rows)
            table.Rows.Add([r.ScheduleDate.ToString("ddd, dd MMM yyyy"), r.MealName, r.FoodName, r.Applied, r.AppliedPortions,
                            r.Tracked ? r.Came : null, r.Tracked ? r.NoShows : null, r.Tracked ? r.NoShowPortions : null,
                            r.Tracked ? r.WalkIns : null, r.Tracked ? r.AttendancePct : "Not tracked"]);
        var tracked = rows.Where(r => r.Tracked).ToList();
        int applied = tracked.Sum(r => r.Applied), came = tracked.Sum(r => r.Came);
        table.Footer = ["Total (tracked meals)", "", "", applied, tracked.Sum(r => r.AppliedPortions), came,
                        tracked.Sum(r => r.NoShows), tracked.Sum(r => r.NoShowPortions), tracked.Sum(r => r.WalkIns), Pct(came, applied)];
        return table;
    }

    // ---------------- exports ----------------
    private static string Cell(object? v) => v switch
    {
        null => "",
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static byte[] ToCsv(ReportTable t)
    {
        static string Esc(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", t.Columns.Select(Esc)));
        foreach (var row in t.Rows) sb.AppendLine(string.Join(",", row.Select(v => Esc(Cell(v)))));
        if (t.Footer is not null) sb.AppendLine(string.Join(",", t.Footer.Select(v => Esc(Cell(v)))));
        // UTF-8 with BOM so Excel shows ₹ and – correctly
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static byte[] ToXlsx(ReportTable t)
    {
        var green = XLColor.FromHtml("#16A34A");
        var black = XLColor.FromHtml("#171717");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(t.Title.Length > 31 ? t.Title[..31] : t.Title);

        ws.Cell(1, 1).Value = $"MealTrack – {t.Title}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(black);
        ws.Cell(2, 1).Value = t.Description;
        ws.Cell(2, 1).Style.Font.SetFontColor(XLColor.FromHtml("#737373"));

        const int headerRow = 4;
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = t.Columns[c];
            cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(green);
        }

        void Write(int r, object?[] values, bool bold)
        {
            for (var c = 0; c < values.Length; c++)
            {
                var cell = ws.Cell(r, c + 1);
                cell.Value = values[c] switch
                {
                    null => Blank.Value,
                    int i => i,
                    long l => l,
                    decimal d => d,
                    _ => values[c]!.ToString(),
                };
                if (bold) cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(black);
            }
        }

        var rowIndex = headerRow + 1;
        foreach (var row in t.Rows) Write(rowIndex++, row, false);
        if (t.Footer is not null) Write(rowIndex, t.Footer, true);
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
