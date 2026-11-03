using System.Globalization;
using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Admin: weekly schedule grid, week builder, single meal edit, publishing and cutoff times.</summary>
[Authorize(Roles = Roles.Admin)]
[Route("admin")]
public class AdminSchedulesController(Db db, ScheduleService schedules, CountService counts) : AppController
{
    private async Task<ScheduleGridVm> GridAsync(DateTime weekStart)
    {
        var rows = await schedules.RangeAsync(weekStart, weekStart.AddDays(6));
        return new ScheduleGridVm
        {
            WeekStart = weekStart,
            MealTypes = await schedules.MealTypesAsync(),
            Cells = rows.ToDictionary(r => (r.ScheduleDate.Date, r.MealTypeId)),
            ActiveCustomers = await counts.ActiveCustomerCountAsync(),
        };
    }

    private IActionResult BackToWeek(DateTime weekStart) =>
        Redirect($"/admin/schedules?week={weekStart:yyyy-MM-dd}");

    [HttpGet("schedules")]
    public async Task<IActionResult> Index(string? week) =>
        View(await GridAsync(WeekStart(ParseDate(week) ?? DateTime.Today)));

    // ---------------- week builder ----------------
    [HttpGet("schedules/builder")]
    public async Task<IActionResult> Builder(string? week)
    {
        var vm = await GridAsync(WeekStart(ParseDate(week) ?? DateTime.Today));
        vm.Foods = await db.QueryAsync<FoodItem>("SELECT id, food_name, unit, status FROM food_items ORDER BY food_name");
        return View(vm);
    }

    [HttpPost("schedules/builder")]
    public async Task<IActionResult> BuilderSave(string? week, [FromForm(Name = "save_mode")] string? saveMode)
    {
        var ws = WeekStart(ParseDate(week) ?? DateTime.Today);
        var publish = saveMode == "publish";
        var mealTypes = await schedules.MealTypesAsync();
        var activeFoods = (await schedules.ActiveFoodsAsync()).Select(f => f.Id).ToHashSet();
        var existing = (await schedules.RangeAsync(ws, ws.AddDays(6))).ToDictionary(r => (r.ScheduleDate.Date, r.MealTypeId));
        int created = 0, updated = 0, invalid = 0;

        for (var i = 0; i < 7; i++)
        {
            var d = ws.AddDays(i);
            foreach (var m in mealTypes)
            {
                var foodId = ParseInt(Request.Form[$"food_{d:yyyyMMdd}_{m.Id}"], minimum: 1);
                if (foodId is null) continue; // blank = leave this slot as it is
                existing.TryGetValue((d, m.Id), out var cur);
                // Inactive foods can't be newly scheduled, but an existing slot may keep its food.
                if (!activeFoods.Contains(foodId.Value) && cur?.FoodItemId != foodId)
                {
                    invalid++;
                    continue;
                }
                var result = await schedules.SaveSlotAsync(d, m.Id, foodId.Value, null, null, null, Me.Id);
                if (result.Created) created++;
                else if (cur is not null && cur.FoodItemId != foodId) updated++;
            }
        }
        var published = publish ? await schedules.SetPublishedAsync(ws, ws.AddDays(6), true) : 0;

        var parts = new List<string>();
        if (created > 0) parts.Add($"{created} meal(s) added");
        if (updated > 0) parts.Add($"{updated} changed");
        if (published > 0) parts.Add($"{published} published");
        Flash(parts.Count > 0 ? "success" : "info",
            parts.Count > 0 ? $"Week of {ws:dd MMM}: {string.Join(", ", parts)}." : "No changes to save.");
        if (invalid > 0) Flash("warning", $"{invalid} slot(s) skipped because the food item is inactive.");
        if (!publish && created > 0) Flash("info", "New meals are saved as drafts. Customers see them after you publish.");
        return BackToWeek(ws);
    }

    [HttpPost("schedules/copy-previous")]
    public async Task<IActionResult> CopyPrevious(string? week)
    {
        var ws = WeekStart(ParseDate(week) ?? DateTime.Today);
        var copied = await schedules.CopyPreviousWeekAsync(ws, Me.Id);
        Flash(copied > 0 ? "success" : "info", copied > 0
            ? $"Copied {copied} meal(s) from the week of {ws.AddDays(-7):dd MMM} as drafts. Review and publish when ready."
            : "Nothing to copy – every slot this week already has a meal, or last week was empty.");
        return BackToWeek(ws);
    }

    [HttpPost("schedules/publish")]
    public async Task<IActionResult> PublishWeek(string? week, string? publish)
    {
        var ws = WeekStart(ParseDate(week) ?? DateTime.Today);
        var on = publish == "1";
        var n = await schedules.SetPublishedAsync(ws, ws.AddDays(6), on);
        Flash(n > 0 ? "success" : "info", n > 0
            ? (on ? $"Published {n} meal(s). Customers have been notified." : $"Unpublished {n} meal(s). Customers can no longer see them.")
            : "Nothing to change.");
        return BackToWeek(ws);
    }

    // ---------------- single meal ----------------
    private async Task<ScheduleEditVm> EditVmAsync(AdminScheduleRow? s)
    {
        var vm = new ScheduleEditVm
        {
            Schedule = s,
            MealTypes = await schedules.MealTypesAsync(),
            Foods = await db.QueryAsync<FoodItem>("SELECT id, food_name, unit, status FROM food_items ORDER BY food_name"),
        };
        if (s is not null)
        {
            vm.Date = s.ScheduleDate;
            vm.MealTypeId = s.MealTypeId;
            vm.FoodItemId = s.FoodItemId;
            vm.Cutoff = s.CutoffTime.ToString("yyyy-MM-ddTHH:mm");
            vm.UseDefaultCutoff = false;
            vm.Notes = s.Notes ?? "";
            vm.IsPublished = s.IsPublished;
            vm.Sellers = await schedules.SellerOptionsAsync(s.Id);
            vm.CountRows = await schedules.CountsForScheduleAsync(s.Id);
            vm.Breakdown = await counts.CustomerBreakdownAsync(s.Id);
            vm.PendingCustomers = await counts.PendingCustomersAsync(s.Id);
        }
        return vm;
    }

    [HttpGet("schedules/new")]
    public async Task<IActionResult> New(string? date, string? meal)
    {
        var vm = await EditVmAsync(null);
        vm.Date = ParseDate(date) ?? DateTime.Today;
        vm.MealTypeId = ParseInt(meal) ?? vm.MealTypes.FirstOrDefault()?.Id ?? 0;
        var existing = (await schedules.RangeAsync(vm.Date, vm.Date)).FirstOrDefault(r => r.MealTypeId == vm.MealTypeId);
        if (existing is not null) return Redirect($"/admin/schedules/{existing.Id}/edit");
        return View("Edit", vm);
    }

    [HttpPost("schedules/new")]
    public async Task<IActionResult> Create(string? date, [FromForm(Name = "meal_type_id")] string? mealTypeId,
        [FromForm(Name = "food_item_id")] string? foodItemId, string? cutoff,
        [FromForm(Name = "use_default_cutoff")] string? useDefault, string? notes, [FromForm(Name = "is_published")] string? isPublished)
    {
        var d = ParseDate(date);
        var mt = ParseInt(mealTypeId, minimum: 1);
        var food = ParseInt(foodItemId, minimum: 1);
        DateTime? cutoffValue = useDefault == "1" ? null : ParseCutoff(cutoff);
        var errors = new List<string>();
        if (d is null) errors.Add("Choose a valid date.");
        if (mt is null) errors.Add("Choose a meal.");
        if (food is null || await db.ScalarAsync<int>("SELECT COUNT(*) FROM food_items WHERE id=@food AND status='active'", new { food }) == 0)
            errors.Add("Choose an active food item.");
        if (useDefault != "1" && cutoffValue is null) errors.Add("Enter a valid cutoff date and time, or tick “Use default cutoff”.");
        if (d is not null && mt is not null &&
            (await schedules.RangeAsync(d.Value, d.Value)).Any(r => r.MealTypeId == mt))
            errors.Add("That date already has this meal. Edit the existing schedule instead.");
        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            var vm = await EditVmAsync(null);
            vm.Date = d ?? DateTime.Today;
            vm.MealTypeId = mt ?? 0;
            vm.FoodItemId = food ?? 0;
            vm.Cutoff = cutoff ?? "";
            vm.UseDefaultCutoff = useDefault == "1";
            vm.Notes = notes ?? "";
            vm.IsPublished = isPublished == "1";
            return View("Edit", vm);
        }
        var result = await schedules.SaveSlotAsync(d!.Value, mt!.Value, food!.Value, cutoffValue,
            string.IsNullOrWhiteSpace(notes) ? null : Clean(notes, 255), false, Me.Id);
        if (isPublished == "1") await schedules.PublishOneAsync(result.Id, true);
        Flash("success", "Meal scheduled." + (isPublished == "1" ? " Customers have been notified." : " It is a draft until you publish it."));
        return Redirect($"/admin/schedules/{result.Id}/edit");
    }

    private static DateTime? ParseCutoff(string? value) =>
        DateTime.TryParseExact(value, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var v) ? v : null;

    [HttpGet("schedules/{id:int}/edit")]
    public async Task<IActionResult> Edit(int id)
    {
        var s = await schedules.GetAsync(id);
        return s is null ? NotFound() : View(await EditVmAsync(s));
    }

    [HttpPost("schedules/{id:int}/edit")]
    public async Task<IActionResult> Update(int id, [FromForm(Name = "food_item_id")] string? foodItemId, string? cutoff,
        [FromForm(Name = "use_default_cutoff")] string? useDefault, string? notes, [FromForm(Name = "is_published")] string? isPublished)
    {
        var s = await schedules.GetAsync(id);
        if (s is null) return NotFound();
        var food = ParseInt(foodItemId, minimum: 1);
        var foodOk = food is not null && (food == s.FoodItemId ||
            await db.ScalarAsync<int>("SELECT COUNT(*) FROM food_items WHERE id=@food AND status='active'", new { food }) > 0);
        DateTime? cutoffValue;
        if (useDefault == "1")
        {
            var meal = (await schedules.MealTypesAsync()).First(m => m.Id == s.MealTypeId);
            cutoffValue = Cutoff.Compute(s.ScheduleDate, meal);
        }
        else
        {
            cutoffValue = ParseCutoff(cutoff);
        }
        if (!foodOk || cutoffValue is null)
        {
            if (!foodOk) Flash("danger", "Choose an active food item.");
            if (cutoffValue is null) Flash("danger", "Enter a valid cutoff date and time.");
            return Redirect($"/admin/schedules/{id}/edit");
        }

        var publish = isPublished == "1";
        var result = await schedules.SaveSlotAsync(s.ScheduleDate, s.MealTypeId, food!.Value, cutoffValue,
            Clean(notes, 255), null, Me.Id);
        if (publish != s.IsPublished) await schedules.PublishOneAsync(id, publish);

        // Seller assignments: seller_ids (checked) + view_{id}
        var sellerIds = Request.Form["seller_ids"].Select(v => ParseInt(v, minimum: 1)).Where(v => v is not null)
            .Select(v => v!.Value).Distinct().ToList();
        await schedules.SetAssignmentsAsync(id, sellerIds.Select(sid => (sid, Request.Form[$"view_{sid}"] == "1")).ToList());

        Flash("success", result.Changed
            ? "Schedule updated. Customers who already submitted and the assigned sellers were notified."
            : "Schedule updated.");
        return Redirect($"/admin/schedules/{id}/edit");
    }

    [HttpPost("schedules/{id:int}/toggle-publish")]
    public async Task<IActionResult> TogglePublish(int id)
    {
        var s = await schedules.GetAsync(id);
        if (s is null) return NotFound();
        await schedules.PublishOneAsync(id, !s.IsPublished);
        Flash("success", $"{s.MealName} on {s.ScheduleDate:ddd dd MMM} " + (s.IsPublished ? "unpublished." : "published. Customers have been notified."));
        return BackToWeek(WeekStart(s.ScheduleDate));
    }

    [HttpPost("schedules/{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, string? confirm)
    {
        var s = await schedules.GetAsync(id);
        if (s is null) return NotFound();
        var rows = await schedules.CountsForScheduleAsync(id);
        if (rows > 0 && confirm != "1")
        {
            Flash("warning", $"This meal has {rows} customer count(s). Tick the confirmation box to delete it together with those counts.");
            return Redirect($"/admin/schedules/{id}/edit");
        }
        if (s.IsPublished && rows > 0)
        {
            var customers = await db.QueryAsync<int>(
                "SELECT customer_id FROM customer_meal_counts WHERE food_schedule_id=@id AND submission_status IN ('submitted','locked')",
                new { id });
            await HttpContext.RequestServices.GetRequiredService<NotificationService>().NotifyManyAsync(customers,
                $"{s.MealName} on {s.ScheduleDate:ddd dd MMM} ({s.FoodName}) has been cancelled by the admin.", "/customer/schedule");
        }
        await schedules.DeleteAsync(id);
        Flash("success", $"{s.MealName} on {s.ScheduleDate:ddd dd MMM} deleted.");
        return BackToWeek(WeekStart(s.ScheduleDate));
    }

    // ---------------- cutoff times ----------------
    [HttpGet("cutoffs")]
    public async Task<IActionResult> Cutoffs() => View(new CutoffsVm { MealTypes = await schedules.MealTypesAsync() });

    [HttpPost("cutoffs")]
    public async Task<IActionResult> CutoffsSave([FromForm(Name = "apply_upcoming")] string? applyUpcoming)
    {
        var mealTypes = await schedules.MealTypesAsync();
        var applied = 0;
        foreach (var m in mealTypes)
        {
            var offset = ParseInt(Request.Form[$"offset_{m.Id}"], minimum: -7, maximum: 0);
            if (offset is null || !TimeSpan.TryParseExact(Request.Form[$"time_{m.Id}"].ToString(), @"hh\:mm",
                    CultureInfo.InvariantCulture, out var time))
            {
                Flash("danger", $"Invalid cutoff for {m.MealName}.");
                return Redirect("/admin/cutoffs");
            }
            await db.ExecuteAsync(
                "UPDATE meal_types SET default_cutoff_day_offset=@offset, default_cutoff_time=@time WHERE id=@id",
                new { offset, time, id = m.Id });
            if (applyUpcoming == "1")
            {
                // Only meals that are still open, and only if the new cutoff is still in the future.
                applied += await db.ExecuteAsync(
                    "UPDATE food_schedule SET cutoff_time = TIMESTAMP(DATE_ADD(schedule_date, INTERVAL @offset DAY), @time) " +
                    "WHERE meal_type_id=@id AND cutoff_time > @now " +
                    "  AND TIMESTAMP(DATE_ADD(schedule_date, INTERVAL @offset DAY), @time) > @now",
                    new { offset, time, id = m.Id, now = DateTime.Now });
            }
        }
        Flash("success", "Default cutoff times saved." +
            (applyUpcoming == "1" ? $" {applied} open meal(s) were updated to the new rule." : " They apply to meals created from now on."));
        return Redirect("/admin/cutoffs");
    }
}
