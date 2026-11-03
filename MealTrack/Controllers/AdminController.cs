using System.Globalization;
using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Admin module: dashboard and food item (menu) management.</summary>
[Authorize(Roles = Roles.Admin)]
[Route("admin")]
public class AdminController(Db db, CountService counts) : AppController
{
    private static readonly string[] FoodUnits = ["plate", "piece", "bowl", "cup", "glass", "serving"];

    [HttpGet("")]
    public async Task<IActionResult> Dashboard()
    {
        var today = DateTime.Today;
        var (mealTotals, _) = await counts.MealTotalsForDateAsync(today);
        return View(new AdminDashboardVm
        {
            MealTotals = mealTotals,
            TodayMeals = await counts.PendingSummaryForDateAsync(today),
            Subs = await counts.SubmissionCountsAsync(today, today),
            ActiveCustomers = await counts.ActiveCustomerCountAsync(),
        });
    }

    // ---------------------------------------------------------------------
    // Food items (menu master list)
    // ---------------------------------------------------------------------
    private Task<FoodItem?> GetFoodAsync(int id) =>
        db.QueryOneAsync<FoodItem>("SELECT * FROM food_items WHERE id=@id", new { id });

    /// <summary>Validate the add/edit form.</summary>
    private async Task<(FoodForm Form, List<string> Errors)> ReadFoodFormAsync(int? foodId = null)
    {
        var f = Request.Form;
        var form = new FoodForm
        {
            FoodName = Clean(f["food_name"], 100),
            Description = Clean(f["description"], 255),
            Unit = Clean(f["unit"], 20).ToLowerInvariant(),
            Price = Clean(f["price"], 12),
            Status = f["status"] == "inactive" ? "inactive" : "active",
        };
        var errors = new List<string>();
        if (form.FoodName.Length < 2)
            errors.Add("Food name must be at least 2 characters.");
        else if (await db.QueryOneAsync<int?>("SELECT id FROM food_items WHERE food_name=@name AND id <> @id",
                     new { name = form.FoodName, id = foodId ?? 0 }) is not null)
            errors.Add($"A food item named “{form.FoodName}” already exists.");
        if (!FoodUnits.Contains(form.Unit))
            errors.Add("Choose a valid unit.");
        if (form.Price != "")
        {
            if (decimal.TryParse(form.Price, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                && price >= 0 && price <= 99999.99m)
                form.PriceValue = Math.Round(price, 2);
            else
                errors.Add("Price must be a number between 0 and 99,999.99.");
        }
        return (form, errors);
    }

    [HttpGet("food-items")]
    public async Task<IActionResult> FoodItems(string? q, string? status)
    {
        q = Clean(q, 100);
        status = status is "active" or "inactive" ? status : "";
        var sql =
            "SELECT f.*, " +
            "  (SELECT COUNT(*) FROM food_schedule s WHERE s.food_item_id = f.id) AS times_scheduled, " +
            "  (SELECT MIN(s.schedule_date) FROM food_schedule s " +
            "     WHERE s.food_item_id = f.id AND s.schedule_date >= @today) AS next_date, " +
            "  (SELECT GROUP_CONCAT(DISTINCT mt.meal_name ORDER BY mt.sort_order SEPARATOR ', ') " +
            "     FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "     WHERE s.food_item_id = f.id) AS meal_names " +
            "FROM food_items f WHERE 1=1 ";
        if (q != "") sql += "AND (f.food_name LIKE @like OR f.description LIKE @like) ";
        if (status != "") sql += "AND f.status = @status ";
        sql += "ORDER BY f.status = 'inactive', f.food_name";
        var items = await db.QueryAsync<FoodItem>(sql, new { today = DateTime.Today, like = $"%{q}%", status });
        var stats = await db.QueryOneAsync<FoodStats>(
            "SELECT COUNT(*) AS total, COALESCE(SUM(status='active'),0) AS active, " +
            "COALESCE(SUM(status='inactive'),0) AS inactive FROM food_items") ?? new FoodStats();
        return View(new FoodItemsVm { Items = items, Q = q, Status = status, Stats = stats });
    }

    [HttpGet("food-items/new")]
    public IActionResult FoodItemNew() => View("FoodItemForm", new FoodFormVm { Units = FoodUnits });

    [HttpPost("food-items/new")]
    public async Task<IActionResult> FoodItemCreate([FromForm(Name = "next_action")] string? nextAction)
    {
        var (form, errors) = await ReadFoodFormAsync();
        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            return View("FoodItemForm", new FoodFormVm { Form = form, Units = FoodUnits });
        }
        await db.ExecuteAsync(
            "INSERT INTO food_items (food_name, description, unit, price, status) VALUES (@name, @desc, @unit, @price, @status)",
            new { name = form.FoodName, desc = form.Description == "" ? null : form.Description, unit = form.Unit,
                  price = form.PriceValue, status = form.Status });
        Flash("success", $"“{form.FoodName}” added to the menu items.");
        return nextAction == "add_another" ? RedirectToAction(nameof(FoodItemNew)) : RedirectToAction(nameof(FoodItems));
    }

    private Task<List<UsageRow>> UpcomingUsageAsync(int foodId) => db.QueryAsync<UsageRow>(
        "SELECT s.schedule_date, mt.meal_name FROM food_schedule s " +
        "JOIN meal_types mt ON mt.id = s.meal_type_id " +
        "WHERE s.food_item_id=@foodId AND s.schedule_date >= @today ORDER BY s.schedule_date, mt.sort_order LIMIT 8",
        new { foodId, today = DateTime.Today });

    [HttpGet("food-items/{id:int}/edit")]
    public async Task<IActionResult> FoodItemEdit(int id)
    {
        var food = await GetFoodAsync(id);
        if (food is null) return NotFound();
        var form = new FoodForm
        {
            FoodName = food.FoodName, Description = food.Description ?? "", Unit = food.Unit, Status = food.Status,
            Price = food.Price?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
        };
        return View("FoodItemForm", new FoodFormVm { Form = form, Food = food, Units = FoodUnits, Usage = await UpcomingUsageAsync(id) });
    }

    [HttpPost("food-items/{id:int}/edit")]
    public async Task<IActionResult> FoodItemUpdate(int id)
    {
        var food = await GetFoodAsync(id);
        if (food is null) return NotFound();
        var (form, errors) = await ReadFoodFormAsync(id);
        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            return View("FoodItemForm", new FoodFormVm { Form = form, Food = food, Units = FoodUnits, Usage = await UpcomingUsageAsync(id) });
        }
        await db.ExecuteAsync(
            "UPDATE food_items SET food_name=@name, description=@desc, unit=@unit, price=@price, status=@status WHERE id=@id",
            new { name = form.FoodName, desc = form.Description == "" ? null : form.Description, unit = form.Unit,
                  price = form.PriceValue, status = form.Status, id });
        Flash("success", $"“{form.FoodName}” updated.");
        return RedirectToAction(nameof(FoodItems));
    }

    [HttpPost("food-items/{id:int}/toggle")]
    public async Task<IActionResult> FoodItemToggle(int id)
    {
        var food = await GetFoodAsync(id);
        if (food is null) return NotFound();
        var newStatus = food.Status == "active" ? "inactive" : "active";
        await db.ExecuteAsync("UPDATE food_items SET status=@newStatus WHERE id=@id", new { newStatus, id });
        Flash("success", newStatus == "inactive"
            ? $"“{food.FoodName}” deactivated. It can't be added to new schedules, but meals already scheduled keep it."
            : $"“{food.FoodName}” activated.");
        var referer = Request.Headers.Referer.ToString();
        return Uri.TryCreate(referer, UriKind.Absolute, out var u) && u.Host == Request.Host.Host
            ? Redirect(u.PathAndQuery)
            : RedirectToAction(nameof(FoodItems));
    }

    [HttpPost("food-items/{id:int}/delete")]
    public async Task<IActionResult> FoodItemDelete(int id)
    {
        var food = await GetFoodAsync(id);
        if (food is null) return NotFound();
        var used = await db.ScalarAsync<int>("SELECT COUNT(*) FROM food_schedule WHERE food_item_id=@id", new { id });
        if (used > 0)
        {
            Flash("warning", $"“{food.FoodName}” is used in {used} scheduled meal(s), so it can't be deleted " +
                             "(that would break count history). Deactivate it instead.");
            return RedirectToAction(nameof(FoodItems));
        }
        await db.ExecuteAsync("DELETE FROM food_items WHERE id=@id", new { id });
        Flash("success", $"“{food.FoodName}” deleted.");
        return RedirectToAction(nameof(FoodItems));
    }
}
