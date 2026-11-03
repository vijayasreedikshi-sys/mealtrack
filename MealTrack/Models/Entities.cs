namespace MealTrack.Models;

// Row types read with Dapper. Column names are snake_case in MySQL
// (schedule_date) and map to these PascalCase properties automatically.

public class AppUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "customer";
    public string? Phone { get; set; }
    public string? RoomNo { get; set; }
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }

    public string FirstName => Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Name;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
}

public static class Roles
{
    public const string Customer = "customer";
    public const string Admin = "admin";
    public const string Seller = "seller";
}

public class MealType
{
    public int Id { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public int DefaultCutoffDayOffset { get; set; }
    public TimeSpan DefaultCutoffTime { get; set; }
}

public class FoodItem
{
    public int Id { get; set; }
    public string FoodName { get; set; } = "";
    public string? Description { get; set; }
    public string Unit { get; set; } = "plate";
    public decimal? Price { get; set; }
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }

    // Extra columns on the list page
    public int TimesScheduled { get; set; }
    public DateTime? NextDate { get; set; }
    public string? MealNames { get; set; }
}

public class NotificationItem
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A published schedule row joined with one customer's count.</summary>
public class MyScheduleRow
{
    public int Id { get; set; }
    public DateTime ScheduleDate { get; set; }
    public DateTime CutoffTime { get; set; }
    public string? Notes { get; set; }
    public int MealTypeId { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public string? Description { get; set; }
    public string Unit { get; set; } = "";
    public decimal? Price { get; set; }
    public int? Quantity { get; set; }
    public string? SubmissionStatus { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? LastWeekQty { get; set; }

    // Calculated after loading
    public string Status { get; set; } = "pending";
    public bool IsOpen { get; set; }
    public bool HasValue { get; set; }
}

/// <summary>One scheduled meal with its automatically aggregated total.</summary>
public class ScheduleTotal
{
    public int ScheduleId { get; set; }
    public DateTime ScheduleDate { get; set; }
    public DateTime CutoffTime { get; set; }
    public bool IsPublished { get; set; }
    public int MealTypeId { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public string Unit { get; set; } = "";
    public int TotalQty { get; set; }
    public int Submitters { get; set; }
    public int ZeroCount { get; set; }

    // Filled by callers that need them
    public int Pending { get; set; }
    public int ActiveCustomers { get; set; }
    public int? Prepared { get; set; }
    public string PrepStatus { get; set; } = "not_started";
    public bool Final { get; set; }
}

public class CustomerQty
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? RoomNo { get; set; }
    public string? Phone { get; set; }
    public int Quantity { get; set; }
    public string SubmissionStatus { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
}

public record SubmissionCounts(int Meals, int Submitted, int Expected, int Pending, int ActiveCustomers);

public class DeadlineRow
{
    public int Id { get; set; }
    public DateTime ScheduleDate { get; set; }
    public DateTime CutoffTime { get; set; }
    public string MealName { get; set; } = "";
    public string FoodName { get; set; } = "";
    public string? SubmissionStatus { get; set; }
}

public class HistoryRow
{
    public DateTime ScheduleDate { get; set; }
    public DateTime CutoffTime { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public int? Quantity { get; set; }
    public string? SubmissionStatus { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int Changes { get; set; }
    public string Status { get; set; } = "";
}

public class MonthlyRow
{
    public string Ym { get; set; } = "";
    public string Label { get; set; } = "";
    public int Breakfast { get; set; }
    public int Lunch { get; set; }
    public int Dinner { get; set; }
    public int Total { get; set; }
}

public class UsageRow
{
    public DateTime ScheduleDate { get; set; }
    public string MealName { get; set; } = "";
}

public class FoodStats
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Inactive { get; set; }
}

// ---------------- Milestone 2: admin ----------------

public class AdminScheduleRow
{
    public int Id { get; set; }
    public DateTime ScheduleDate { get; set; }
    public int MealTypeId { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public int FoodItemId { get; set; }
    public string FoodName { get; set; } = "";
    public string FoodStatus { get; set; } = "active";
    public DateTime CutoffTime { get; set; }
    public bool IsPublished { get; set; }
    public string? Notes { get; set; }
    public int TotalQty { get; set; }
    public int Submitters { get; set; }
    public string? Sellers { get; set; }
}

public class SellerOption
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public bool Assigned { get; set; }
    public bool CanViewCustomers { get; set; }
}

/// <summary>A customer or seller on the admin list pages, with activity stats.</summary>
public class UserRow : AppUser
{
    public int Submissions30 { get; set; }
    public int Portions30 { get; set; }
    public DateTime? LastActivity { get; set; }
    public string? MealDefaults { get; set; }
    public int UpcomingAssignments { get; set; }
}

public class SellerDefault
{
    public int MealTypeId { get; set; }
    public bool CanViewCustomers { get; set; }
}

/// <summary>One customer-meal line on the admin "View Food Counts" page.</summary>
public class CountRow
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string? RoomNo { get; set; }
    public string? Phone { get; set; }
    public int ScheduleId { get; set; }
    public DateTime ScheduleDate { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public DateTime CutoffTime { get; set; }
    public int? Quantity { get; set; }
    public string? SubmissionStatus { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string Status { get; set; } = "";
}

// ---------------- Milestone 3: seller / reports ----------------

public class PrepRecord
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public string SellerName { get; set; } = "";
    public int FoodScheduleId { get; set; }
    public DateTime ScheduleDate { get; set; }
    public string MealName { get; set; } = "";
    public string FoodName { get; set; } = "";
    public int RequiredQuantity { get; set; }
    public int PreparedQuantity { get; set; }
    public int ConsumedQuantity { get; set; }
    public int RemainingQuantity { get; set; }
    public int WastedQuantity { get; set; }
    public string PreparationStatus { get; set; } = "not_started";
    public string? Remarks { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Wastage % = wasted / prepared × 100.</summary>
    public decimal WastagePct => PreparedQuantity == 0 ? 0 : Math.Round(WastedQuantity * 100m / PreparedQuantity, 1);
    public int Variance => PreparedQuantity - RequiredQuantity;
}

/// <summary>A seller's assignment for one scheduled meal, with live total and prep status.</summary>
public class AssignmentRow
{
    public int ScheduleId { get; set; }
    public DateTime ScheduleDate { get; set; }
    public string MealName { get; set; } = "";
    public int SortOrder { get; set; }
    public string FoodName { get; set; } = "";
    public string Unit { get; set; } = "";
    public DateTime CutoffTime { get; set; }
    public bool CanViewCustomers { get; set; }
    public int TotalQty { get; set; }
    public int Submitters { get; set; }
    public int? PreparedQuantity { get; set; }
    public int? WastedQuantity { get; set; }
    public string PreparationStatus { get; set; } = "not_started";
    public bool Final => CutoffTime <= DateTime.Now;
}
