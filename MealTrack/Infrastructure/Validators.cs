using System.Globalization;
using System.Text.RegularExpressions;

namespace MealTrack.Infrastructure;

/// <summary>Server-side input validation helpers.</summary>
public static partial class Validators
{
    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailRe();

    [GeneratedRegex(@"^[0-9+\- ]{7,15}$")]
    private static partial Regex PhoneRe();

    public static string Clean(string? value, int maxLen = 255)
    {
        var s = (value ?? "").Trim();
        return s.Length > maxLen ? s[..maxLen] : s;
    }

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2) return "Name must be at least 2 characters.";
        if (name.Length > 100) return "Name is too long.";
        return null;
    }

    public static string? ValidateEmail(string email)
    {
        if (string.IsNullOrEmpty(email) || !EmailRe().IsMatch(email) || email.Length > 150)
            return "Enter a valid email address.";
        return null;
    }

    public static string? ValidatePhone(string phone, bool required = false)
    {
        if (string.IsNullOrEmpty(phone)) return required ? "Phone number is required." : null;
        return PhoneRe().IsMatch(phone) ? null : "Enter a valid phone number.";
    }

    public static string? ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "Password must be at least 8 characters.";
        if (!password.Any(char.IsAsciiLetter) || !password.Any(char.IsAsciiDigit))
            return "Password must contain both letters and numbers.";
        return null;
    }

    public static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.Date
            : null;

    public static int? ParseInt(string? value, int? minimum = null, int? maximum = null)
    {
        if (!int.TryParse((value ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            return null;
        if (minimum is not null && n < minimum) return null;
        if (maximum is not null && n > maximum) return null;
        return n;
    }

    /// <summary>Monday of the week containing d.</summary>
    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Only allow relative redirects inside this site.</summary>
    public static string? SafeNext(string? target)
    {
        if (string.IsNullOrEmpty(target) || !target.StartsWith('/') || target.StartsWith("//") || target.StartsWith("/\\"))
            return null;
        return target;
    }
}
