using MealTrack.Data;
using MealTrack.Models;

namespace MealTrack.Services;

/// <summary>
/// Automatic food count aggregation – the single source of truth for totals.
///
/// Rules applied in every query:
///  * only submission_status IN ('submitted','locked') is counted
///    (pending / cancelled / never-submitted are excluded)
///  * quantity 0 is a valid submission (it adds 0 but counts as submitted)
///  * only ACTIVE customers are counted
///  * UNIQUE(customer_id, food_schedule_id) means an edited submission is a
///    single row, so it can never be double-counted
/// </summary>
public class CountService(Db db)
{
    public const string ValidStatuses = "('submitted','locked')";

    // LEFT JOIN fragment: valid counts of active customers for schedule alias "s"
    private const string ValidCountsJoin =
        "LEFT JOIN (customer_meal_counts c " +
        "           JOIN users cu ON cu.id = c.customer_id AND cu.status='active' AND cu.role='customer') " +
        "      ON c.food_schedule_id = s.id AND c.submission_status IN " + ValidStatuses + " ";

    /// <summary>One row per scheduled meal with its total required quantity.</summary>
    public Task<List<ScheduleTotal>> ScheduleTotalsAsync(DateTime start, DateTime end, int? mealTypeId = null,
        int? sellerId = null, bool publishedOnly = true)
    {
        var sql =
            "SELECT s.id AS schedule_id, s.schedule_date, s.cutoff_time, s.is_published, " +
            "       mt.id AS meal_type_id, mt.meal_name, mt.sort_order, f.food_name, f.unit, " +
            "       COALESCE(SUM(c.quantity), 0) AS total_qty, " +
            "       COUNT(c.id) AS submitters, " +
            "       COALESCE(SUM(c.quantity = 0), 0) AS zero_count " +
            "FROM food_schedule s " +
            "JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "JOIN food_items f ON f.id = s.food_item_id ";
        if (sellerId is not null)
            sql += "JOIN seller_assignments sa ON sa.food_schedule_id = s.id AND sa.seller_id = @sellerId ";
        sql += ValidCountsJoin + "WHERE s.schedule_date BETWEEN @start AND @end ";
        if (publishedOnly) sql += "AND s.is_published = 1 ";
        if (mealTypeId is not null) sql += "AND s.meal_type_id = @mealTypeId ";
        sql +=
            "GROUP BY s.id, s.schedule_date, s.cutoff_time, s.is_published, mt.id, mt.meal_name, " +
            "         mt.sort_order, f.food_name, f.unit " +
            "ORDER BY s.schedule_date, mt.sort_order";
        return db.QueryAsync<ScheduleTotal>(sql, new { start = start.Date, end = end.Date, sellerId, mealTypeId });
    }

    /// <summary>{meal_name: total_qty} for one date plus "Total".</summary>
    public async Task<(Dictionary<string, int> Totals, List<ScheduleTotal> Rows)> MealTotalsForDateAsync(
        DateTime d, int? sellerId = null)
    {
        var rows = await ScheduleTotalsAsync(d, d, sellerId: sellerId);
        var totals = rows.ToDictionary(r => r.MealName, r => r.TotalQty);
        totals["Total"] = rows.Sum(r => r.TotalQty);
        return (totals, rows);
    }

    public Task<int> ScheduleTotalAsync(int scheduleId) => db.ScalarAsync<int>(
        "SELECT COALESCE(SUM(c.quantity),0) FROM customer_meal_counts c " +
        "JOIN users u ON u.id = c.customer_id AND u.status='active' AND u.role='customer' " +
        "WHERE c.food_schedule_id = @scheduleId AND c.submission_status IN " + ValidStatuses,
        new { scheduleId });

    /// <summary>Customer-wise quantities for one scheduled meal (valid submissions only).</summary>
    public Task<List<CustomerQty>> CustomerBreakdownAsync(int scheduleId) => db.QueryAsync<CustomerQty>(
        "SELECT u.id, u.name, u.room_no, c.quantity, c.submission_status, c.updated_at " +
        "FROM customer_meal_counts c " +
        "JOIN users u ON u.id = c.customer_id AND u.status='active' AND u.role='customer' " +
        "WHERE c.food_schedule_id = @scheduleId AND c.submission_status IN " + ValidStatuses + " " +
        "ORDER BY u.room_no, u.name",
        new { scheduleId });

    public Task<int> ActiveCustomerCountAsync() =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE role='customer' AND status='active'");

    /// <summary>Active customers who have NOT submitted a valid count for this meal.</summary>
    public Task<List<CustomerQty>> PendingCustomersAsync(int scheduleId) => db.QueryAsync<CustomerQty>(
        "SELECT u.id, u.name, u.room_no, u.phone FROM users u " +
        "LEFT JOIN customer_meal_counts c ON c.customer_id = u.id AND c.food_schedule_id = @scheduleId " +
        "     AND c.submission_status IN " + ValidStatuses + " " +
        "WHERE u.role='customer' AND u.status='active' AND c.id IS NULL " +
        "ORDER BY u.room_no, u.name",
        new { scheduleId });

    /// <summary>Pending (not submitted) customer count per meal for a date.</summary>
    public async Task<List<ScheduleTotal>> PendingSummaryForDateAsync(DateTime d)
    {
        var active = await ActiveCustomerCountAsync();
        var rows = await ScheduleTotalsAsync(d, d);
        foreach (var r in rows)
        {
            r.Pending = Math.Max(active - r.Submitters, 0);
            r.ActiveCustomers = active;
        }
        return rows;
    }

    /// <summary>Number of valid submissions vs. expected (active customers × published meals).</summary>
    public async Task<SubmissionCounts> SubmissionCountsAsync(DateTime start, DateTime end)
    {
        var row = await db.QueryOneAsync<(long Meals, long Submitted)>(
            "SELECT COUNT(DISTINCT s.id) AS meals, COUNT(c.id) AS submitted " +
            "FROM food_schedule s " + ValidCountsJoin +
            "WHERE s.is_published=1 AND s.schedule_date BETWEEN @start AND @end",
            new { start = start.Date, end = end.Date });
        var active = await ActiveCustomerCountAsync();
        var meals = (int)row.Meals;
        var submitted = (int)row.Submitted;
        var expected = meals * active;
        return new SubmissionCounts(meals, submitted, expected, Math.Max(expected - submitted, 0), active);
    }
}
