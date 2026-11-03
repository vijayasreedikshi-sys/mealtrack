using System.Globalization;
using Dapper;
using MealTrack.Data;
using MealTrack.Infrastructure;
using MealTrack.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

// Dates like "Mon, 03 Nov 2026, 08:00 PM" regardless of the PC's regional settings.
var culture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

// snake_case columns (schedule_date) map to PascalCase properties (ScheduleDate).
DefaultTypeMap.MatchNamesWithUnderscores = true;

// `dotnet run -- init-db [--reset] [--no-demo]` creates the database instead of starting the site.
var initDb = args.Length > 0 && args[0].Equals("init-db", StringComparison.OrdinalIgnoreCase);

var builder = WebApplication.CreateBuilder(initDb ? [] : args);

// Machine-specific settings (MySQL password) live in appsettings.Local.json, which is not committed.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(); // environment variables still win (the tests use this to pick mealtrack_test)

builder.Services.Configure<DbOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("MealTrack"));

builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<CountService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<AttendanceService>();
builder.Services.AddHostedService<CutoffSweepService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "MealTrack.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.LoginPath = "/login";
        o.ReturnUrlParameter = "next";
        o.Events.OnRedirectToLogin = ctx =>
        {
            var tempData = ctx.HttpContext.RequestServices
                .GetRequiredService<ITempDataDictionaryFactory>().GetTempData(ctx.HttpContext);
            tempData.AddFlash("info", "Please log in to continue.");
            tempData.Save();
            ctx.Response.Redirect("/login?next=" + Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString));
            return Task.CompletedTask;
        };
        // Wrong role -> 403 page (rendered by ErrorController through status code pages).
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); // CSRF check on every POST
    o.Filters.Add<AntiforgeryFlashFilter>();
});
builder.Services.AddAntiforgery(o => o.Cookie.Name = "MealTrack.Csrf");

var app = builder.Build();

if (initDb)
{
    return await DbInitializer.RunAsync(app.Services, args.Skip(1).ToArray(), app.Environment.ContentRootPath);
}

// Add any tables/columns introduced since the database was created (keeps existing data).
await DbInitializer.UpgradeAsync(app.Services.GetRequiredService<Db>(), app.Environment.ContentRootPath, app.Logger);

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
    app.UseExceptionHandler("/error/500");

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>(); // reloads the user each request, logs out inactive users
app.UseAuthorization();

app.MapControllers();

await app.RunAsync();
return 0;

/// <summary>Lets the integration tests start the app (WebApplicationFactory).</summary>
public partial class Program { }
