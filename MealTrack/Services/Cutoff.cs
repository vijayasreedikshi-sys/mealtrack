using MealTrack.Data;
using MealTrack.Models;
using Microsoft.Extensions.Options;

namespace MealTrack.Services;

/// <summary>Cutoff rules and status calculation.</summary>
public static class Cutoff
{
    /// <summary>Default cutoff for a meal on a date, from meal_types settings.</summary>
    public static DateTime Compute(DateTime scheduleDate, MealType mealType) =>
        scheduleDate.Date.AddDays(mealType.DefaultCutoffDayOffset) + mealType.DefaultCutoffTime;

    public static bool IsOpen(DateTime cutoffTime, DateTime? now = null) => (now ?? DateTime.Now) < cutoffTime;

    /// <summary>
    /// Status shown to users:
    ///   submitted -> customer submitted and can still edit
    ///   locked    -> submitted and the cutoff has passed
    ///   pending   -> not yet submitted, still open
    ///   missed    -> not submitted and the cutoff has passed
    /// </summary>
    public static string CountStatus(string? submissionStatus, DateTime cutoffTime, DateTime? now = null)
    {
        var closed = cutoffTime <= (now ?? DateTime.Now);
        if (submissionStatus == "cancelled") return "cancelled";
        if (submissionStatus == "locked" || (submissionStatus == "submitted" && closed)) return "locked";
        if (submissionStatus == "submitted") return "submitted";
        return closed ? "missed" : "pending";
    }
}

/// <summary>
/// Background job (every SweepIntervalSeconds): locks counts after cutoff, sends
/// deadline reminders and final-count notices. All notifications use a dedupe_key,
/// so nothing is ever sent twice. Runs inside the web app – no Windows service needed.
/// </summary>
public class CutoffSweepService(Db db, IOptions<AppOptions> options, ILogger<CutoffSweepService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.Value.SweepIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunSweepAsync(DateTime.Now);
            }
            catch (Exception ex) // never crash the site because of the sweep
            {
                log.LogError(ex, "Lock/notification sweep failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunSweepAsync(DateTime now)
    {
        var window = now.AddHours(options.Value.ReminderHoursBeforeCutoff);
        var since = now.AddHours(-24);

        await db.InTransactionAsync(async (conn, tx) =>
        {
            // 1. Lock submitted counts whose cutoff has passed.
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE customer_meal_counts c JOIN food_schedule s ON s.id = c.food_schedule_id " +
                "SET c.submission_status='locked' " +
                "WHERE c.submission_status='submitted' AND s.cutoff_time <= @now",
                new { now }, tx);

            // 2. Deadline reminders for active customers who have not submitted yet.
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) " +
                "SELECT u.id, " +
                "  CONCAT('Reminder: submit your ', mt.meal_name, ' count for ', " +
                "         DATE_FORMAT(s.schedule_date, '%a %d %b'), ' before ', " +
                "         DATE_FORMAT(s.cutoff_time, '%h:%i %p'), '.'), " +
                "  CONCAT('/customer/submit?date=', DATE_FORMAT(s.schedule_date, '%Y-%m-%d')), " +
                "  CONCAT('remind:', s.id, ':', u.id), @now " +
                "FROM food_schedule s " +
                "JOIN meal_types mt ON mt.id = s.meal_type_id " +
                "JOIN users u ON u.role='customer' AND u.status='active' " +
                "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = u.id " +
                "     AND c.submission_status IN ('submitted','locked') " +
                "WHERE s.is_published=1 AND s.cutoff_time > @now AND s.cutoff_time <= @window AND c.id IS NULL",
                new { now, window }, tx);

            // 3. Final cooking count to assigned sellers once a meal closes (last 24h only).
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) " +
                "SELECT a.seller_id, " +
                "  CONCAT('Final count for ', mt.meal_name, ' (', f.food_name, ') on ', " +
                "         DATE_FORMAT(s.schedule_date, '%a %d %b'), ': ', COALESCE(t.total, 0), ' portions.'), " +
                "  CONCAT('/seller?date=', DATE_FORMAT(s.schedule_date, '%Y-%m-%d')), " +
                "  CONCAT('final:', s.id, ':', a.seller_id), @now " +
                "FROM seller_assignments a " +
                "JOIN food_schedule s ON s.id = a.food_schedule_id " +
                "JOIN meal_types mt ON mt.id = s.meal_type_id " +
                "JOIN food_items f ON f.id = s.food_item_id " +
                "LEFT JOIN ( " +
                "   SELECT c.food_schedule_id, SUM(c.quantity) AS total " +
                "   FROM customer_meal_counts c JOIN users u ON u.id = c.customer_id AND u.status='active' " +
                "   WHERE c.submission_status IN ('submitted','locked') GROUP BY c.food_schedule_id " +
                ") t ON t.food_schedule_id = s.id " +
                "WHERE s.is_published=1 AND s.cutoff_time <= @now AND s.cutoff_time > @since",
                new { now, since }, tx);

            // 4. Day after a meal that used QR check-in: tell customers who applied but never came.
            //    (Meals with no check-ins at all are skipped – the QR was not used that day.)
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) " +
                "SELECT c.customer_id, " +
                "  CONCAT('You applied for ', c.quantity, ' ', mt.meal_name, ' (', f.food_name, ') on ', " +
                "         DATE_FORMAT(s.schedule_date, '%a %d %b'), ' but did not check in. ', " +
                "         'Please enter 0 when you will not eat, so food is not wasted.'), " +
                "  '/customer/history', CONCAT('noshow:', s.id, ':', c.customer_id), @now " +
                "FROM customer_meal_counts c " +
                "JOIN users u ON u.id = c.customer_id AND u.status='active' " +
                "JOIN food_schedule s ON s.id = c.food_schedule_id " +
                "JOIN meal_types mt ON mt.id = s.meal_type_id " +
                "JOIN food_items f ON f.id = s.food_item_id " +
                "LEFT JOIN meal_attendance a ON a.food_schedule_id = s.id AND a.customer_id = c.customer_id " +
                "WHERE c.submission_status IN ('submitted','locked') AND c.quantity > 0 AND a.id IS NULL " +
                "  AND s.schedule_date < DATE(@now) AND s.schedule_date >= DATE(@now) - INTERVAL 2 DAY " +
                "  AND EXISTS (SELECT 1 FROM meal_attendance x WHERE x.food_schedule_id = s.id)",
                new { now }, tx);
        });
    }
}
