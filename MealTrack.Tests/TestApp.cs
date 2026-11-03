using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using MealTrack.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MealTrack.Tests;

/// <summary>
/// Starts the real app against a separate MySQL database (mealtrack_test), recreated
/// once per test run. Uses the same MySQL user/password as appsettings.Local.json.
/// </summary>
public class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestPassword = "Test@1234";
    private static readonly string Hash = BCrypt.Net.BCrypt.HashPassword(TestPassword, 4);
    private int _seq;

    public TestApp()
    {
        Environment.SetEnvironmentVariable("Database__Name", "mealtrack_test");
        Environment.SetEnvironmentVariable("MealTrack__SweepIntervalSeconds", "3600");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    public Db Db => Services.GetRequiredService<Db>();

    public async Task InitializeAsync()
    {
        var env = Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal("mealtrack_test", Db.DatabaseName); // never touch the real database
        await DbInitializer.ResetAsync(Db, env.ContentRootPath, withDemo: false);
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    // ---------------- test data helpers ----------------
    public async Task<int> AddUserAsync(string role, string status = "active", string? name = null)
    {
        var n = Interlocked.Increment(ref _seq);
        return await Db.ScalarAsync<int>(
            "INSERT INTO users (name, email, password_hash, role, room_no, status) VALUES (@name, @email, @Hash, @role, @room, @status); " +
            "SELECT LAST_INSERT_ID();",
            new { name = name ?? $"{role} {n}", email = $"{role}{n}.{Guid.NewGuid():N}@test.local", Hash, role, room = $"R{n}", status });
    }

    public async Task<string> EmailOfAsync(int userId) =>
        await Db.ScalarAsync<string>("SELECT email FROM users WHERE id=@userId", new { userId }, "");

    /// <summary>A unique future date so tests never collide on UNIQUE(schedule_date, meal_type_id).</summary>
    public DateTime NextDate() => DateTime.Today.AddDays(100 + Interlocked.Increment(ref _seq));

    public DateTime PastDate() => DateTime.Today.AddDays(-100 - Interlocked.Increment(ref _seq));

    public Task<int> MealIdAsync(string meal) =>
        Db.ScalarAsync<int>("SELECT id FROM meal_types WHERE meal_name=@meal", new { meal });

    public async Task<int> AddScheduleAsync(DateTime date, string meal, DateTime? cutoff = null, bool published = true)
    {
        var mealId = await MealIdAsync(meal);
        var foodId = await Db.ScalarAsync<int>("SELECT id FROM food_items ORDER BY id LIMIT 1");
        return await Db.ScalarAsync<int>(
            "INSERT INTO food_schedule (schedule_date, meal_type_id, food_item_id, cutoff_time, is_published) " +
            "VALUES (@date, @mealId, @foodId, @cutoff, @published); SELECT LAST_INSERT_ID();",
            new { date, mealId, foodId, cutoff = cutoff ?? date.AddHours(-2), published });
    }

    public Task AddCountAsync(int customerId, int scheduleId, int qty, string status = "submitted") => Db.ExecuteAsync(
        "INSERT INTO customer_meal_counts (customer_id, food_schedule_id, quantity, submission_status, submitted_at, updated_at) " +
        "VALUES (@customerId, @scheduleId, @qty, @status, NOW(), NOW())",
        new { customerId, scheduleId, qty, status });

    /// <summary>An HttpClient logged in as the given user (cookies kept, redirects not followed).</summary>
    public async Task<HttpClient> LoginAsync(int userId)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = await AntiforgeryTokenAsync(client, "/login");
        var resp = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = await EmailOfAsync(userId), ["password"] = TestPassword, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.DoesNotContain("/login", resp.Headers.Location?.ToString() ?? "");
        return client;
    }

    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(m.Success, $"No antiforgery token on {url}");
        return m.Groups[1].Value;
    }
}

[CollectionDefinition("app")]
public class AppCollection : ICollectionFixture<TestApp> { }
