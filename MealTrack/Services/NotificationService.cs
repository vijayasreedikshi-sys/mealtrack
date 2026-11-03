using MealTrack.Data;
using MealTrack.Models;

namespace MealTrack.Services;

/// <summary>In-app notifications.</summary>
public class NotificationService(Db db)
{
    private const string InsertSql =
        "INSERT IGNORE INTO notifications (user_id, message, link, dedupe_key, created_at) " +
        "VALUES (@userId, @message, @link, @dedupeKey, @createdAt)";

    private static string Trim(string message) => message.Length > 500 ? message[..500] : message;

    /// <summary>Create a notification. With a dedupeKey the same notice is never sent twice.</summary>
    public Task NotifyAsync(int userId, string message, string? link = null, string? dedupeKey = null) =>
        db.ExecuteAsync(InsertSql, new { userId, message = Trim(message), link, dedupeKey, createdAt = DateTime.Now });

    public async Task<int> NotifyManyAsync(IEnumerable<int> userIds, string message, string? link = null,
        string? keyPrefix = null)
    {
        var now = DateTime.Now;
        var rows = userIds.Select(uid => new
        {
            userId = uid,
            message = Trim(message),
            link,
            dedupeKey = keyPrefix is null ? null : $"{keyPrefix}:{uid}",
            createdAt = now,
        }).ToList();
        if (rows.Count > 0) await db.ExecuteAsync(InsertSql, rows); // Dapper runs it once per row
        return rows.Count;
    }

    public async Task<int> NotifyRoleAsync(string role, string message, string? link = null, string? keyPrefix = null)
    {
        var ids = await db.QueryAsync<int>("SELECT id FROM users WHERE role=@role AND status='active'", new { role });
        return await NotifyManyAsync(ids, message, link, keyPrefix);
    }

    private sealed class UpdateInfo
    {
        public int SellerId { get; set; }
        public string MealName { get; set; } = "";
        public DateTime ScheduleDate { get; set; }
        public int Total { get; set; }
    }

    /// <summary>
    /// Tells assigned sellers that counts changed before the cutoff.
    /// Debounced: at most one alert per schedule per seller per hour (dedupe_key).
    /// </summary>
    public async Task NotifySellersOfUpdatesAsync(IEnumerable<int> scheduleIds)
    {
        var hour = DateTime.Now.ToString("yyyyMMddHH");
        foreach (var scheduleId in scheduleIds)
        {
            var rows = await db.QueryAsync<UpdateInfo>(
                "SELECT a.seller_id, mt.meal_name, s.schedule_date, " +
                "  (SELECT COALESCE(SUM(c.quantity),0) FROM customer_meal_counts c " +
                "     JOIN users u ON u.id=c.customer_id AND u.status='active' AND u.role='customer' " +
                "     WHERE c.food_schedule_id=s.id AND c.submission_status IN ('submitted','locked')) AS total " +
                "FROM seller_assignments a JOIN food_schedule s ON s.id=a.food_schedule_id " +
                "JOIN meal_types mt ON mt.id=s.meal_type_id WHERE a.food_schedule_id=@scheduleId",
                new { scheduleId });
            foreach (var r in rows)
                await NotifyAsync(r.SellerId,
                    $"Counts updated for {r.MealName} on {r.ScheduleDate:ddd dd MMM} – current total {r.Total} portions (can still change until cutoff).",
                    $"/seller/requirements?date={r.ScheduleDate:yyyy-MM-dd}", $"upd:{scheduleId}:{hour}:{r.SellerId}");
        }
    }

    public Task<int> UnreadCountAsync(int userId) =>
        db.ScalarAsync<int>("SELECT COUNT(*) FROM notifications WHERE user_id=@userId AND is_read=0", new { userId });

    public Task<List<NotificationItem>> RecentAsync(int userId, int limit = 5) => db.QueryAsync<NotificationItem>(
        "SELECT id, message, link, is_read, created_at FROM notifications " +
        "WHERE user_id=@userId ORDER BY created_at DESC, id DESC LIMIT @limit",
        new { userId, limit });

    public Task<int> MarkReadAsync(int userId, int notificationId) => db.ExecuteAsync(
        "UPDATE notifications SET is_read=1 WHERE id=@notificationId AND user_id=@userId",
        new { userId, notificationId });

    public Task<int> MarkAllReadAsync(int userId) => db.ExecuteAsync(
        "UPDATE notifications SET is_read=1 WHERE user_id=@userId AND is_read=0", new { userId });
}
