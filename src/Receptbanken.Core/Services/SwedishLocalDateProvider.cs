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

    /// <summary>
    /// Returns the current calendar date in the Europe/Stockholm timezone.
    /// </summary>
    public static DateOnly GetSwedishToday(TimeProvider? timeProvider = null)
    {
        var utcNow = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return FromUtcInstant(utcNow, GetSwedishTimeZone());
    }

    /// <summary>
    /// Resolves the Europe/Stockholm timezone (or its Windows equivalent W. Europe Standard Time).
    /// </summary>
    public static TimeZoneInfo GetSwedishTimeZone()
    {
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

    /// <summary>
    /// Deterministically converts a Swedish local wall-clock <see cref="DateTime"/> to a UTC <see cref="DateTimeOffset"/>.
    /// - Invalid local times during the spring DST transition are advanced minute-by-minute to the next valid local time (e.g. 02:30 -> 03:00).
    /// - Ambiguous local times during the autumn DST transition use the first occurrence (the larger UTC offset, i.e. summer time).
    /// </summary>
    public static DateTimeOffset ConvertSwedishLocalToUtc(DateTime localDateTime, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? GetSwedishTimeZone();
        var local = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local))
        {
            while (tz.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
            }
        }

        TimeSpan offset;
        if (tz.IsAmbiguousTime(local))
        {
            var offsets = tz.GetAmbiguousTimeOffsets(local);
            offset = offsets.Max();
        }
        else
        {
            offset = tz.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
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

