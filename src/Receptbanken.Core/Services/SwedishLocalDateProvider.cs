using Receptbanken.Core.Interfaces;

namespace Receptbanken.Core.Services;

/// <summary>
/// Resolves the user's local calendar date.
/// Supports client-reported local date (from browser) and falls back to Swedish local time (Europe/Stockholm)
/// rather than raw server UTC, ensuring midnight boundary accuracy even inside UTC Linux containers.
/// </summary>
public class SwedishLocalDateProvider : ILocalDateProvider
{
    private readonly TimeProvider _timeProvider;
    private DateOnly? _clientOverrideDate;

    public SwedishLocalDateProvider(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void SetClientLocalDate(DateOnly clientDate)
    {
        _clientOverrideDate = clientDate;
    }

    public DateOnly GetLocalToday()
    {
        if (_clientOverrideDate.HasValue)
        {
            return _clientOverrideDate.Value;
        }

        var utcNow = _timeProvider.GetUtcNow();
        var tz = ResolveSwedishOrLocalTimeZone();
        var localDateTime = TimeZoneInfo.ConvertTime(utcNow, tz);
        return DateOnly.FromDateTime(localDateTime.DateTime);
    }

    public static DateOnly FromUtcInstant(DateTimeOffset utcInstant, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? ResolveSwedishOrLocalTimeZone();
        var local = TimeZoneInfo.ConvertTime(utcInstant, tz);
        return DateOnly.FromDateTime(local.DateTime);
    }

    private static TimeZoneInfo ResolveSwedishOrLocalTimeZone()
    {
        // If local system time zone is already non-UTC (e.g. developer workstation), use it;
        // if running inside a UTC container, resolve Europe/Stockholm (or W. Europe Standard Time on Windows).
        if (TimeZoneInfo.Local.BaseUtcOffset != TimeSpan.Zero || TimeZoneInfo.Local.SupportsDaylightSavingTime)
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        }
        catch
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            }
            catch
            {
                return TimeZoneInfo.Local;
            }
        }
    }
}

