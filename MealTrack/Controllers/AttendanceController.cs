using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>
/// Customer side of meal check-in: scan the QR shown at the counter (or type its 6-digit code).
/// </summary>
[Authorize(Roles = Roles.Customer)]
public class CheckInController(Db db, AttendanceService attendance) : AppController
{
    /// <summary>
    /// Scanning the meal's QR marks the customer as having eaten that meal straight away
    /// (no extra button). Scanning again does not count twice. Only works on the meal's own day.
    /// </summary>
    [HttpGet("/scan/{token}")]
    public async Task<IActionResult> Scan(string token)
    {
        var now = DateTime.Now;
        var meal = await attendance.VerifyTokenAsync(token);
        var vm = new ScanVm { Meal = meal };
        if (meal is null) vm.State = "invalid";
        else if (meal.ScheduleDate.Date != now.Date) vm.State = "wrong_day";
        else
        {
            var result = await attendance.CheckInAsync(meal.Id, Me.Id, "qr", null, now);
            vm.State = result.Created ? "done" : "already";
            vm.CheckedInAt = result.CheckedInAt;
            vm.AppliedQty = result.AppliedQty;
        }
        return View(vm);
    }

    [HttpGet("/check-in")]
    public async Task<IActionResult> Index() => View(await PageAsync());

    [HttpPost("/check-in")]
    public async Task<IActionResult> Code(string? code)
    {
        var now = DateTime.Now;
        var meal = await attendance.MatchCodeAsync(code, now);
        if (meal is null)
        {
            Flash("danger", "That code does not match any of today's meals. Check the 6 digits under the QR.");
            return Redirect("/check-in");
        }
        var result = await attendance.CheckInAsync(meal.Id, Me.Id, "code", null, now);
        if (!result.Created)
            Flash("info", $"You already checked in for {meal.MealName} at {result.CheckedInAt:hh:mm tt}.");
        else if (result.AppliedQty == 0)
            Flash("warning", $"Checked in for {meal.MealName}, but you did not apply for this meal. Please submit your count next time.");
        else
            Flash("success", $"Checked in for {meal.MealName} ({meal.FoodName}). Enjoy your meal!");
        return Redirect("/check-in");
    }

    private async Task<CheckInPageVm> PageAsync()
    {
        var meals = await attendance.PublishedMealsOnAsync(DateTime.Today);
        var mine = await db.QueryAsync<AttendanceLine>(
            "SELECT s.id AS customer_id, COALESCE(c.quantity,0) AS quantity, a.checked_in_at, a.method " +
            "FROM food_schedule s " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id=s.id AND c.customer_id=@me AND c.submission_status IN ('submitted','locked') " +
            "LEFT JOIN meal_attendance a ON a.food_schedule_id=s.id AND a.customer_id=@me " +
            "WHERE s.schedule_date=@today AND s.is_published=1", new { me = Me.Id, today = DateTime.Today });
        // CustomerId carries the schedule id here
        return new CheckInPageVm { TodayMeals = meals, Mine = mine.ToDictionary(m => m.CustomerId) };
    }
}

/// <summary>
/// Counter screen and attendance lists for admins (every meal) and sellers (their assigned meals).
/// </summary>
[Authorize(Roles = Roles.Admin + "," + Roles.Seller)]
[Route("attendance")]
public class AttendanceController(Db db, AttendanceService attendance, IOptions<AppOptions> options) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date)
    {
        var selected = ParseDate(date) ?? DateTime.Today;
        var rows = await attendance.ReportRowsAsync(selected, selected, Me.Role == Roles.Seller ? Me.Id : null);
        return View(new AttendanceIndexVm { Selected = selected, Rows = rows });
    }

    private async Task<MealInfo?> AllowedMealAsync(int scheduleId)
    {
        var meal = await attendance.MealAsync(scheduleId);
        return meal is not null && await attendance.CanManageAsync(Me, scheduleId) ? meal : null;
    }

    private string BaseUrl => options.Value.PublicBaseUrl is { Length: > 0 } url ? url : $"{Request.Scheme}://{Request.Host}";

    [HttpGet("{scheduleId:int}")]
    public async Task<IActionResult> Live(int scheduleId)
    {
        var meal = await AllowedMealAsync(scheduleId);
        if (meal is null) return StatusCode(StatusCodes.Status403Forbidden);
        var vm = new AttendanceLiveVm
        {
            Meal = meal,
            Qr = await attendance.QrAsync(meal, BaseUrl),
            Summary = await attendance.SummaryAsync(scheduleId),
            IsToday = meal.ScheduleDate.Date == DateTime.Today,
        };
        var host = Request.Host.Host;
        if (options.Value.PublicBaseUrl is not { Length: > 0 } && (host == "localhost" || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)))
            vm.LanUrls = LanAddresses().Select(a => $"{Request.Scheme}://{a}:{Request.Host.Port ?? 80}{Request.Path}").ToList();
        return View(vm);
    }

    /// <summary>Polled by the counter screen for live numbers (the QR itself stays the same all day).</summary>
    [HttpGet("{scheduleId:int}/live")]
    public async Task<IActionResult> LiveData(int scheduleId)
    {
        var meal = await AllowedMealAsync(scheduleId);
        if (meal is null) return StatusCode(StatusCodes.Status403Forbidden);
        var s = await attendance.SummaryAsync(scheduleId);
        var recent = await attendance.RecentAsync(scheduleId, 8);
        return Json(new
        {
            stats = new { s.AppliedCustomers, s.AppliedPortions, s.Came, s.NoShows, s.NoShowPortions, s.WalkIns, s.AttendancePct },
            recent = recent.Select(r => new { r.Name, r.RoomNo, time = r.CheckedInAt!.Value.ToString("hh:mm tt"), r.WalkIn, r.Method }),
        });
    }

    [HttpGet("{scheduleId:int}/list")]
    public async Task<IActionResult> List(int scheduleId, string? filter)
    {
        var meal = await AllowedMealAsync(scheduleId);
        if (meal is null) return StatusCode(StatusCodes.Status403Forbidden);
        var lines = await attendance.LinesAsync(scheduleId);
        filter = filter is "came" or "noshow" or "walkin" ? filter : "all";
        return View(new AttendanceListVm
        {
            Meal = meal,
            Summary = AttendanceSummary.From(lines),
            Lines = filter switch
            {
                "came" => lines.Where(l => l.Came).ToList(),
                "noshow" => lines.Where(l => l.Applied && !l.Came).ToList(),
                "walkin" => lines.Where(l => l.WalkIn).ToList(),
                _ => lines,
            },
            Filter = filter,
            CanMark = meal.ScheduleDate.Date <= DateTime.Today,
        });
    }

    /// <summary>Staff marks a customer who has no phone (or undoes a mistaken check-in).</summary>
    [HttpPost("{scheduleId:int}/mark")]
    public async Task<IActionResult> Mark(int scheduleId, int customerId, string? undo, string? filter)
    {
        var meal = await AllowedMealAsync(scheduleId);
        if (meal is null) return StatusCode(StatusCodes.Status403Forbidden);
        if (meal.ScheduleDate.Date > DateTime.Today)
            Flash("warning", "Check-ins can be marked on the day of the meal.");
        else if (undo == "1")
        {
            await attendance.UndoAsync(scheduleId, customerId);
            Flash("info", "Check-in removed.");
        }
        else
        {
            var isCustomer = await db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM users WHERE id=@customerId AND role='customer' AND status='active'", new { customerId }) > 0;
            if (!isCustomer) return BadRequest();
            await attendance.CheckInAsync(scheduleId, customerId, "manual", Me.Id, DateTime.Now);
            Flash("success", "Marked as checked in.");
        }
        return Redirect($"/attendance/{scheduleId}/list?filter={filter ?? "all"}");
    }

    /// <summary>The same QR on a printable sheet (for counters without a screen).</summary>
    [HttpGet("{scheduleId:int}/print")]
    public async Task<IActionResult> Print(int scheduleId)
    {
        var meal = await AllowedMealAsync(scheduleId);
        if (meal is null) return StatusCode(StatusCodes.Status403Forbidden);
        var qr = await attendance.QrAsync(meal, BaseUrl);
        return View(new AttendancePrintVm { Meal = meal, ScanUrl = qr.ScanUrl, Svg = qr.Svg, Code = qr.Code });
    }

    [HttpPost("{scheduleId:int}/regenerate")]
    public async Task<IActionResult> Regenerate(int scheduleId)
    {
        var meal = await AllowedMealAsync(scheduleId);
        if (meal is null) return StatusCode(StatusCodes.Status403Forbidden);
        await attendance.RegenerateAsync(scheduleId);
        Flash("success", "New QR created. Any old or printed QR for this meal no longer works.");
        return Redirect($"/attendance/{scheduleId}");
    }

    private static IEnumerable<string> LanAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."))
        .Select(a => a.ToString())
        .Distinct();
}
