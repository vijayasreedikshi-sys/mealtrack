namespace MealTrack.Models;

// ---------- Schedules ----------
public class ScheduleGridVm
{
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd => WeekStart.AddDays(6);
    public List<MealType> MealTypes { get; set; } = [];
    /// <summary>(date, meal_type_id) -> schedule</summary>
    public Dictionary<(DateTime, int), AdminScheduleRow> Cells { get; set; } = [];
    public List<FoodItem> Foods { get; set; } = [];
    public int Drafts => Cells.Values.Count(c => !c.IsPublished);
    public int Published => Cells.Values.Count(c => c.IsPublished);
    public int ActiveCustomers { get; set; }

    public AdminScheduleRow? Cell(DateTime d, int mealTypeId) => Cells.GetValueOrDefault((d.Date, mealTypeId));
}

public class ScheduleEditVm
{
    public AdminScheduleRow? Schedule { get; set; }      // null = new
    public DateTime Date { get; set; }
    public int MealTypeId { get; set; }
    public int FoodItemId { get; set; }
    public string Cutoff { get; set; } = "";             // yyyy-MM-ddTHH:mm
    public bool UseDefaultCutoff { get; set; } = true;
    public string Notes { get; set; } = "";
    public bool IsPublished { get; set; }
    public List<MealType> MealTypes { get; set; } = [];
    public List<FoodItem> Foods { get; set; } = [];
    public List<SellerOption> Sellers { get; set; } = [];
    public int CountRows { get; set; }
    public List<CustomerQty> Breakdown { get; set; } = [];
    public List<CustomerQty> PendingCustomers { get; set; } = [];
}

public class CutoffsVm
{
    public List<MealType> MealTypes { get; set; } = [];
}

// ---------- Customers / sellers ----------
public class UsersVm
{
    public string Kind { get; set; } = "customers";      // customers | sellers
    public string Role => Kind == "sellers" ? Roles.Seller : Roles.Customer;
    public string Label => Kind == "sellers" ? "Seller" : "Customer";
    public List<UserRow> Users { get; set; } = [];
    public string Q { get; set; } = "";
    public string Status { get; set; } = "";
    public int Total { get; set; }
    public int Active { get; set; }
}

public class UserFormVm
{
    public string Kind { get; set; } = "customers";
    public string Label => Kind == "sellers" ? "Seller" : "Customer";
    public AppUser? User { get; set; }                   // null = new
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string RoomNo { get; set; } = "";
    public string Status { get; set; } = "active";
}

public class CustomerDetailVm
{
    public AppUser User { get; set; } = new();
    public List<HistoryRow> Recent { get; set; } = [];
    public List<MonthlyRow> Monthly { get; set; } = [];
    public int Submitted30 { get; set; }
    public int Missed30 { get; set; }
    public int Portions30 { get; set; }
}

public class SellerDetailVm
{
    public AppUser User { get; set; } = new();
    public List<MealType> MealTypes { get; set; } = [];
    public Dictionary<int, SellerDefault> Defaults { get; set; } = [];
    public List<AssignmentRow> Upcoming { get; set; } = [];
    public List<PrepRecord> Activity { get; set; } = [];
}

// ---------- Food counts ----------
public class CountsVm
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int? Meal { get; set; }
    public int? Customer { get; set; }
    public string Tab { get; set; } = "all";            // all | submitted | pending
    public List<MealType> MealTypes { get; set; } = [];
    public List<AppUser> Customers { get; set; } = [];
    public List<ScheduleTotal> Totals { get; set; } = [];
    public List<CountRow> Rows { get; set; } = [];
    public Dictionary<string, int> TabCounts { get; set; } = [];
    public int Page { get; set; } = 1;
    public int Pages { get; set; } = 1;

    public string Query(object? overrides = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["start"] = Start.ToString("yyyy-MM-dd"), ["end"] = End.ToString("yyyy-MM-dd"),
            ["meal"] = Meal?.ToString(), ["customer"] = Customer?.ToString(), ["tab"] = Tab,
        };
        if (overrides is not null)
            foreach (var p in overrides.GetType().GetProperties())
                d[p.Name] = p.GetValue(overrides)?.ToString();
        return string.Join("&", d.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }
}

// ---------- Reports ----------
public class ReportTable
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    /// <summary>Indexes of numeric columns (right-aligned, summed in the footer).</summary>
    public HashSet<int> NumericColumns { get; set; } = [];
    public List<object?[]> Rows { get; set; } = [];
    public object?[]? Footer { get; set; }
}

public class ReportsVm
{
    public string Type { get; set; } = "daily";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public ReportTable Table { get; set; } = new();
    public static readonly (string Key, string Label, string Icon)[] Types =
    [
        ("daily", "Daily consumption", "calendar-days"),
        ("weekly", "Weekly consumption", "calendar-range"),
        ("monthly", "Monthly consumption", "calendar"),
        ("customer", "Customer-wise", "users"),
        ("seller", "Seller preparation", "chef-hat"),
        ("wastage", "Wastage", "leaf"),
        ("attendance", "Attendance & no-shows", "qr-code"),
    ];
}

// ---------- Seller ----------
public class RequirementsVm
{
    public DateTime Selected { get; set; }
    public List<AssignmentRow> Rows { get; set; } = [];
    public Dictionary<int, List<CustomerQty>> Breakdown { get; set; } = [];
    public int Total => Rows.Sum(r => r.TotalQty);
}

public class AssignmentsVm
{
    public DateTime WeekStart { get; set; }
    public List<IGrouping<DateTime, AssignmentRow>> Grouped { get; set; } = [];
}

public class PrepFormVm
{
    public AssignmentRow Assignment { get; set; } = new();
    public PrepRecord? Existing { get; set; }
    public int Prepared { get; set; }
    public int Consumed { get; set; }
    public int Remaining { get; set; }
    public int Wasted { get; set; }
    public string Status { get; set; } = "not_started";
    public string Remarks { get; set; } = "";
}

public class PrepHistoryVm
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public List<PrepRecord> Rows { get; set; } = [];
    public int Required => Rows.Sum(r => r.RequiredQuantity);
    public int Prepared => Rows.Sum(r => r.PreparedQuantity);
    public int Wasted => Rows.Sum(r => r.WastedQuantity);
    public decimal WastagePct => Prepared == 0 ? 0 : Math.Round(Wasted * 100m / Prepared, 1);
}
