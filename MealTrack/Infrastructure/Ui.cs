using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace MealTrack.Infrastructure;

/// <summary>Date formatting used by the views.</summary>
public static class Fmt
{
    public static string Date(DateTime? v, string fmt = "ddd, dd MMM yyyy") => v?.ToString(fmt) ?? "";
    public static string Dt(DateTime? v, string fmt = "dd MMM yyyy, hh:mm tt") => v?.ToString(fmt) ?? "";
    public static string Time(DateTime? v) => v?.ToString("hh:mm tt") ?? "";
    public static string Iso(DateTime? v) => v?.ToString("yyyy-MM-ddTHH:mm:ss") ?? "";
    public static string IsoDate(DateTime v) => v.ToString("yyyy-MM-dd");

    public static string Ago(DateTime? v)
    {
        if (v is null) return "";
        var secs = (int)(DateTime.Now - v.Value).TotalSeconds;
        if (secs < 60) return "just now";
        if (secs < 3600) return $"{secs / 60} min ago";
        if (secs < 86400) return $"{secs / 3600} h ago";
        return v.Value.ToString("dd MMM");
    }

    public static string Title(string? s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}

/// <summary>Reusable UI components (stat card, status badge, icons...).</summary>
public static class Ui
{
    public static readonly IReadOnlyDictionary<string, string> MealIcons = new Dictionary<string, string>
    {
        ["Breakfast"] = "sunrise", ["Lunch"] = "sun", ["Dinner"] = "moon",
    };

    private static readonly Dictionary<string, string> StatusLabels = new()
    {
        ["submitted"] = "Submitted", ["locked"] = "Locked", ["pending"] = "Pending", ["missed"] = "Not submitted",
        ["cancelled"] = "Cancelled", ["not_started"] = "Not Started", ["preparing"] = "Preparing",
        ["completed"] = "Completed", ["active"] = "Active", ["inactive"] = "Inactive", ["final"] = "Final",
        ["open"] = "Open", ["published"] = "Published", ["draft"] = "Draft",
    };

    private static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");

    public static string IconHtml(string name, string cls = "") =>
        $"<i data-lucide=\"{E(name)}\" class=\"{E(cls)}\" aria-hidden=\"true\"></i>";

    public static IHtmlContent Icon(string name, string cls = "") => new HtmlString(IconHtml(name, cls));

    public static IHtmlContent StatusBadge(string? status)
    {
        status ??= "pending";
        var label = StatusLabels.TryGetValue(status, out var l) ? l : Fmt.Title(status);
        return new HtmlString($"<span class=\"status status-{E(status)}\">{E(label)}</span>");
    }

    public static IHtmlContent StatCard(string icon, string label, object? value, string? sub = null, string tone = "")
    {
        var subHtml = sub is null ? "" : $"<div class=\"stat-sub\">{E(sub)}</div>";
        return new HtmlString(
            $"<div class=\"card stat-card\"><div class=\"card-body\">" +
            $"<div class=\"stat-icon {E(tone)}\">{IconHtml(icon)}</div>" +
            $"<div class=\"min-w-0\"><div class=\"stat-label\">{E(label)}</div>" +
            $"<div class=\"stat-value\">{E(value?.ToString())}</div>{subHtml}</div></div></div>");
    }

    public static IHtmlContent MealChip(string meal)
    {
        var icon = MealIcons.TryGetValue(meal, out var i) ? i : "utensils";
        return new HtmlString($"<span class=\"meal-chip\"><span class=\"meal-ico\">{IconHtml(icon)}</span>{E(meal)}</span>");
    }

    public static IHtmlContent EmptyState(string icon, string title, string? text = null)
    {
        var textHtml = text is null ? "" : $"<div class=\"small mt-1\">{E(text)}</div>";
        return new HtmlString(
            $"<div class=\"empty-state\">{IconHtml(icon)}<div class=\"fw-semibold text-black\">{E(title)}</div>{textHtml}</div>");
    }
}
