using System.Security.Cryptography;
using System.Text;
using MealTrack.Data;
using MealTrack.Models;
using QRCoder;

namespace MealTrack.Services;

/// <summary>
/// Meal check-in by QR code. Every scheduled meal (one date + one meal type) has its own
/// random secret, so there is ONE fixed QR per meal per day, and a new QR the next day
/// (the next day's meal is a different schedule row with its own secret).
/// The QR does not change during the day, so it can be shown on a screen or printed.
/// A 6-digit code from the same secret is for customers who cannot scan.
/// Customers scan → /scan/{token} → checked in at once (a row in meal_attendance).
/// Applied (count &gt; 0) without a check-in = no-show, the food that gets wasted.
/// </summary>
public class AttendanceService(Db db)
{
    private const string Valid = "c.submission_status IN ('submitted','locked')";

    // ---------------- meals ----------------
    private const string MealSql =
        "SELECT s.id, s.schedule_date, mt.meal_name, mt.sort_order, f.food_name, s.is_published, s.qr_secret " +
        "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id JOIN food_items f ON f.id = s.food_item_id ";

    public Task<MealInfo?> MealAsync(int scheduleId) =>
        db.QueryOneAsync<MealInfo>(MealSql + "WHERE s.id=@scheduleId", new { scheduleId });

    public Task<List<MealInfo>> PublishedMealsOnAsync(DateTime date) =>
        db.QueryAsync<MealInfo>(MealSql + "WHERE s.schedule_date=@date AND s.is_published=1 ORDER BY mt.sort_order",
            new { date = date.Date });

    /// <summary>The meal's secret, created the first time its QR is shown.</summary>
    public async Task<string> SecretAsync(MealInfo meal)
    {
        if (meal.QrSecret is { Length: > 0 }) return meal.QrSecret;
        await db.ExecuteAsync("UPDATE food_schedule SET qr_secret=@secret WHERE id=@id AND qr_secret IS NULL",
            new { secret = NewSecret(), id = meal.Id });
        meal.QrSecret = await db.ScalarAsync<string>("SELECT qr_secret FROM food_schedule WHERE id=@id", new { id = meal.Id }, "");
        return meal.QrSecret;
    }

    /// <summary>New secret: the QR shown or printed earlier for this meal stops working.</summary>
    public Task RegenerateAsync(int scheduleId) =>
        db.ExecuteAsync("UPDATE food_schedule SET qr_secret=@secret WHERE id=@scheduleId", new { secret = NewSecret(), scheduleId });

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // ---------------- token & code ----------------
    private static byte[] Mac(string secret, string message) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));

    private static string Sig(string secret, string message) => Convert.ToHexString(Mac(secret, message), 0, 8).ToLowerInvariant();

    /// <summary>The token inside the meal's QR: "{scheduleId}-{signature}".</summary>
    public static string Token(int scheduleId, string secret) => $"{scheduleId}-{Sig(secret, $"meal:{scheduleId}")}";

    public static string Code(int scheduleId, string secret) =>
        (BitConverter.ToUInt32(Mac(secret, $"code:{scheduleId}"), 0) % 1_000_000).ToString("D6");

    /// <summary>Checks a scanned token (the caller checks that the meal is today).</summary>
    public async Task<MealInfo?> VerifyTokenAsync(string? token)
    {
        var parts = (token ?? "").Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var scheduleId) || parts[1].Length != 16) return null;
        var meal = await MealAsync(scheduleId);
        if (meal is null || !meal.IsPublished || meal.QrSecret is not { Length: > 0 } secret) return null;
        return Same(parts[1], Sig(secret, $"meal:{scheduleId}")) ? meal : null;
    }

    /// <summary>Finds which of today's meals a typed 6-digit code belongs to.</summary>
    public async Task<MealInfo?> MatchCodeAsync(string? code, DateTime today)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) return null;
        foreach (var meal in await PublishedMealsOnAsync(today.Date))
            if (meal.QrSecret is { Length: > 0 } secret && Same(code, Code(meal.Id, secret))) return meal;
        return null; // a meal whose QR was never shown has no code yet
    }

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>The meal's QR (same all day).</summary>
    public async Task<MealQr> QrAsync(MealInfo meal, string baseUrl)
    {
        var secret = await SecretAsync(meal);
        var url = $"{baseUrl.TrimEnd('/')}/scan/{Token(meal.Id, secret)}";
        return new MealQr(url, Code(meal.Id, secret), Svg(url));
    }


    /// <summary>Renders a QR code as inline SVG (#171717 on #FFFFFF, quiet zone included).</summary>
    public static string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var n = matrix.Count;
        var sb = new StringBuilder(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {n} {n}\" shape-rendering=\"crispEdges\" " +
            $"role=\"img\" aria-label=\"Meal check-in QR code\"><rect width=\"{n}\" height=\"{n}\" fill=\"#FFFFFF\"/>" +
            "<path fill=\"#171717\" d=\"");
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (matrix[y][x]) sb.Append($"M{x} {y}h1v1h-1z");
        return sb.Append("\"/></svg>").ToString();
    }

    // ---------------- check-in ----------------
    public async Task<CheckInResult> CheckInAsync(int scheduleId, int customerId, string method, int? markedBy, DateTime now)
    {
        var created = await db.ExecuteAsync(
            "INSERT IGNORE INTO meal_attendance (food_schedule_id, customer_id, checked_in_at, method, marked_by) " +
            "VALUES (@scheduleId, @customerId, @now, @method, @markedBy)",
            new { scheduleId, customerId, now, method, markedBy }) > 0;
        var at = await db.ScalarAsync<DateTime>(
            "SELECT checked_in_at FROM meal_attendance WHERE food_schedule_id=@scheduleId AND customer_id=@customerId",
            new { scheduleId, customerId }, now);
        return new CheckInResult(created, at, await AppliedQtyAsync(scheduleId, customerId));
    }

    public Task UndoAsync(int scheduleId, int customerId) => db.ExecuteAsync(
        "DELETE FROM meal_attendance WHERE food_schedule_id=@scheduleId AND customer_id=@customerId", new { scheduleId, customerId });

    public Task<int> AppliedQtyAsync(int scheduleId, int customerId) => db.ScalarAsync<int>(
        $"SELECT COALESCE(SUM(c.quantity),0) FROM customer_meal_counts c WHERE c.food_schedule_id=@scheduleId " +
        $"AND c.customer_id=@customerId AND {Valid}", new { scheduleId, customerId });

    public Task<DateTime?> CheckedInAtAsync(int scheduleId, int customerId) => db.QueryOneAsync<DateTime?>(
        "SELECT checked_in_at FROM meal_attendance WHERE food_schedule_id=@scheduleId AND customer_id=@customerId",
        new { scheduleId, customerId });

    // ---------------- lists & numbers ----------------
    private const string LineSql =
        "SELECT u.id AS customer_id, u.name, u.room_no, COALESCE(c.quantity,0) AS quantity, " +
        "       a.checked_in_at, a.method, mb.name AS marked_by_name " +
        "FROM users u " +
        "LEFT JOIN customer_meal_counts c ON c.customer_id=u.id AND c.food_schedule_id=@scheduleId AND " + Valid + " " +
        "LEFT JOIN meal_attendance a ON a.customer_id=u.id AND a.food_schedule_id=@scheduleId " +
        "LEFT JOIN users mb ON mb.id=a.marked_by " +
        "WHERE u.role='customer' AND u.status='active' AND (c.quantity > 0 OR a.id IS NOT NULL) ";

    public Task<List<AttendanceLine>> LinesAsync(int scheduleId) =>
        db.QueryAsync<AttendanceLine>(LineSql + "ORDER BY u.room_no, u.name", new { scheduleId });

    public Task<List<AttendanceLine>> RecentAsync(int scheduleId, int limit) =>
        db.QueryAsync<AttendanceLine>(LineSql + "AND a.id IS NOT NULL ORDER BY a.checked_in_at DESC, a.id DESC LIMIT @limit",
            new { scheduleId, limit });

    public async Task<AttendanceSummary> SummaryAsync(int scheduleId) => AttendanceSummary.From(await LinesAsync(scheduleId));

    /// <summary>Applied vs. came for every published meal in a date range (optionally one seller's meals).</summary>
    public Task<List<AttendanceReportRow>> ReportRowsAsync(DateTime start, DateTime end, int? sellerId = null) =>
        db.QueryAsync<AttendanceReportRow>(
            "SELECT s.id AS schedule_id, s.schedule_date, mt.meal_name, mt.sort_order, f.food_name, " +
            "  COALESCE(ap.applied,0) AS applied, COALESCE(ap.portions,0) AS applied_portions, " +
            "  COALESCE(ap.came,0) AS came, COALESCE(ap.no_show_portions,0) AS no_show_portions, " +
            "  (SELECT COUNT(*) FROM meal_attendance a2 JOIN users u2 ON u2.id=a2.customer_id AND u2.status='active' " +
            "     WHERE a2.food_schedule_id=s.id) AS checked_in_total " +
            "FROM food_schedule s " +
            "JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "JOIN food_items f ON f.id = s.food_item_id " +
            "LEFT JOIN ( " +
            "   SELECT c.food_schedule_id, COUNT(*) AS applied, SUM(c.quantity) AS portions, " +
            "          SUM(a.id IS NOT NULL) AS came, SUM(CASE WHEN a.id IS NULL THEN c.quantity ELSE 0 END) AS no_show_portions " +
            "   FROM customer_meal_counts c " +
            "   JOIN users u ON u.id=c.customer_id AND u.status='active' AND u.role='customer' " +
            "   LEFT JOIN meal_attendance a ON a.food_schedule_id=c.food_schedule_id AND a.customer_id=c.customer_id " +
            "   WHERE " + Valid + " AND c.quantity > 0 GROUP BY c.food_schedule_id " +
            ") ap ON ap.food_schedule_id = s.id " +
            "WHERE s.is_published=1 AND s.schedule_date BETWEEN @start AND @end " +
            "  AND (@sellerId IS NULL OR EXISTS (SELECT 1 FROM seller_assignments sa WHERE sa.food_schedule_id=s.id AND sa.seller_id=@sellerId)) " +
            "ORDER BY s.schedule_date, mt.sort_order",
            new { start = start.Date, end = end.Date, sellerId });

    /// <summary>Admins manage every meal; sellers only the meals assigned to them.</summary>
    public async Task<bool> CanManageAsync(AppUser user, int scheduleId) => user.Role switch
    {
        Roles.Admin => true,
        Roles.Seller => await db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM seller_assignments WHERE seller_id=@sellerId AND food_schedule_id=@scheduleId",
            new { sellerId = user.Id, scheduleId }) > 0,
        _ => false,
    };
}
