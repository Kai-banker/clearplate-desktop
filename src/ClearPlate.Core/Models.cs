using System.Globalization;

namespace ClearPlate.Core;

public sealed record Session(string Token, string AccountId, string Nickname);
public sealed record UserProfile(string AccountId, string Nickname, int Successes, int Remaining,
    string IssuedToken, DateTimeOffset? LastCheckIn);
public sealed class AppState
{
    public bool ScheduleEnabled { get; set; } = true;
    public bool NeedsLogin { get; set; }
    public string GroupId { get; set; } = "";
    public string Location { get; set; } = "";
    public HashSet<string> Attempted { get; set; } = [];
    public HashSet<string> Confirmed { get; set; } = [];
    public Dictionary<string, SlotRecord> Records { get; set; } = [];
}
public sealed record SlotRecord(string StartedAt, string UpdatedAt, string Stage, string Outcome, string Message);
public sealed class LoginRequiredException() : Exception("登录已失效，请在微信正常登录后导入本人新 token。");
public sealed class BusinessException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
public static class Schedule
{
    public static readonly string[] Slots = ["08:00", "12:00", "18:00"];
    public static DateTimeOffset Beijing(DateTimeOffset date) => date.ToOffset(TimeSpan.FromHours(8));
    public static string Day(DateTimeOffset date) => Beijing(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Timestamp(DateTimeOffset date) => Beijing(date).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public static string Key(string slot, DateTimeOffset date) => Day(date) + "@" + slot;
    public static string? Due(DateTimeOffset date)
    {
        var now = Beijing(date).TimeOfDay;
        return Slots.FirstOrDefault(slot => now >= TimeSpan.Parse(slot, CultureInfo.InvariantCulture)
            && now < TimeSpan.Parse(slot, CultureInfo.InvariantCulture).Add(TimeSpan.FromMinutes(5)));
    }
    public static string Suggested(DateTimeOffset date) => Due(date)
        ?? Slots.FirstOrDefault(s => string.CompareOrdinal(s, Beijing(date).ToString("HH:mm", CultureInfo.InvariantCulture)) >= 0)
        ?? Slots[^1];
}
