namespace Receptbanken.Core.Services;

/// <summary>
/// Thread-safe in-memory notification service for real-time synchronization of the shared
/// shopping list across active Blazor Server circuits within the server process.
/// </summary>
public sealed class ShoppingListNotifier
{
    private long _version;
    private long _orderingVersion;

    /// <summary>
    /// Monotonically increasing version number incremented after every persisted shopping list change.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Monotonically increasing version number incremented after every persisted sort order change.
    /// Used to detect and reject stale concurrent reorder operations without blocking normal check-offs or additions.
    /// </summary>
    public long OrderingVersion => Interlocked.Read(ref _orderingVersion);

    /// <summary>
    /// Raised after a shopping list change has been persisted to SQLite.
    /// The argument is the updated <see cref="Version"/>.
    /// </summary>
    public event Action<long>? OnChanged;

    /// <summary>
    /// Increments the ordering version counter as well as the general version counter and notifies all active subscribers.
    /// </summary>
    public long NotifyOrderingChanged()
    {
        Interlocked.Increment(ref _orderingVersion);
        return NotifyChanged();
    }

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
