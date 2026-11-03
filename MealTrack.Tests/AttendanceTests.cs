using System.Net;
using MealTrack.Models;
using MealTrack.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MealTrack.Tests;

/// <summary>Meal check-in by QR: token rules, duplicates, codes, access and no-show numbers.</summary>
[Collection("app")]
public class AttendanceTests(TestApp app)
{
    private AttendanceService Attendance => app.Services.CreateScope().ServiceProvider.GetRequiredService<AttendanceService>();

    /// <summary>Today's schedule for a meal (only one per date + meal can exist, so share it).</summary>
    private async Task<int> TodayAsync(string meal)
    {
        var mealId = await app.MealIdAsync(meal);
        var id = await app.Db.ScalarAsync<int>(
            "SELECT id FROM food_schedule WHERE schedule_date=CURDATE() AND meal_type_id=@mealId", new { mealId });
        return id > 0 ? id : await app.AddScheduleAsync(DateTime.Today, meal, cutoff: DateTime.Now.AddHours(-1));
    }

    private async Task<string> TokenAsync(int scheduleId)
    {
        var svc = Attendance;
        return AttendanceService.Token(scheduleId, await svc.SecretAsync((await svc.MealAsync(scheduleId))!));
    }

    private Task<int> RowsAsync(int scheduleId, int customerId) => app.Db.ScalarAsync<int>(
        "SELECT COUNT(*) FROM meal_attendance WHERE food_schedule_id=@scheduleId AND customer_id=@customerId",
        new { scheduleId, customerId });

    [Fact]
    public async Task Scanning_the_meal_QR_marks_it_eaten_once()
    {
        var s = await TodayAsync("Breakfast");
        var customer = await app.AddUserAsync("customer");
        await app.AddCountAsync(customer, s, 1, "locked");
        var client = await app.LoginAsync(customer);
        var token = await TokenAsync(s);

        Assert.Contains("Breakfast marked as eaten", await client.GetStringAsync($"/scan/{token}")); // no extra button
        Assert.Contains("Already marked for Breakfast", await client.GetStringAsync($"/scan/{token}"));
        Assert.Equal(1, await RowsAsync(s, customer));
    }

    [Fact]
    public async Task One_QR_per_meal_per_day()
    {
        var breakfast = await TodayAsync("Breakfast");
        var lunch = await TodayAsync("Lunch");
        var tomorrow = await app.AddScheduleAsync(app.NextDate(), "Breakfast");
        var first = await TokenAsync(breakfast);
        Assert.Equal(first, await TokenAsync(breakfast));          // same QR all day
        Assert.NotEqual(first, await TokenAsync(lunch));          // each meal has its own
        Assert.NotEqual(first, await TokenAsync(tomorrow));       // next day gets a new one
    }

    [Fact]
    public async Task Tampered_or_replaced_QR_is_refused()
    {
        var s = await TodayAsync("Dinner");
        var customer = await app.AddUserAsync("customer");
        var client = await app.LoginAsync(customer);

        var good = await TokenAsync(s);
        var tampered = good[..^1] + (good[^1] == '0' ? '1' : '0');
        Assert.Contains("not recognised", await client.GetStringAsync($"/scan/{tampered}"));

        await Attendance.RegenerateAsync(s); // "New QR"
        Assert.Contains("not recognised", await client.GetStringAsync($"/scan/{good}"));
        Assert.Equal(0, await RowsAsync(s, customer));
    }

    [Fact]
    public async Task QR_of_another_day_is_refused()
    {
        var s = await app.AddScheduleAsync(app.NextDate(), "Lunch");
        var customer = await app.AddUserAsync("customer");
        var client = await app.LoginAsync(customer);
        Assert.Contains("Not today", await client.GetStringAsync($"/scan/{await TokenAsync(s)}"));
        Assert.Equal(0, await RowsAsync(s, customer));
    }

    [Fact]
    public async Task Typed_code_checks_in()
    {
        var s = await TodayAsync("Lunch");
        var svc = Attendance;
        var code = AttendanceService.Code(s, await svc.SecretAsync((await svc.MealAsync(s))!));
        var customer = await app.AddUserAsync("customer");
        var client = await app.LoginAsync(customer);

        var csrf = await TestApp.AntiforgeryTokenAsync(client, "/check-in");
        var resp = await client.PostAsync("/check-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["code"] = code,
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(1, await RowsAsync(s, customer));
    }

    [Fact]
    public async Task Only_assigned_sellers_and_admins_open_the_counter_screen()
    {
        var s = await app.AddScheduleAsync(app.PastDate(), "Dinner");
        var seller = await app.AddUserAsync("seller");
        var sellerClient = await app.LoginAsync(seller);
        Assert.Equal(HttpStatusCode.Forbidden, (await sellerClient.GetAsync($"/attendance/{s}")).StatusCode);
        await app.Db.ExecuteAsync("INSERT INTO seller_assignments (seller_id, food_schedule_id) VALUES (@seller, @s)", new { seller, s });
        Assert.Equal(HttpStatusCode.OK, (await sellerClient.GetAsync($"/attendance/{s}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sellerClient.GetAsync($"/attendance/{s}/live")).StatusCode);

        var admin = await app.LoginAsync(await app.AddUserAsync("admin"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/attendance/{s}/list")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/attendance/{s}/print")).StatusCode);

        var customer = await app.LoginAsync(await app.AddUserAsync("customer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/attendance/{s}")).StatusCode);
    }

    [Fact]
    public async Task No_shows_are_applied_customers_who_did_not_check_in()
    {
        var s = await app.AddScheduleAsync(app.PastDate(), "Lunch");
        int came = await app.AddUserAsync("customer"), noShow = await app.AddUserAsync("customer"),
            zero = await app.AddUserAsync("customer"), walkIn = await app.AddUserAsync("customer");
        await app.AddCountAsync(came, s, 1, "locked");
        await app.AddCountAsync(noShow, s, 1, "locked");
        await app.AddCountAsync(zero, s, 0, "locked"); // said "not eating" – neither applied nor a no-show
        var svc = Attendance;
        await svc.CheckInAsync(s, came, "qr", null, DateTime.Now);
        await svc.CheckInAsync(s, walkIn, "qr", null, DateTime.Now);

        var sum = await svc.SummaryAsync(s);
        Assert.Equal(2, sum.AppliedCustomers);
        Assert.Equal(2, sum.AppliedPortions);
        Assert.Equal(1, sum.Came);
        Assert.Equal(1, sum.NoShows);
        Assert.Equal(1, sum.NoShowPortions);
        Assert.Equal(1, sum.WalkIns);

        var row = (await svc.ReportRowsAsync(DateTime.Today.AddYears(-1), DateTime.Today)).Single(r => r.ScheduleId == s);
        Assert.Equal((2, 1, 1, 1), (row.Applied, row.Came, row.NoShowPortions, row.WalkIns));
    }
}
