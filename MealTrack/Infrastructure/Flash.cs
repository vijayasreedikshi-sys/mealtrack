using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MealTrack.Infrastructure;

public record FlashMessage(string Category, string Message);

/// <summary>One-time messages shown on the next page (like Flask's flash()), stored in TempData.</summary>
public static class Flash
{
    private const string Key = "__flash";

    public static void AddFlash(this ITempDataDictionary tempData, string category, string message)
    {
        var list = Peek(tempData);
        list.Add(new FlashMessage(category, message));
        tempData[Key] = JsonSerializer.Serialize(list);
    }

    /// <summary>Returns the queued messages and removes them.</summary>
    public static List<FlashMessage> TakeFlashes(this ITempDataDictionary tempData)
    {
        var json = tempData[Key] as string;   // reading marks it for deletion
        return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<FlashMessage>>(json) ?? [];
    }

    private static List<FlashMessage> Peek(ITempDataDictionary tempData)
    {
        var json = tempData.Peek(Key) as string;
        return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<FlashMessage>>(json) ?? [];
    }
}
