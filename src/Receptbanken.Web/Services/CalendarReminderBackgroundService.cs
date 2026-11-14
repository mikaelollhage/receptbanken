using Receptbanken.Infrastructure.Push;

namespace Receptbanken.Web.Services;

/// <summary>
/// Hosted background service that triggers <see cref="CalendarReminderScheduler"/> every 30 seconds.
/// </summary>
public sealed class CalendarReminderBackgroundService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly CalendarReminderScheduler _scheduler;
    private readonly ILogger<CalendarReminderBackgroundService> _logger;
    private readonly TimeProvider _timeProvider;

    public CalendarReminderBackgroundService(
        CalendarReminderScheduler scheduler,
        ILogger<CalendarReminderBackgroundService> logger,
        TimeProvider? timeProvider = null)
    {
        _scheduler = scheduler;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _scheduler.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oväntat fel i kalenderpåminnelsetjänsten.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
