using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MealTrack.Data;
using MealTrack.Infrastructure;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using static MealTrack.Infrastructure.Validators;

namespace MealTrack.Controllers;

/// <summary>Common pages: login, registration, password reset, profile, notifications, logout.</summary>
public class AuthController(Db db, NotificationService notifications, IOptions<AppOptions> options,
    ILogger<AuthController> log) : AppController
{
    // Keeps login timing similar whether or not the email exists.
    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword("not-a-real-password-123"));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ---------------- login / logout ----------------
    [HttpGet("/login")]
    public IActionResult Login()
    {
        if (HttpContext.CurrentUser() is { } u) return Redirect(HomeUrlFor(u.Role));
        ViewData["Email"] = "";
        return View();
    }

    [HttpPost("/login")]
    public async Task<IActionResult> Login(string? email, string? password, [FromQuery] string? next)
    {
        if (HttpContext.CurrentUser() is { } current) return Redirect(HomeUrlFor(current.Role));
        email = Clean(email, 150).ToLowerInvariant();
        var user = await db.QueryOneAsync<AppUser>("SELECT * FROM users WHERE email=@email", new { email });
        var valid = BCrypt.Net.BCrypt.Verify(password ?? "", user?.PasswordHash ?? DummyHash.Value);

        if (user is null || !valid)
        {
            Flash("danger", "Invalid email or password.");
        }
        else if (user.Status != "active")
        {
            Flash("danger", "Your account is inactive. Please contact the admin.");
        }
        else
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Name),
                new(ClaimTypes.Role, user.Role),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });

            Flash("success", $"Welcome back, {user.FirstName}!");
            var nxt = SafeNext(next);
            if (nxt is not null && (nxt.StartsWith("/" + user.Role) || nxt.StartsWith("/profile") || nxt.StartsWith("/notifications")))
                return Redirect(nxt);
            return Redirect(HomeUrlFor(user.Role));
        }
        ViewData["Email"] = email;
        return View();
    }

    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        Flash("info", "You have been logged out.");
        return Redirect("/login");
    }

    // ---------------- registration ----------------
    [HttpGet("/register")]
    public IActionResult Register()
    {
        if (HttpContext.CurrentUser() is { } u) return Redirect(HomeUrlFor(u.Role));
        return View(new RegisterForm());
    }

    [HttpPost("/register")]
    public async Task<IActionResult> Register(string? name, string? email, string? phone, [FromForm(Name = "room_no")] string? roomNo,
        string? password, [FromForm(Name = "confirm_password")] string? confirmPassword)
    {
        if (HttpContext.CurrentUser() is { } u) return Redirect(HomeUrlFor(u.Role));
        var form = new RegisterForm
        {
            Name = Clean(name, 100),
            Email = Clean(email, 150).ToLowerInvariant(),
            Phone = Clean(phone, 15),
            RoomNo = Clean(roomNo, 20),
        };
        password ??= "";
        var errors = new[]
        {
            ValidateName(form.Name), ValidateEmail(form.Email),
            ValidatePhone(form.Phone, required: true), ValidatePassword(password),
        }.Where(e => e is not null).Cast<string>().ToList();
        if (password != (confirmPassword ?? "")) errors.Add("Passwords do not match.");
        if (errors.Count == 0 && await db.QueryOneAsync<int?>("SELECT id FROM users WHERE email=@Email", form) is not null)
            errors.Add("An account with this email already exists.");

        if (errors.Count > 0)
        {
            errors.ForEach(e => Flash("danger", e));
            return View(form);
        }

        var userId = await db.InsertAsync(
            "INSERT INTO users (name, email, password_hash, role, phone, room_no, status) " +
            "VALUES (@Name, @Email, @hash, 'customer', @Phone, @room, 'active')",
            new { form.Name, form.Email, hash = BCrypt.Net.BCrypt.HashPassword(password), form.Phone,
                  room = string.IsNullOrEmpty(form.RoomNo) ? null : form.RoomNo });
        await notifications.NotifyAsync((int)userId,
            "Welcome to MealTrack! Check the weekly schedule and submit your food counts before each cutoff.",
            "/customer/schedule");
        Flash("success", "Registration successful. Please log in.");
        return Redirect("/login");
    }

    // ---------------- password reset ----------------
    [HttpGet("/forgot-password")]
    public IActionResult ForgotPassword() => View();

    [HttpPost("/forgot-password")]
    public async Task<IActionResult> ForgotPassword(string? email)
    {
        email = Clean(email, 150).ToLowerInvariant();
        var user = await db.QueryOneAsync<AppUser>("SELECT id, status FROM users WHERE email=@email", new { email });
        if (user is not null && user.Status == "active")
        {
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await db.ExecuteAsync(
                "INSERT INTO password_resets (user_id, token_hash, expires_at) VALUES (@userId, @hash, @expires)",
                new { userId = user.Id, hash = Sha256Hex(token),
                      expires = DateTime.Now.AddMinutes(options.Value.PasswordResetMinutes) });
            var link = Url.Action(nameof(ResetPassword), "Auth", new { token }, Request.Scheme);
            // No mail server configured: the link is written to the server console.
            log.LogWarning("PASSWORD RESET LINK for {Email}: {Link}", email, link);
            Console.WriteLine($"\n*** MealTrack password reset link for {email}:\n{link}\n");
        }
        Flash("info", "If an account exists for that email, a reset link has been generated. " +
                      "In this local setup the link is printed in the server console window, " +
                      "or the admin can reset your password.");
        return Redirect("/login");
    }

    private Task<PasswordReset?> FindResetAsync(string token) =>
        db.QueryOneAsync<PasswordReset>(
            "SELECT id, user_id FROM password_resets WHERE token_hash=@hash AND used=0 AND expires_at > @now",
            new { hash = Sha256Hex(token), now = DateTime.Now });

    [HttpGet("/reset-password/{token}")]
    public async Task<IActionResult> ResetPassword(string token)
    {
        if (await FindResetAsync(token) is null)
        {
            Flash("danger", "This reset link is invalid or has expired.");
            return Redirect("/forgot-password");
        }
        return View(model: token);
    }

    [HttpPost("/reset-password/{token}")]
    public async Task<IActionResult> ResetPassword(string token, string? password,
        [FromForm(Name = "confirm_password")] string? confirmPassword)
    {
        var reset = await FindResetAsync(token);
        if (reset is null)
        {
            Flash("danger", "This reset link is invalid or has expired.");
            return Redirect("/forgot-password");
        }
        password ??= "";
        var error = ValidatePassword(password);
        if (error is null && password != confirmPassword) error = "Passwords do not match.";
        if (error is not null)
        {
            Flash("danger", error);
            return View(model: token);
        }
        var userId = reset.UserId;
        await db.InTransactionAsync(async (conn, tx) =>
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE users SET password_hash=@hash WHERE id=@userId",
                new { hash = BCrypt.Net.BCrypt.HashPassword(password), userId }, tx);
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE password_resets SET used=1 WHERE user_id=@userId",
                new { userId }, tx);
        });
        Flash("success", "Password updated. Please log in.");
        return Redirect("/login");
    }

    // ---------------- profile ----------------
    [Authorize]
    [HttpGet("/profile")]
    public IActionResult Profile() => View(Me);

    [Authorize]
    [HttpPost("/profile")]
    public async Task<IActionResult> Profile([FromForm(Name = "action")] string? formAction, string? name, string? phone,
        [FromForm(Name = "room_no")] string? roomNo,
        [FromForm(Name = "current_password")] string? currentPassword,
        [FromForm(Name = "new_password")] string? newPassword,
        [FromForm(Name = "confirm_password")] string? confirmPassword)
    {
        var me = Me;
        if (formAction == "details")
        {
            name = Clean(name, 100);
            phone = Clean(phone, 15);
            string? room = Clean(roomNo, 20);
            if (room == "") room = null;
            var errors = new[] { ValidateName(name), ValidatePhone(phone) }.Where(e => e is not null).ToList();
            if (errors.Count > 0)
            {
                errors.ForEach(e => Flash("danger", e!));
            }
            else
            {
                if (me.Role != Roles.Customer) room = me.RoomNo;
                await db.ExecuteAsync("UPDATE users SET name=@name, phone=@phone, room_no=@room WHERE id=@id",
                    new { name, phone = phone == "" ? null : phone, room, id = me.Id });
                Flash("success", "Profile updated.");
            }
        }
        else if (formAction == "password")
        {
            var stored = await db.ScalarAsync<string>("SELECT password_hash FROM users WHERE id=@id", new { id = me.Id }, "");
            string? error;
            if (!BCrypt.Net.BCrypt.Verify(currentPassword ?? "", stored))
            {
                error = "Current password is incorrect.";
            }
            else
            {
                error = ValidatePassword(newPassword ?? "");
                if (error is null && newPassword != confirmPassword) error = "New passwords do not match.";
            }
            if (error is not null)
            {
                Flash("danger", error);
            }
            else
            {
                await db.ExecuteAsync("UPDATE users SET password_hash=@hash WHERE id=@id",
                    new { hash = BCrypt.Net.BCrypt.HashPassword(newPassword), id = me.Id });
                Flash("success", "Password changed successfully.");
            }
        }
        return Redirect("/profile");
    }

    // ---------------- notifications (all roles) ----------------
    [Authorize]
    [HttpGet("/notifications")]
    public async Task<IActionResult> Notifications(string? page)
    {
        const int perPage = 20;
        var p = ParseInt(page, minimum: 1) ?? 1;
        var total = await db.ScalarAsync<int>("SELECT COUNT(*) FROM notifications WHERE user_id=@id", new { id = Me.Id });
        var items = await db.QueryAsync<NotificationItem>(
            "SELECT id, message, link, is_read, created_at FROM notifications WHERE user_id=@id " +
            "ORDER BY created_at DESC, id DESC LIMIT @perPage OFFSET @offset",
            new { id = Me.Id, perPage, offset = (p - 1) * perPage });
        return View(new NotificationsPageVm
        {
            Items = items, Page = p, Total = total, Unread = await notifications.UnreadCountAsync(Me.Id), Pages = Math.Max(1, (total + perPage - 1) / perPage),
        });
    }

    [Authorize]
    [HttpPost("/notifications/{nid:int}/open")]
    public async Task<IActionResult> OpenNotification(int nid)
    {
        var item = await db.QueryOneAsync<NotificationItem>(
            "SELECT id, link FROM notifications WHERE id=@nid AND user_id=@uid", new { nid, uid = Me.Id });
        if (item is null) return NotFound();
        await notifications.MarkReadAsync(Me.Id, nid);
        return Redirect(SafeNext(item.Link) ?? "/notifications");
    }

    [Authorize]
    [HttpPost("/notifications/read-all")]
    public async Task<IActionResult> ReadAllNotifications(string? next)
    {
        await notifications.MarkAllReadAsync(Me.Id);
        Flash("success", "All notifications marked as read.");
        return Redirect(SafeNext(next) ?? "/notifications");
    }
}

public class PasswordReset
{
    public int Id { get; set; }
    public int UserId { get; set; }
}
