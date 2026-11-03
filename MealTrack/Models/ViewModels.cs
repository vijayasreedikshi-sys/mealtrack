namespace MealTrack.Models;

// ---------- Auth ----------
public class RegisterForm
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string RoomNo { get; set; } = "";
}

public class NotificationsPageVm
{
    public int Unread { get; set; }
    public List<NotificationItem> Items { get; set; } = [];
    public int Page { get; set; }
    public int Pages { get; set; }
    public int Total { get; set; }
}

public class ErrorVm
{
    public int Code { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
}

// ---------- Customer ----------
public class CustomerDashboardVm
{
    public List<MyScheduleRow> TodayRows { get; set; } = [];
    public int WeekTotalMeals { get; set; }
    public int WeekSubmitted { get; set; }
    public int WeekPortions { get; set; }
    public int OpenUnsubmitted { get; set; }
    public DeadlineRow? NextDeadline { get; set; }
    public List<DeadlineRow> UpcomingPending { get; set; } = [];
    public DateTime WeekStart { get; set; }
}

public class ScheduleWeekVm
{
    public List<(DateTime Day, Dictionary<string, MyScheduleRow> Meals)> Grid { get; set; } = [];
    public List<MealType> MealTypes { get; set; } = [];
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public DateTime PrevWeek { get; set; }
    public DateTime NextWeek { get; set; }
}

public class SubmitVm
{
    public List<IGrouping<DateTime, MyScheduleRow>> Grouped { get; set; } = [];
    public string View { get; set; } = "day";
    public DateTime Selected { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AnyOpen { get; set; }
    public int MaxQty { get; set; }
    public DateTime PrevDate { get; set; }
    public DateTime NextDate { get; set; }
}

public class SubmissionsVm
{
    public List<IGrouping<DateTime, MyScheduleRow>> Grouped { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    public DateTime End { get; set; }
}

public class HistoryVm
{
    public List<HistoryRow> Rows { get; set; } = [];
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int? MealFilter { get; set; }
    public List<MealType> MealTypes { get; set; } = [];
    public List<(string Meal, int Total)> MealTotals { get; set; } = [];
    public int Missed { get; set; }
    public List<MonthlyRow> Monthly { get; set; } = [];
}

// ---------- Admin ----------
public class AdminDashboardVm
{
    public Dictionary<string, int> MealTotals { get; set; } = [];
    public List<ScheduleTotal> TodayMeals { get; set; } = [];
    public SubmissionCounts Subs { get; set; } = new(0, 0, 0, 0, 0);
    public int ActiveCustomers { get; set; }
}

public class FoodItemsVm
{
    public List<FoodItem> Items { get; set; } = [];
    public string Q { get; set; } = "";
    public string Status { get; set; } = "";
    public FoodStats Stats { get; set; } = new();
}

public class FoodForm
{
    public string FoodName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "plate";
    public string Price { get; set; } = "";
    public string Status { get; set; } = "active";
    public decimal? PriceValue { get; set; }
}

public class FoodFormVm
{
    public FoodForm Form { get; set; } = new();
    public FoodItem? Food { get; set; }
    public IReadOnlyList<string> Units { get; set; } = [];
    public List<UsageRow> Usage { get; set; } = [];
}

// ---------- Seller ----------
public class SellerDashboardVm
{
    public DateTime Selected { get; set; }
    public List<AssignmentRow> Rows { get; set; } = [];
    public List<AssignmentRow> Upcoming { get; set; } = [];
    public int RecentPrepared { get; set; }
    public int RecentWasted { get; set; }
    public int Total => Rows.Sum(r => r.TotalQty);
    public decimal RecentWastagePct => RecentPrepared == 0 ? 0 : Math.Round(RecentWasted * 100m / RecentPrepared, 1);
}
