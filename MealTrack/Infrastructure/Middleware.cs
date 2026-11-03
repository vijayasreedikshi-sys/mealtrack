using System.Security.Claims;
using MealTrack.Data;
using MealTrack.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MealTrack.Infrastructure;

public static class CurrentUserExtensions
{
    private const string Key = "MealTrack.CurrentUser";

    public static AppUser? CurrentUser(this HttpContext ctx) => ctx.Items[Key] as AppUser;

    internal static void SetCurrentUser(this HttpContext ctx, AppUser user) => ctx.Items[Key] = user;
}

/// <summary>
/// Runs on every request after cookie authentication: reloads the user from the
/// database and signs them out if the account was deactivated or its role changed.
/// </summary>
public class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, Db db, ITempDataDictionaryFactory tempDataFactory)
    {
        if (ctx.User.Identity?.IsAuthenticated == true
            && int.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var user = await db.QueryOneAsync<AppUser>(
                "SELECT id, name, email, role, phone, room_no, status, created_at FROM users WHERE id=@userId",
                new { userId });
            if (user is null || user.Status != "active" || user.Role != ctx.User.FindFirstValue(ClaimTypes.Role))
            {
                await ctx.SignOutAsync();
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
                var tempData = tempDataFactory.GetTempData(ctx);
                tempData.AddFlash("warning", "Your session has ended. Please log in again.");
                tempData.Save();
            }
            else
            {
                ctx.SetCurrentUser(user);
            }
        }
        await next(ctx);
    }
}

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "SAMEORIGIN";
            h["Referrer-Policy"] = "same-origin";
            if (ctx.CurrentUser() is not null)
                h.CacheControl = "no-store"; // back button after logout shows nothing
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}

/// <summary>An expired/missing CSRF token redirects back with a message instead of a bare 400.</summary>
public class AntiforgeryFlashFilter(ITempDataDictionaryFactory tempDataFactory) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult) return;
        var http = context.HttpContext;
        tempDataFactory.GetTempData(http).AddFlash("warning", "Your form expired. Please try again.");
        var target = "/";
        if (Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            && referer.Host == http.Request.Host.Host)
            target = referer.PathAndQuery;
        context.Result = new RedirectResult(target);
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
