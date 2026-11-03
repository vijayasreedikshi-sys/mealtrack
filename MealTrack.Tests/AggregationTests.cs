using MealTrack.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MealTrack.Tests;

/// <summary>Automatic food-count totals (CountService is the single source of truth).</summary>
[Collection("app")]
public class AggregationTests(TestApp app)
{
    private CountService Counts => app.Services.CreateScope().ServiceProvider.GetRequiredService<CountService>();

    [Fact]
    public async Task Seven_customers_one_portion_each_totals_6_4_4_equals_14()
    {
        var day = app.NextDate();
        var b = await app.AddScheduleAsync(day, "Breakfast");
        var l = await app.AddScheduleAsync(day, "Lunch");
        var d = await app.AddScheduleAsync(day, "Dinner");
        int[][] table = // customer -> (breakfast, lunch, dinner); max 1 portion per meal
        [
            [1, 1, 1],
            [1, 1, 1],
            [1, 1, 0],
            [1, 0, 1],
            [1, 0, 1],
            [1, 1, 0],
            [0, 0, 0],
        ];
        foreach (var q in table)
        {
            var c = await app.AddUserAsync("customer");
            await app.AddCountAsync(c, b, q[0]);
            await app.AddCountAsync(c, l, q[1]);
            await app.AddCountAsync(c, d, q[2]);
        }

        var (totals, rows) = await Counts.MealTotalsForDateAsync(day);
        Assert.Equal(6, totals["Breakfast"]);
        Assert.Equal(4, totals["Lunch"]);
        Assert.Equal(4, totals["Dinner"]);
        Assert.Equal(14, totals["Total"]);
        Assert.All(rows, r => Assert.Equal(7, r.Submitters)); // a 0 still counts as a submission
    }

    [Fact]
    public async Task Zero_is_a_submission_but_adds_nothing()
    {
        var day = app.NextDate();
        var s = await app.AddScheduleAsync(day, "Lunch");
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 0);
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 1);
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 1);
        var row = (await Counts.ScheduleTotalsAsync(day, day))
            .Single(r => r.ScheduleId == s);
        Assert.Equal(2, row.TotalQty);
        Assert.Equal(3, row.Submitters);
        Assert.Equal(1, row.ZeroCount);
    }

    [Fact]
    public async Task Pending_cancelled_and_inactive_customers_are_excluded()
    {
        var s = await app.AddScheduleAsync(app.NextDate(), "Dinner");
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 1);                    // counted
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 1, "pending");         // draft
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 1, "cancelled");
        await app.AddCountAsync(await app.AddUserAsync("customer", "inactive"), s, 1);        // inactive
        await app.AddCountAsync(await app.AddUserAsync("customer"), s, 1, "locked");          // counted
        Assert.Equal(2, await Counts.ScheduleTotalAsync(s));
        Assert.Equal(2, (await Counts.CustomerBreakdownAsync(s)).Count);
    }

    [Fact]
    public async Task Pending_customers_lists_active_customers_without_a_valid_count()
    {
        var s = await app.AddScheduleAsync(app.NextDate(), "Breakfast");
        var submitted = await app.AddUserAsync("customer");
        var draft = await app.AddUserAsync("customer");
        await app.AddCountAsync(submitted, s, 1);
        await app.AddCountAsync(draft, s, 1, "pending");
        var pending = (await Counts.PendingCustomersAsync(s)).Select(p => p.Id).ToList();
        Assert.DoesNotContain(submitted, pending);
        Assert.Contains(draft, pending);
    }
}
