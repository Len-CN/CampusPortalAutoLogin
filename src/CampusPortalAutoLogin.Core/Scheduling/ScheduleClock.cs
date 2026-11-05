using CampusPortalAutoLogin.Core.Models;
namespace CampusPortalAutoLogin.Core.Scheduling;
public sealed class ScheduleClock
{
    private DateTime? next;
    private DateTime? lastDate;
    public static DateTime Next(DateTime now, string time)
    {
        var parsed = TimeOnly.ParseExact(time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = now.Date.Add(parsed.ToTimeSpan());
        return candidate > now ? candidate : candidate.AddDays(1);
    }
    public void Reset(DateTime now, AppSettings settings) => next = settings.ScheduledLogin ? Next(now, settings.ScheduledTime) : null;
    public bool TakeDue(DateTime now, AppSettings settings)
    {
        if (!settings.ScheduledLogin || next is null || now < next) return false;
        next = Next(now, settings.ScheduledTime);
        if (lastDate >= now.Date) return false;
        lastDate = now.Date;
        return true;
    }
}
