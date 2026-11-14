namespace Receptbanken.Core.Services;

/// <summary>
/// Thread-safe in-memory notification service for real-time synchronization of the shared
/// family calendar across active Blazor Server circuits within the server process.
/// </summary>
public sealed class CalendarNotifier
{
    private long _version;

    /// <summary>
    /// Monotonically increasing version number incremented after every persisted calendar change.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Raised after a calendar change has been persisted to SQLite.
    /// The argument is the updated <see cref="Version"/>.
    /// </summary>
    public event Action<long>? OnChanged;

    /// <summary>
    /// Increments the version counter and notifies all active subscribers safely.
    /// </summary>
    public long NotifyChanged()
    {
        var newVersion = Interlocked.Increment(ref _version);
        var handlers = OnChanged;
        if (handlers != null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Action<long>>())
            {
                try
                {
                    handler(newVersion);
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if a subscriber circuit is currently disposing.
                }
            }
        }

        return newVersion;
    }
}
