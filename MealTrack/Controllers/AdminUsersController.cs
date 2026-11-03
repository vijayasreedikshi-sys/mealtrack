using System.Security.Cryptography;
using MealTrack.Data;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Admin: manage customers and sellers (same pages, "kind" in the URL).</summary>
[Authorize(Roles = Roles.Admin)]
[Route("admin/{kind:regex(^(customers|sellers)$)}")]
public class AdminUsersController(Db db, ScheduleService schedules, NotificationService notifications) : AppController
{
    private static string RoleOf(string kind) => kind == "sellers" ? Roles.Seller : Roles.Customer;

    private async Task<AppUser?> GetUserAsync(string kind, int id) => await db.QueryOneAsync<AppUser>(
        "SELECT id, name, email, role, phone, room_no, status, created_at FROM users WHERE id=@id AND role=@role",
        new { id, role = RoleOf(kind) });

    /// <summary>Readable temporary password, e.g. "Meal-4821-kx".</summary>
    private static string TempPassword()
    {
        const string letters = "abcdefghjkmnpqrstuvwxyz";
        return $"Meal-{RandomNumberGenerator.GetInt32(1000, 10000)}-" +
               $"{letters[RandomNumberGenerator.GetInt32(letters.Length)]}{letters[RandomNumberGenerator.GetInt32(letters.Length)]}";
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string kind, string? q, string? status)
    {
        q = Clean(q, 100);
        status = status is "active" or "inactive" ? status : "";
        var role = RoleOf(kind);
        var sql =
            "SELECT u.id, u.name, u.email, u.role, u.phone, u.room_no, u.status, u.created_at, " +
            "  (SELECT COUNT(*) FROM customer_meal_counts c JOIN food_schedule s ON s.id=c.food_schedule_id " +
            "     WHERE c.customer_id=u.id AND c.submission_status IN ('submitted','locked') AND s.schedule_date >= @since) AS submissions30, " +
            "  (SELECT COALESCE(SUM(c.quantity),0) FROM customer_meal_counts c JOIN food_schedule s ON s.id=c.food_schedule_id " +
            "     WHERE c.customer_id=u.id AND c.submission_status IN ('submitted','locked') AND s.schedule_date >= @since) AS portions30, " +
            "  GREATEST(COALESCE((SELECT MAX(c.updated_at) FROM customer_meal_counts c WHERE c.customer_id=u.id), CAST('1000-01-01' AS DATETIME)), " +
            "           COALESCE((SELECT MAX(p.updated_at) FROM food_preparation p WHERE p.seller_id=u.id), CAST('1000-01-01' AS DATETIME))) AS last_activity, " +
            "  (SELECT GROUP_CONCAT(mt.meal_name ORDER BY mt.sort_order SEPARATOR ', ') FROM seller_meal_defaults d " +
            "     JOIN meal_types mt ON mt.id=d.meal_type_id WHERE d.seller_id=u.id) AS meal_defaults, " +
            "  (SELECT COUNT(*) FROM seller_assignments a JOIN food_schedule s ON s.id=a.food_schedule_id " +
            "     WHERE a.seller_id=u.id AND s.schedule_date >= @today) AS upcoming_assignments " +
            "FROM users u WHERE u.role=@role ";
        if (q != "") sql += "AND (u.name LIKE @like OR u.email LIKE @like OR u.phone LIKE @like OR u.room_no LIKE @like) ";
        if (status != "") sql += "AND u.status=@status ";
        sql += "ORDER BY u.status, u.room_no IS NULL, u.room_no, u.name";
        var today = DateTime.Today;
        var users = await db.QueryAsync<UserRow>(sql, new { role, like = $"%{q}%", status, since = today.AddDays(-29), today });
        foreach (var u in users.Where(u => u.LastActivity?.Year == 1000)) u.LastActivity = null;

        return View(new UsersVm
        {
            Kind = kind, Users = users, Q = q, Status = status,
            Total = await db.ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE role=@role", new { role }),
            Active = await db.ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE role=@role AND status='active'", new { role }),
        });
    }

    // ---------------- add / edit ----------------
    private (UserFormVm Form, List<string> Errors) ReadForm(string kind)
    {
        var f = Request.Form;
        var form = new UserFormVm
        {
            Kind = kind,
            Name = Clean(f["name"], 100),
            Email = Clean(f["email"], 150).ToLowerInvariant(),
            Phone = Clean(f["phone"], 15),
            RoomNo = kind == "customers" ? Clean(f["room_no"], 20) : "",
            Status = f["status"] == "inactive" ? "inactive" : "active",
        };
        var errors = new[]
        {
            ValidateName(form.Name), ValidateEmail(form.Email), ValidatePhone(form.Phone, required: kind == "customers"),
        }.Where(e => e is not null).Cast<string>().ToList();
        return (form, errors);
    }

    [HttpGet("new")]
    public IActionResult New(string kind) => View("UserForm", new UserFormVm { Kind = kind });

    [HttpPost("new")]
    public async Task<IActionResult> Create(string kind, string? password)
    {
        var (form, errors) = ReadForm(kind);
        password = (password ?? "").Trim();
        if (password != "" && ValidatePassword(password) is { } pe) errors.Add(pe);
        if (errors.Count == 0 && await db.QueryOneAsync<int?>("SELECT id FROM users WHERE email=@Email", form) is not null)
            errors.Add("An account with this email already exists.");
        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            return View("UserForm", form);
        }
        var generated = password == "";
        if (generated) password = TempPassword();
        var id = (int)await db.InsertAsync(
            "INSERT INTO users (name, email, password_hash, role, phone, room_no, status) " +
            "VALUES (@Name, @Email, @hash, @role, @phone, @room, @Status)",
            new { form.Name, form.Email, hash = BCrypt.Net.BCrypt.HashPassword(password), role = RoleOf(kind),
                  phone = form.Phone == "" ? null : form.Phone, room = form.RoomNo == "" ? null : form.RoomNo, form.Status });
        await notifications.NotifyAsync(id, kind == "sellers"
            ? "Welcome to MealTrack! Your cooking requirements appear on the dashboard."
            : "Welcome to MealTrack! Check the weekly schedule and submit your food counts before each cutoff.",
            kind == "sellers" ? "/seller" : "/customer/schedule");
        Flash("success", $"{form.Name} added." + (generated
            ? $" Temporary password: {password} – share it with them; they can change it on their profile page."
            : ""));
        return Redirect(kind == "sellers" ? $"/admin/sellers/{id}" : $"/admin/customers/{id}");
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(string kind, int id)
    {
        var u = await GetUserAsync(kind, id);
        if (u is null) return NotFound();
        return View("UserForm", new UserFormVm
        {
            Kind = kind, User = u, Name = u.Name, Email = u.Email, Phone = u.Phone ?? "", RoomNo = u.RoomNo ?? "", Status = u.Status,
        });
    }

    [HttpPost("{id:int}/edit")]
    public async Task<IActionResult> Update(string kind, int id)
    {
        var u = await GetUserAsync(kind, id);
        if (u is null) return NotFound();
        var (form, errors) = ReadForm(kind);
        form.User = u;
        if (errors.Count == 0 && await db.QueryOneAsync<int?>("SELECT id FROM users WHERE email=@Email AND id<>@id",
                new { form.Email, id }) is not null)
            errors.Add("Another account already uses this email.");
        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            return View("UserForm", form);
        }
        await db.ExecuteAsync(
            "UPDATE users SET name=@Name, email=@Email, phone=@phone, room_no=@room, status=@Status WHERE id=@id",
            new { form.Name, form.Email, phone = form.Phone == "" ? null : form.Phone,
                  room = form.RoomNo == "" ? null : form.RoomNo, form.Status, id });
        Flash("success", $"{form.Name} updated.");
        return Redirect($"/admin/{kind}/{id}");
    }

    [HttpPost("{id:int}/toggle")]
    public async Task<IActionResult> Toggle(string kind, int id)
    {
        var u = await GetUserAsync(kind, id);
        if (u is null) return NotFound();
        var newStatus = u.Status == "active" ? "inactive" : "active";
        await db.ExecuteAsync("UPDATE users SET status=@newStatus WHERE id=@id", new { newStatus, id });
        Flash("success", newStatus == "inactive"
            ? $"{u.Name} deactivated. They can no longer log in" + (kind == "customers" ? ", and their counts are excluded from totals." : ".")
            : $"{u.Name} activated.");
        var referer = Request.Headers.Referer.ToString();
        return Uri.TryCreate(referer, UriKind.Absolute, out var r) && r.Host == Request.Host.Host
            ? Redirect(r.PathAndQuery) : Redirect($"/admin/{kind}");
    }

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(string kind, int id)
    {
        var u = await GetUserAsync(kind, id);
        if (u is null) return NotFound();
        var password = TempPassword();
        await db.ExecuteAsync("UPDATE users SET password_hash=@hash WHERE id=@id",
            new { hash = BCrypt.Net.BCrypt.HashPassword(password), id });
        await db.ExecuteAsync("UPDATE password_resets SET used=1 WHERE user_id=@id", new { id });
        Flash("success", $"Password for {u.Name} reset. Temporary password: {password}");
        return Redirect($"/admin/{kind}/{id}");
    }

    // ---------------- detail pages ----------------
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(string kind, int id)
    {
        var u = await GetUserAsync(kind, id);
        if (u is null) return NotFound();
        return kind == "sellers" ? await SellerDetailAsync(u) : await CustomerDetailAsync(u);
    }

    private async Task<IActionResult> CustomerDetailAsync(AppUser u)
    {
        var today = DateTime.Today;
        var recent = await db.QueryAsync<HistoryRow>(
            "SELECT s.schedule_date, s.cutoff_time, mt.meal_name, mt.sort_order, f.food_name, " +
            "       c.quantity, c.submission_status, c.submitted_at, c.updated_at, " +
            "       (SELECT COUNT(*) FROM count_change_log l WHERE l.count_id = c.id) AS changes " +
            "FROM food_schedule s JOIN meal_types mt ON mt.id = s.meal_type_id JOIN food_items f ON f.id = s.food_item_id " +
            "LEFT JOIN customer_meal_counts c ON c.food_schedule_id = s.id AND c.customer_id = @id " +
            "WHERE s.is_published = 1 AND s.schedule_date BETWEEN @from AND @to " +
            "ORDER BY s.schedule_date DESC, mt.sort_order",
            new { id = u.Id, from = today.AddDays(-29), to = today.AddDays(7) });
        foreach (var r in recent) r.Status = Cutoff.CountStatus(r.SubmissionStatus, r.CutoffTime);
        var past = recent.Where(r => r.ScheduleDate <= today).ToList();

        var monthly = await db.QueryAsync<MonthlyRow>(
            "SELECT DATE_FORMAT(s.schedule_date, '%Y-%m') AS ym, DATE_FORMAT(MIN(s.schedule_date), '%b %Y') AS label, " +
            "       SUM(CASE WHEN mt.meal_name='Breakfast' THEN c.quantity ELSE 0 END) AS breakfast, " +
            "       SUM(CASE WHEN mt.meal_name='Lunch' THEN c.quantity ELSE 0 END) AS lunch, " +
            "       SUM(CASE WHEN mt.meal_name='Dinner' THEN c.quantity ELSE 0 END) AS dinner, SUM(c.quantity) AS total " +
            "FROM customer_meal_counts c JOIN food_schedule s ON s.id = c.food_schedule_id JOIN meal_types mt ON mt.id = s.meal_type_id " +
            "WHERE c.customer_id = @id AND c.submission_status IN ('submitted','locked') " +
            "GROUP BY ym ORDER BY ym DESC LIMIT 6", new { id = u.Id });

        return View("CustomerDetail", new CustomerDetailVm
        {
            User = u, Recent = recent, Monthly = monthly,
            Submitted30 = past.Count(r => r.Status is "locked" or "submitted"),
            Missed30 = past.Count(r => r.Status == "missed"),
            Portions30 = past.Where(r => r.Status is "locked" or "submitted").Sum(r => r.Quantity ?? 0),
        });
    }

    private async Task<IActionResult> SellerDetailAsync(AppUser u)
    {
        var today = DateTime.Today;
        var defaults = (await db.QueryAsync<SellerDefault>(
                "SELECT meal_type_id, can_view_customers FROM seller_meal_defaults WHERE seller_id=@id", new { id = u.Id }))
            .ToDictionary(d => d.MealTypeId);
        var upcoming = await db.QueryAsync<AssignmentRow>(
            SellerController.AssignmentSql + "AND s.schedule_date BETWEEN @from AND @to " + SellerController.AssignmentGroupBy,
            new { sellerId = u.Id, from = today, to = today.AddDays(13) });
        var activity = await db.QueryAsync<PrepRecord>(
            SellerController.PrepSql + "WHERE p.seller_id=@id ORDER BY p.updated_at DESC LIMIT 15", new { id = u.Id });
        return View("SellerDetail", new SellerDetailVm
        {
            User = u, MealTypes = await schedules.MealTypesAsync(), Defaults = defaults, Upcoming = upcoming, Activity = activity,
        });
    }

    /// <summary>Default meal responsibilities; also updates upcoming assignments.</summary>
    [HttpPost("{id:int}/defaults")]
    public async Task<IActionResult> SaveDefaults(string kind, int id)
    {
        if (kind != "sellers") return NotFound();
        var u = await GetUserAsync(kind, id);
        if (u is null) return NotFound();
        var mealTypes = await schedules.MealTypesAsync();
        var today = DateTime.Today;
        await db.InTransactionAsync(async (conn, tx) =>
        {
            foreach (var m in mealTypes)
            {
                var on = Request.Form[$"meal_{m.Id}"] == "1";
                var canView = Request.Form[$"view_{m.Id}"] == "1";
                var p = new { sellerId = id, mealTypeId = m.Id, canView, today };
                if (on)
                {
                    await Dapper.SqlMapper.ExecuteAsync(conn,
                        "INSERT INTO seller_meal_defaults (seller_id, meal_type_id, can_view_customers) VALUES (@sellerId, @mealTypeId, @canView) " +
                        "ON DUPLICATE KEY UPDATE can_view_customers=@canView", p, tx);
                    // Assign upcoming meals of this type
                    await Dapper.SqlMapper.ExecuteAsync(conn,
                        "INSERT INTO seller_assignments (seller_id, food_schedule_id, can_view_customers) " +
                        "SELECT @sellerId, s.id, @canView FROM food_schedule s WHERE s.meal_type_id=@mealTypeId AND s.schedule_date >= @today " +
                        "ON DUPLICATE KEY UPDATE can_view_customers=@canView", p, tx);
                }
                else
                {
                    await Dapper.SqlMapper.ExecuteAsync(conn,
                        "DELETE FROM seller_meal_defaults WHERE seller_id=@sellerId AND meal_type_id=@mealTypeId", p, tx);
                    // Remove upcoming assignments that have no preparation record yet
                    await Dapper.SqlMapper.ExecuteAsync(conn,
                        "DELETE a FROM seller_assignments a JOIN food_schedule s ON s.id=a.food_schedule_id " +
                        "LEFT JOIN food_preparation fp ON fp.food_schedule_id=s.id AND fp.seller_id=a.seller_id " +
                        "WHERE a.seller_id=@sellerId AND s.meal_type_id=@mealTypeId AND s.schedule_date >= @today AND fp.id IS NULL", p, tx);
                }
            }
        });
        await notifications.NotifyAsync(id, "Your meal responsibilities were updated by the admin.", "/seller/assignments");
        Flash("success", $"Responsibilities for {u.Name} saved. Upcoming assignments were updated to match.");
        return Redirect($"/admin/sellers/{id}");
    }
}
