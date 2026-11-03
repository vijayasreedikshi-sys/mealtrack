using System.Net;
using MealTrack.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MealTrack.Tests;

/// <summary>End-to-end checks through HTTP: submission rules and role-based access.</summary>
[Collection("app")]
public class WebTests(TestApp app)
{
    private async Task<HttpResponseMessage> SubmitAsync(HttpClient client, DateTime day, int scheduleId, int qty)
    {
        var token = await TestApp.AntiforgeryTokenAsync(client, $"/customer/submit?date={day:yyyy-MM-dd}");
        return await client.PostAsync("/customer/submit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["date"] = day.ToString("yyyy-MM-dd"), ["view"] = "day",
            [$"qty_{scheduleId}"] = qty.ToString(),
        }));
    }

    private Task<int> RowsAsync(int customerId, int scheduleId) => app.Db.ScalarAsync<int>(
        "SELECT COUNT(*) FROM customer_meal_counts WHERE customer_id=@customerId AND food_schedule_id=@scheduleId",
        new { customerId, scheduleId });

    [Fact]
    public async Task Editing_a_count_updates_it_without_double_counting()
    {
        var day = app.NextDate();
        var s = await app.AddScheduleAsync(day, "Lunch");
        var customer = await app.AddUserAsync("customer");
        var client = await app.LoginAsync(customer);

        Assert.Equal(HttpStatusCode.Redirect, (await SubmitAsync(client, day, s, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SubmitAsync(client, day, s, 0)).StatusCode);

        Assert.Equal(1, await RowsAsync(customer, s));
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CountService>().ScheduleTotalAsync(s));
        Assert.Equal(2, await app.Db.ScalarAsync<int>(
            "SELECT COUNT(*) FROM count_change_log l JOIN customer_meal_counts c ON c.id=l.count_id " +
            "WHERE c.customer_id=@customer AND c.food_schedule_id=@s", new { customer, s }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task More_than_one_portion_per_meal_is_rejected(int qty)
    {
        var day = app.NextDate();
        var s = await app.AddScheduleAsync(day, "Dinner");
        var customer = await app.AddUserAsync("customer");
        var client = await app.LoginAsync(customer);

        await SubmitAsync(client, day, s, 1);
        await SubmitAsync(client, day, s, qty); // refused: one person, one portion

        Assert.Equal(1, await RowsAsync(customer, s));
        Assert.Equal(1, await app.Db.ScalarAsync<int>(
            "SELECT quantity FROM customer_meal_counts WHERE customer_id=@customer AND food_schedule_id=@s", new { customer, s }));
    }

    [Fact]
    public async Task Database_refuses_more_than_one_portion()
    {
        var s = await app.AddScheduleAsync(app.NextDate(), "Lunch");
        var customer = await app.AddUserAsync("customer");
        await Assert.ThrowsAnyAsync<Exception>(() => app.AddCountAsync(customer, s, 2));
    }

    [Fact]
    public async Task Submitting_after_the_cutoff_is_rejected()
    {
        var day = app.PastDate();
        var s = await app.AddScheduleAsync(day, "Dinner", cutoff: DateTime.Now.AddMinutes(-5));
        var customer = await app.AddUserAsync("customer");
        var client = await app.LoginAsync(customer);

        await SubmitAsync(client, day, s, 2);
        Assert.Equal(0, await RowsAsync(customer, s));
    }

    [Fact]
    public async Task Seller_cannot_open_an_unassigned_schedule()
    {
        var s = await app.AddScheduleAsync(app.PastDate(), "Lunch");
        var seller = await app.AddUserAsync("seller");
        var client = await app.LoginAsync(seller);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/seller/prep/{s}")).StatusCode);

        // ...but can once assigned
        await app.Db.ExecuteAsync("INSERT INTO seller_assignments (seller_id, food_schedule_id) VALUES (@seller, @s)", new { seller, s });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/seller/prep/{s}")).StatusCode);
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/schedules")]
    [InlineData("/admin/reports")]
    [InlineData("/seller")]
    public async Task Customer_is_blocked_from_admin_and_seller_pages(string url)
    {
        var client = await app.LoginAsync(await app.AddUserAsync("customer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Seller_is_blocked_from_admin_pages()
    {
        var client = await app.LoginAsync(await app.AddUserAsync("seller"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/customers")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_users_are_sent_to_login()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/customer/history");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith("/login", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Inactive_user_cannot_log_in()
    {
        var id = await app.AddUserAsync("customer", "inactive");
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await TestApp.AntiforgeryTokenAsync(client, "/login");
        var resp = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = await app.EmailOfAsync(id), ["password"] = TestApp.TestPassword, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // stays on the login page
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/schedules")]
    [InlineData("/admin/schedules/builder")]
    [InlineData("/admin/cutoffs")]
    [InlineData("/admin/food-items")]
    [InlineData("/admin/customers")]
    [InlineData("/admin/sellers")]
    [InlineData("/admin/counts?tab=pending")]
    [InlineData("/admin/reports?type=daily")]
    [InlineData("/admin/reports?type=weekly")]
    [InlineData("/admin/reports?type=monthly")]
    [InlineData("/admin/reports?type=customer")]
    [InlineData("/admin/reports?type=seller")]
    [InlineData("/admin/reports?type=wastage")]
    [InlineData("/admin/reports?type=daily&format=csv")]
    [InlineData("/admin/reports?type=wastage&format=xlsx")]
    [InlineData("/admin/reports?type=attendance")]
    [InlineData("/admin/reports?type=attendance&format=xlsx")]
    [InlineData("/attendance")]
    [InlineData("/admin/api/consumption")]
    [InlineData("/admin/api/meal-split")]
    [InlineData("/admin/api/top-foods")]
    [InlineData("/admin/api/preparation")]
    [InlineData("/admin/api/wastage")]
    public async Task Admin_pages_render(string url)
    {
        var client = await app.LoginAsync(await app.AddUserAsync("admin"));
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
