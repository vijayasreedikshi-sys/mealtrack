namespace MealTrack.Models;

/// <summary>A scheduled meal as needed by the check-in screens.</summary>
public class MealInfo
{
    public int Id { get; set; }
    public DateTime ScheduleDate { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public bool IsPublished { get; set; }
    public string? QrSecret { get; set; }
}

/// <summary>One customer on a meal's attendance list: applied (count &gt; 0) and/or checked in.</summary>
public class AttendanceLine
{
    public int CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string? RoomNo { get; set; }
    public int Quantity { get; set; }               // applied portions (0 = did not apply)
    public DateTime? CheckedInAt { get; set; }
    public string? Method { get; set; }             // qr / code / manual
    public string? MarkedByName { get; set; }
    public bool Applied => Quantity > 0;
    public bool Came => CheckedInAt is not null;
    public bool WalkIn => Came && !Applied;
}

public class AttendanceSummary
{
    public int AppliedCustomers { get; init; }
    public int AppliedPortions { get; init; }
    public int Came { get; init; }               // applied and checked in
    public int NoShows { get; init; }            // applied, not (yet) checked in
    public int NoShowPortions { get; init; }     // portions cooked for people who did not come
    public int WalkIns { get; init; }            // checked in without applying
    public int CheckedInTotal => Came + WalkIns;
    public bool Tracked => CheckedInTotal > 0;
    public decimal AttendancePct => AppliedCustomers == 0 ? 0 : Math.Round(Came * 100m / AppliedCustomers, 1);

    public static AttendanceSummary From(IReadOnlyCollection<AttendanceLine> lines) => new()
    {
        AppliedCustomers = lines.Count(l => l.Applied),
        AppliedPortions = lines.Where(l => l.Applied).Sum(l => l.Quantity),
        Came = lines.Count(l => l.Applied && l.Came),
        NoShows = lines.Count(l => l.Applied && !l.Came),
        NoShowPortions = lines.Where(l => l.Applied && !l.Came).Sum(l => l.Quantity),
        WalkIns = lines.Count(l => l.WalkIn),
    };
}

/// <summary>Per-meal attendance numbers for lists and the attendance report.</summary>
public class AttendanceReportRow
{
    public int ScheduleId { get; set; }
    public DateTime ScheduleDate { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public int Applied { get; set; }
    public int AppliedPortions { get; set; }
    public int Came { get; set; }
    public int NoShowPortions { get; set; }
    public int CheckedInTotal { get; set; }
    public int NoShows => Applied - Came;
    public int WalkIns => CheckedInTotal - Came;
    public bool Tracked => CheckedInTotal > 0;
    public decimal AttendancePct => Applied == 0 ? 0 : Math.Round(Came * 100m / Applied, 1);
}

/// <summary>A meal's check-in QR: one per meal per day, it does not change during the day.</summary>
public record MealQr(string ScanUrl, string Code, string Svg);

public record CheckInResult(bool Created, DateTime CheckedInAt, int AppliedQty);

// ---------------- view models ----------------

public class AttendanceIndexVm
{
    public DateTime Selected { get; set; }
    public List<AttendanceReportRow> Rows { get; set; } = [];
}

public class AttendanceLiveVm
{
    public MealInfo Meal { get; set; } = new();
    public MealQr Qr { get; set; } = null!;
    public AttendanceSummary Summary { get; set; } = new();
    public bool IsToday { get; set; }
    /// <summary>Set when the page is opened via localhost: an address phones on the same Wi-Fi can reach.</summary>
    public List<string> LanUrls { get; set; } = [];
}

public class AttendanceListVm
{
    public MealInfo Meal { get; set; } = new();
    public List<AttendanceLine> Lines { get; set; } = [];
    public AttendanceSummary Summary { get; set; } = new();
    public bool CanMark { get; set; }
    public string Filter { get; set; } = "all";
}

public class AttendancePrintVm
{
    public MealInfo Meal { get; set; } = new();
    public string Svg { get; set; } = "";
    public string ScanUrl { get; set; } = "";
    public string Code { get; set; } = "";
}

public class ScanVm
{
    /// <summary>done, already, invalid, wrong_day</summary>
    public string State { get; set; } = "invalid";
    public MealInfo? Meal { get; set; }
    public int AppliedQty { get; set; }
    public DateTime? CheckedInAt { get; set; }
}

public class CheckInPageVm
{
    public List<MealInfo> TodayMeals { get; set; } = [];
    public Dictionary<int, AttendanceLine> Mine { get; set; } = [];
}
