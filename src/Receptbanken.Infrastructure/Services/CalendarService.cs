using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Receptbanken.Core.Models;
using Receptbanken.Core.Services;
using Receptbanken.Infrastructure.Data;

namespace Receptbanken.Infrastructure.Services;

public class CalendarService
{
    private const int MaxQueryRangeDays = 366;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CalendarNotifier _notifier;
    private readonly ILogger<CalendarService> _logger;
    private readonly TimeProvider _timeProvider;

    public CalendarService(
        IDbContextFactory<AppDbContext> dbFactory,
        CalendarNotifier notifier,
        ILogger<CalendarService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CalendarNotifier Notifier => _notifier;

    public Task<IReadOnlyList<CalendarOccurrenceDto>> GetWeekOccurrencesAsync(
        DateOnly dateInWeek,
        CancellationToken cancellationToken = default)
    {
        var week = CalendarWeek.FromDate(dateInWeek);
        return GetOccurrencesAsync(week.WeekStartDate, week.WeekEndDate, cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarOccurrenceDto>> GetOccurrencesAsync(
        DateOnly rangeStart,
        DateOnly rangeEnd,
        CancellationToken cancellationToken = default)
    {
        if (rangeEnd < rangeStart)
            throw new ArgumentException("Slutdatum måste vara samma som eller senare än startdatum.", nameof(rangeEnd));

        if (rangeEnd.DayNumber - rangeStart.DayNumber > MaxQueryRangeDays)
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), $"Datumintervallet får inte överstiga {MaxQueryRangeDays} dagar.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var events = await db.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions)
            .Where(e =>
                (e.StartDate <= rangeEnd && (e.RecurrenceEndDate == null || e.RecurrenceEndDate >= rangeStart)) ||
                e.Exceptions.Any(ex =>
                    !ex.IsCancelled &&
                    ex.OverrideDate != null &&
                    ex.OverrideDate >= rangeStart &&
                    ex.OverrideDate <= rangeEnd))
            .ToListAsync(cancellationToken);

        return CalendarRecurrenceExpander.Expand(events, rangeStart, rangeEnd);
    }

    public async Task<CalendarEvent?> GetEventByIdAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CalendarEvents
            .AsNoTracking()
            .Include(e => e.Exceptions.OrderBy(ex => ex.OriginalDate))
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
    }

    public async Task<List<string>> GetKnownFamilyMembersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var eventCsvs = await db.CalendarEvents
            .AsNoTracking()
            .Where(e => e.FamilyMembersCsv != null && e.FamilyMembersCsv != "")
            .Select(e => e.FamilyMembersCsv!)
            .ToListAsync(cancellationToken);

        var exceptionCsvs = await db.CalendarEventExceptions
            .AsNoTracking()
            .Where(ex => !ex.IsCancelled && ex.OverrideFamilyMembersCsv != null && ex.OverrideFamilyMembersCsv != "")
            .Select(ex => ex.OverrideFamilyMembersCsv!)
            .ToListAsync(cancellationToken);

        return CalendarFamilyMemberHelper
            .Normalize(eventCsvs.Concat(exceptionCsvs))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CalendarEvent> CreateEventAsync(
        CalendarEventInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = ValidateAndNormalizeEventInput(input);

        var now = _timeProvider.GetUtcNow();
        var entity = new CalendarEvent
        {
            Title = normalized.Title,
            StartDate = input.StartDate,
            IsAllDay = input.IsAllDay,
            StartTime = input.IsAllDay ? null : input.StartTime,
            EndTime = input.IsAllDay ? null : input.EndTime,
            Location = normalized.Location,
            Notes = normalized.Notes,
            FamilyMembersCsv = normalized.FamilyMembersCsv,
            ReminderMinutesBefore = input.ReminderMinutesBefore,
            RecurrenceIntervalWeeks = input.RecurrenceIntervalWeeks,
            RecurrenceDaysOfWeekMask = (int)input.RecurrenceDaysOfWeekMask,
            RecurrenceEndDate = input.RecurrenceIntervalWeeks > 0 ? input.RecurrenceEndDate : null,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.CalendarEvents.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _notifier.NotifyChanged();
        _logger.LogInformation("Skapade kalenderhändelse {EventId} ('{Title}')", entity.Id, entity.Title);
        return entity;
    }

    public async Task<CalendarEvent> UpdateEventAsync(
        int eventId,
        CalendarEventInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = ValidateAndNormalizeEventInput(input);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var entity = await db.CalendarEvents
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken)
            ?? throw new KeyNotFoundException($"Kalenderhändelse med ID {eventId} hittades inte.");

        var now = _timeProvider.GetUtcNow();
        entity.Title = normalized.Title;
        entity.StartDate = input.StartDate;
        entity.IsAllDay = input.IsAllDay;
        entity.StartTime = input.IsAllDay ? null : input.StartTime;
        entity.EndTime = input.IsAllDay ? null : input.EndTime;
        entity.Location = normalized.Location;
        entity.Notes = normalized.Notes;
        entity.FamilyMembersCsv = normalized.FamilyMembersCsv;
        entity.ReminderMinutesBefore = input.ReminderMinutesBefore;
        entity.RecurrenceIntervalWeeks = input.RecurrenceIntervalWeeks;
        entity.RecurrenceDaysOfWeekMask = (int)input.RecurrenceDaysOfWeekMask;
        entity.RecurrenceEndDate = input.RecurrenceIntervalWeeks > 0 ? input.RecurrenceEndDate : null;
        entity.UpdatedAt = now;

        // Remove exceptions whose OriginalDate is no longer a valid occurrence of the updated series.
        if (entity.Exceptions.Count > 0)
        {
            var orphanedExceptions = entity.RecurrenceIntervalWeeks == 0
                ? entity.Exceptions.ToList()
                : entity.Exceptions.Where(ex => !CalendarRecurrenceExpander.IsValidRecurrenceDate(entity, ex.OriginalDate)).ToList();

            if (orphanedExceptions.Count > 0)
            {
                db.CalendarEventExceptions.RemoveRange(orphanedExceptions);
                foreach (var orphaned in orphanedExceptions)
                {
                    entity.Exceptions.Remove(orphaned);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _notifier.NotifyChanged();
        _logger.LogInformation("Uppdaterade kalenderhändelse {EventId} ('{Title}')", entity.Id, entity.Title);
        return entity;
    }

    public async Task<bool> DeleteEventAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var entity = await db.CalendarEvents
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        if (entity == null)
            return false;

        db.CalendarEvents.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _notifier.NotifyChanged();
        _logger.LogInformation("Raderade kalenderhändelse {EventId} ('{Title}')", entity.Id, entity.Title);
        return true;
    }

    public async Task<CalendarEventException> UpdateSingleOccurrenceAsync(
        int eventId,
        DateOnly originalDate,
        CalendarOccurrenceOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = ValidateAndNormalizeCommonFields(
            input.Title,
            input.IsAllDay,
            input.StartTime,
            input.EndTime,
            input.Location,
            input.Notes,
            input.FamilyMembers,
            input.ReminderMinutesBefore);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ev = await db.CalendarEvents
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken)
            ?? throw new KeyNotFoundException($"Kalenderhändelse med ID {eventId} hittades inte.");

        if (ev.RecurrenceIntervalWeeks <= 0)
            throw new InvalidOperationException("Undantag för enskilda tillfällen kan endast skapas för återkommande händelser.");

        if (!CalendarRecurrenceExpander.IsValidRecurrenceDate(ev, originalDate))
            throw new ArgumentException($"Datumet {originalDate:yyyy-MM-dd} ingår inte i den återkommande serien.", nameof(originalDate));

        var now = _timeProvider.GetUtcNow();
        var exception = ev.Exceptions.FirstOrDefault(ex => ex.OriginalDate == originalDate);
        if (exception == null)
        {
            exception = new CalendarEventException
            {
                CalendarEventId = ev.Id,
                OriginalDate = originalDate,
                CreatedAt = now
            };
            ev.Exceptions.Add(exception);
            db.CalendarEventExceptions.Add(exception);
        }

        exception.IsCancelled = false;
        exception.OverrideDate = input.Date != originalDate ? input.Date : null;
        exception.OverrideTitle = normalized.Title;
        exception.OverrideIsAllDay = input.IsAllDay;
        exception.OverrideStartTime = input.IsAllDay ? null : input.StartTime;
        exception.OverrideEndTime = input.IsAllDay ? null : input.EndTime;
        exception.OverrideLocation = normalized.Location ?? string.Empty;
        exception.OverrideNotes = normalized.Notes ?? string.Empty;
        exception.OverrideFamilyMembersCsv = normalized.FamilyMembersCsv ?? string.Empty;
        exception.OverrideReminderMinutesBefore = input.ReminderMinutesBefore;
        exception.OverrideReminderCleared = !input.ReminderMinutesBefore.HasValue;
        exception.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _notifier.NotifyChanged();
        _logger.LogInformation("Uppdaterade enskilt tillfälle {OriginalDate} för kalenderhändelse {EventId}", originalDate, eventId);
        return exception;
    }

    public async Task<CalendarEventException> DeleteSingleOccurrenceAsync(
        int eventId,
        DateOnly originalDate,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ev = await db.CalendarEvents
            .Include(e => e.Exceptions)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken)
            ?? throw new KeyNotFoundException($"Kalenderhändelse med ID {eventId} hittades inte.");

        if (ev.RecurrenceIntervalWeeks <= 0)
            throw new InvalidOperationException("Enskilt tillfälle kan endast raderas på återkommande händelser. Använd DeleteEventAsync för engångshändelser.");

        if (!CalendarRecurrenceExpander.IsValidRecurrenceDate(ev, originalDate))
            throw new ArgumentException($"Datumet {originalDate:yyyy-MM-dd} ingår inte i den återkommande serien.", nameof(originalDate));

        var now = _timeProvider.GetUtcNow();
        var exception = ev.Exceptions.FirstOrDefault(ex => ex.OriginalDate == originalDate);
        if (exception == null)
        {
            exception = new CalendarEventException
            {
                CalendarEventId = ev.Id,
                OriginalDate = originalDate,
                CreatedAt = now
            };
            ev.Exceptions.Add(exception);
            db.CalendarEventExceptions.Add(exception);
        }

        exception.IsCancelled = true;
        exception.OverrideDate = null;
        exception.OverrideTitle = null;
        exception.OverrideIsAllDay = null;
        exception.OverrideStartTime = null;
        exception.OverrideEndTime = null;
        exception.OverrideLocation = null;
        exception.OverrideNotes = null;
        exception.OverrideFamilyMembersCsv = null;
        exception.OverrideReminderMinutesBefore = null;
        exception.OverrideReminderCleared = false;
        exception.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _notifier.NotifyChanged();
        _logger.LogInformation("Raderade enskilt tillfälle {OriginalDate} för kalenderhändelse {EventId}", originalDate, eventId);
        return exception;
    }

    private static NormalizedCommonFields ValidateAndNormalizeEventInput(CalendarEventInput input)
    {
        var common = ValidateAndNormalizeCommonFields(
            input.Title,
            input.IsAllDay,
            input.StartTime,
            input.EndTime,
            input.Location,
            input.Notes,
            input.FamilyMembers,
            input.ReminderMinutesBefore);

        if (input.RecurrenceIntervalWeeks < 0 || input.RecurrenceIntervalWeeks > CalendarEvent.MaxRecurrenceIntervalWeeks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"Upprepningsintervallet måste vara 0 (engångshändelse) eller mellan 1 och {CalendarEvent.MaxRecurrenceIntervalWeeks} veckor.");
        }

        if (input.RecurrenceIntervalWeeks == 0)
        {
            if (input.RecurrenceDaysOfWeekMask != IsoWeekdayMask.None)
                throw new ArgumentException("Veckodagar får inte anges för en engångshändelse.", nameof(input));

            if (input.RecurrenceEndDate.HasValue)
                throw new ArgumentException("Slutdatum för upprepning får inte anges för en engångshändelse.", nameof(input));
        }
        else
        {
            if (input.RecurrenceDaysOfWeekMask == IsoWeekdayMask.None || !input.RecurrenceDaysOfWeekMask.HasValidWeekdaysOnly())
                throw new ArgumentException("Minst en giltig veckodag måste väljas för återkommande händelser.", nameof(input));

            if (input.RecurrenceEndDate.HasValue && input.RecurrenceEndDate.Value < input.StartDate)
                throw new ArgumentException("Slutdatum för upprepning kan inte vara tidigare än startdatum.", nameof(input));
        }

        return common;
    }

    private static NormalizedCommonFields ValidateAndNormalizeCommonFields(
        string title,
        bool isAllDay,
        TimeOnly? startTime,
        TimeOnly? endTime,
        string? location,
        string? notes,
        IReadOnlyList<string>? familyMembers,
        int? reminderMinutesBefore)
    {
        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (trimmedTitle.Length == 0)
            throw new ArgumentException("Rubrik måste anges.", nameof(title));

        if (trimmedTitle.Length > CalendarEvent.MaxTitleLength)
            throw new ArgumentException($"Rubriken får vara högst {CalendarEvent.MaxTitleLength} tecken.", nameof(title));

        var trimmedLocation = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        if (trimmedLocation?.Length > CalendarEvent.MaxLocationLength)
            throw new ArgumentException($"Plats får vara högst {CalendarEvent.MaxLocationLength} tecken.", nameof(location));

        var trimmedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (trimmedNotes?.Length > CalendarEvent.MaxNotesLength)
            throw new ArgumentException($"Anteckning får vara högst {CalendarEvent.MaxNotesLength} tecken.", nameof(notes));

        var familyMembersCsv = CalendarFamilyMemberHelper.FormatCsv(familyMembers);
        if (familyMembersCsv?.Length > CalendarEvent.MaxFamilyMembersCsvLength)
            throw new ArgumentException($"Familjemedlemmar får vara högst {CalendarEvent.MaxFamilyMembersCsvLength} tecken.", nameof(familyMembers));

        if (isAllDay)
        {
            if (startTime.HasValue || endTime.HasValue)
                throw new ArgumentException("Starttid och sluttid får inte anges för heldagshändelser.", nameof(startTime));
        }
        else
        {
            if (!startTime.HasValue)
                throw new ArgumentException("Starttid måste anges för tidsatta händelser.", nameof(startTime));

            if (endTime.HasValue && endTime.Value <= startTime.Value)
                throw new ArgumentException("Sluttid måste vara senare än starttid.", nameof(endTime));
        }

        if (!CalendarReminderOptions.IsValidReminder(isAllDay, reminderMinutesBefore))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderMinutesBefore),
                $"Ogiltigt påminnelseval '{reminderMinutesBefore}' för {(isAllDay ? "heldagshändelse" : "tidsatt händelse")}.");
        }

        return new NormalizedCommonFields(trimmedTitle, trimmedLocation, trimmedNotes, familyMembersCsv);
    }

    private sealed record NormalizedCommonFields(
        string Title,
        string? Location,
        string? Notes,
        string? FamilyMembersCsv);
}
