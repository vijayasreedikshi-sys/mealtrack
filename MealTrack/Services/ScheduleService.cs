using Dapper;
using MealTrack.Data;
using MealTrack.Models;

namespace MealTrack.Services;

public record SaveResult(int Id, bool Created, bool Changed);

/// <summary>
/// Creating and changing food schedules: single meals, the week builder,
/// copying the previous week, publishing, seller auto-assignment and the
/// "schedule changed" notifications.
/// </summary>
public class ScheduleService(Db db, NotificationService notifications)
{
    public Task<List<MealType>> MealTypesAsync() =>
        db.QueryAsync<MealType>("SELECT * FROM meal_types ORDER BY sort_order");

    public Task<List<FoodItem>> ActiveFoodsAsync() =>
        db.QueryAsync<FoodItem>("SELECT id, food_name, unit, status FROM food_items WHERE status='active' ORDER BY food_name");

    /// <summary>All schedules (draft and published) in a date range with live totals.</summary>
    public Task<List<AdminScheduleRow>> RangeAsync(DateTime start, DateTime end) => db.QueryAsync<AdminScheduleRow>(
        "SELECT s.id, s.schedule_date, s.meal_type_id, mt.meal_name, mt.sort_order, s.food_item_id, f.food_name, " +
        "       f.status AS food_status, s.cutoff_time, s.is_published, s.notes, " +
        "       COALESCE(t.total, 0) AS total_qty, COALESCE(t.cnt, 0) AS submitters, " +
        "       (SELECT GROUP_CONCAT(u.name ORDER BY u.name SEPARATOR ', ') FROM seller_assignments a " +
        "          JOIN users u ON u.id = a.seller_id WHERE a.food_schedule_id = s.id) AS sellers " +
        "FROM food_schedule s " +
        "JOIN meal_types mt ON mt.id = s.meal_type_id " +
        "JOIN food_items f ON f.id = s.food_item_id " +
        "LEFT JOIN (SELECT c.food_schedule_id, SUM(c.quantity) AS total, COUNT(*) AS cnt " +
        "           FROM customer_meal_counts c " +
        "           JOIN users u ON u.id = c.customer_id AND u.status='active' AND u.role='customer' " +
        "           WHERE c.submission_status IN ('submitted','locked') GROUP BY c.food_schedule_id) t " +
        "       ON t.food_schedule_id = s.id " +
        "WHERE s.schedule_date BETWEEN @start AND @end " +
        "ORDER BY s.schedule_date, mt.sort_order",
        new { start = start.Date, end = end.Date });

    public async Task<AdminScheduleRow?> GetAsync(int id)
    {
        var row = await db.QueryOneAsync<AdminScheduleRow>(
            "SELECT s.id, s.schedule_date, s.meal_type_id, mt.meal_name, mt.sort_order, s.food_item_id, f.food_name, " +
            "       f.status AS food_status, s.cutoff_time, s.is_published, s.notes " +
            "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id JOIN food_items f ON f.id = s.food_item_id " +
            "WHERE s.id=@id", new { id });
        if (row is null) return null;
        var all = await RangeAsync(row.ScheduleDate, row.ScheduleDate);
        return all.FirstOrDefault(r => r.Id == id) ?? row;
    }

    /// <summary>
    /// Create or update the schedule for one date + meal.
    /// cutoffOverride null = meal-type default for new rows, unchanged for existing rows.
    /// notes null = unchanged. publish null = new rows are drafts, existing rows unchanged.
    /// </summary>
    public async Task<SaveResult> SaveSlotAsync(DateTime date, int mealTypeId, int foodItemId, DateTime? cutoffOverride,
        string? notes, bool? publish, int adminId)
    {
        date = date.Date;
        var meal = await db.QueryOneAsync<MealType>("SELECT * FROM meal_types WHERE id=@mealTypeId", new { mealTypeId })
                   ?? throw new ArgumentException("Unknown meal type");
        var existing = await db.QueryOneAsync<AdminScheduleRow>(
            "SELECT id, food_item_id, cutoff_time, is_published, notes FROM food_schedule " +
            "WHERE schedule_date=@date AND meal_type_id=@mealTypeId", new { date, mealTypeId });

        if (existing is null)
        {
            var cutoff = cutoffOverride ?? Cutoff.Compute(date, meal);
            var id = (int)await db.InsertAsync(
                "INSERT INTO food_schedule (schedule_date, meal_type_id, food_item_id, cutoff_time, is_published, notes, created_by) " +
                "VALUES (@date, @mealTypeId, @foodItemId, @cutoff, @pub, @notes, @adminId)",
                new { date, mealTypeId, foodItemId, cutoff, pub = publish ?? false, notes, adminId });
            await AutoAssignAsync(id, mealTypeId);
            return new SaveResult(id, true, false);
        }

        var newCutoff = cutoffOverride ?? existing.CutoffTime;
        var newNotes = notes ?? existing.Notes;
        var newPublished = publish ?? existing.IsPublished;
        var foodChanged = existing.FoodItemId != foodItemId;
        var cutoffChanged = existing.CutoffTime != newCutoff;
        if (!foodChanged && !cutoffChanged && newNotes == existing.Notes && newPublished == existing.IsPublished)
            return new SaveResult(existing.Id, false, false);

        await db.ExecuteAsync(
            "UPDATE food_schedule SET food_item_id=@foodItemId, cutoff_time=@newCutoff, notes=@newNotes, is_published=@newPublished " +
            "WHERE id=@id",
            new { foodItemId, newCutoff, newNotes, newPublished, id = existing.Id });

        // Re-open or lock counts if the cutoff moved.
        if (cutoffChanged)
        {
            await db.ExecuteAsync(
                newCutoff > DateTime.Now
                    ? "UPDATE customer_meal_counts SET submission_status='submitted' WHERE food_schedule_id=@id AND submission_status='locked'"
                    : "UPDATE customer_meal_counts SET submission_status='locked' WHERE food_schedule_id=@id AND submission_status='submitted'",
                new { id = existing.Id });
        }

        var changed = existing.IsPublished && (foodChanged || cutoffChanged);
        if (changed) await NotifyChangedAsync(existing.Id, foodChanged, cutoffChanged);
        return new SaveResult(existing.Id, false, changed);
    }

    /// <summary>"Schedule changed" to customers who already submitted and to assigned sellers.</summary>
    private async Task NotifyChangedAsync(int scheduleId, bool foodChanged, bool cutoffChanged)
    {
        var s = await GetAsync(scheduleId);
        if (s is null) return;
        var what = new List<string>();
        if (foodChanged) what.Add($"the menu is now {s.FoodName}");
        if (cutoffChanged) what.Add($"the cutoff is now {s.CutoffTime:ddd dd MMM, hh:mm tt}");
        var message = $"Schedule changed: {s.MealName} on {s.ScheduleDate:ddd dd MMM} – {string.Join(" and ", what)}.";

        var customers = await db.QueryAsync<int>(
            "SELECT customer_id FROM customer_meal_counts WHERE food_schedule_id=@scheduleId " +
            "AND submission_status IN ('submitted','locked')", new { scheduleId });
        await notifications.NotifyManyAsync(customers, message + " Please check your count.",
            $"/customer/submit?date={s.ScheduleDate:yyyy-MM-dd}");
        var sellers = await db.QueryAsync<int>(
            "SELECT seller_id FROM seller_assignments WHERE food_schedule_id=@scheduleId", new { scheduleId });
        await notifications.NotifyManyAsync(sellers, message, $"/seller/requirements?date={s.ScheduleDate:yyyy-MM-dd}");
    }

    /// <summary>Assign the sellers whose default responsibility covers this meal type.</summary>
    public Task AutoAssignAsync(int scheduleId, int mealTypeId) => db.ExecuteAsync(
        "INSERT IGNORE INTO seller_assignments (seller_id, food_schedule_id, can_view_customers) " +
        "SELECT d.seller_id, @scheduleId, d.can_view_customers FROM seller_meal_defaults d " +
        "JOIN users u ON u.id = d.seller_id AND u.status='active' WHERE d.meal_type_id=@mealTypeId",
        new { scheduleId, mealTypeId });

    /// <summary>Copy last week's menu into empty slots of the given week (as drafts).</summary>
    public async Task<int> CopyPreviousWeekAsync(DateTime weekStart, int adminId)
    {
        var previous = await RangeAsync(weekStart.AddDays(-7), weekStart.AddDays(-1));
        var current = (await RangeAsync(weekStart, weekStart.AddDays(6)))
            .Select(r => (r.ScheduleDate, r.MealTypeId)).ToHashSet();
        var copied = 0;
        foreach (var p in previous)
        {
            var target = p.ScheduleDate.AddDays(7);
            if (current.Contains((target, p.MealTypeId))) continue;
            await SaveSlotAsync(target, p.MealTypeId, p.FoodItemId, null, p.Notes, false, adminId);
            copied++;
        }
        return copied;
    }

    /// <summary>Publish or unpublish every schedule in a range. Publishing notifies customers once.</summary>
    public async Task<int> SetPublishedAsync(DateTime start, DateTime end, bool publish)
    {
        var changed = await db.ExecuteAsync(
            "UPDATE food_schedule SET is_published=@publish WHERE schedule_date BETWEEN @start AND @end AND is_published<>@publish",
            new { publish, start = start.Date, end = end.Date });
        if (publish && changed > 0)
        {
            var label = start.Date == end.Date ? $"{start:ddd dd MMM}" : $"{start:dd MMM} – {end:dd MMM}";
            await notifications.NotifyRoleAsync(Roles.Customer,
                $"New food schedule published for {label}. Submit your counts before each cutoff.",
                $"/customer/schedule?week={Infrastructure.Validators.WeekStart(start):yyyy-MM-dd}",
                $"pub:{start:yyyyMMdd}:{DateTime.Now:yyyyMMddHHmm}");
        }
        return changed;
    }

    /// <summary>Publish or unpublish one meal. Publishing notifies customers.</summary>
    public async Task PublishOneAsync(int id, bool publish)
    {
        var changed = await db.ExecuteAsync(
            "UPDATE food_schedule SET is_published=@publish WHERE id=@id AND is_published<>@publish", new { publish, id });
        if (!publish || changed == 0) return;
        var s = await GetAsync(id);
        if (s is null) return;
        await notifications.NotifyRoleAsync(Roles.Customer,
            $"{s.MealName} on {s.ScheduleDate:ddd dd MMM} ({s.FoodName}) is now on the menu. Submit your count before {s.CutoffTime:ddd hh:mm tt}.",
            $"/customer/submit?date={s.ScheduleDate:yyyy-MM-dd}", $"pub1:{id}:{DateTime.Now:yyyyMMddHHmm}");
    }

    public Task<int> CountsForScheduleAsync(int id) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM customer_meal_counts WHERE food_schedule_id=@id", new { id });

    public Task<int> DeleteAsync(int id) => db.ExecuteAsync("DELETE FROM food_schedule WHERE id=@id", new { id });

    // ---------------- seller assignments ----------------
    public Task<List<SellerOption>> SellerOptionsAsync(int scheduleId) => db.QueryAsync<SellerOption>(
        "SELECT u.id, u.name, u.status, (a.id IS NOT NULL) AS assigned, COALESCE(a.can_view_customers, 0) AS can_view_customers " +
        "FROM users u LEFT JOIN seller_assignments a ON a.seller_id = u.id AND a.food_schedule_id=@scheduleId " +
        "WHERE u.role='seller' ORDER BY u.status, u.name", new { scheduleId });

    public Task SetAssignmentsAsync(int scheduleId, IReadOnlyCollection<(int SellerId, bool CanView)> sellers) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var keep = sellers.Select(s => s.SellerId).DefaultIfEmpty(0).ToArray();
            await conn.ExecuteAsync(
                "DELETE FROM seller_assignments WHERE food_schedule_id=@scheduleId AND seller_id NOT IN @keep",
                new { scheduleId, keep }, tx);
            foreach (var (sellerId, canView) in sellers)
                await conn.ExecuteAsync(
                    "INSERT INTO seller_assignments (seller_id, food_schedule_id, can_view_customers) " +
                    "VALUES (@sellerId, @scheduleId, @canView) ON DUPLICATE KEY UPDATE can_view_customers=@canView",
                    new { sellerId, scheduleId, canView }, tx);
        });
}
