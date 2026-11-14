namespace Receptbanken.Core.Services;

/// <summary>
/// Thread-safe in-memory notification service for real-time synchronization of the shared
/// shopping list across active Blazor Server circuits within the server process.
/// </summary>
public sealed class ShoppingListNotifier
{
    /// <summary>
    /// Maximum number of recently deleted shopping list item IDs retained in memory for
    /// cross-instance reorder validation.
    /// </summary>
    public const int MaxTrackedDeletedIds = 1024;

    private long _version;
    private long _orderingVersion;
    private readonly object _deletedIdsLock = new();
    private readonly Dictionary<int, LinkedListNode<int>> _deletedItemNodes = new();
    private readonly LinkedList<int> _deletedItemOrder = new();

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

    /// <summary>
    /// Records a deleted shopping list item ID in the bounded FIFO history.
    /// </summary>
    public void RecordDeletedItem(int id)
    {
        if (id <= 0)
        {
            return;
        }

        lock (_deletedIdsLock)
        {
            RecordDeletedItemCore(id);
        }
    }

    /// <summary>
    /// Records multiple deleted shopping list item IDs in the bounded FIFO history.
    /// </summary>
    public void RecordDeletedItems(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        lock (_deletedIdsLock)
        {
            foreach (var id in ids)
            {
                if (id > 0)
                {
                    RecordDeletedItemCore(id);
                }
            }
        }
    }

    /// <summary>
    /// Removes a previously deleted shopping list item ID (for example when restored via Undo)
    /// from both the lookup set and the FIFO eviction order.
    /// </summary>
    public bool RemoveDeletedItem(int id)
    {
        if (id <= 0)
        {
            return false;
        }

        lock (_deletedIdsLock)
        {
            if (_deletedItemNodes.Remove(id, out var node))
            {
                _deletedItemOrder.Remove(node);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Returns whether the given shopping list item ID is currently recorded as recently deleted.
    /// </summary>
    public bool WasItemDeleted(int id)
    {
        if (id <= 0)
        {
            return false;
        }

        lock (_deletedIdsLock)
        {
            return _deletedItemNodes.ContainsKey(id);
        }
    }

    /// <summary>
    /// Clears all tracked deleted shopping list item IDs.
    /// </summary>
    public void ClearDeletedItems()
    {
        lock (_deletedIdsLock)
        {
            _deletedItemNodes.Clear();
            _deletedItemOrder.Clear();
        }
    }

    private void RecordDeletedItemCore(int id)
    {
        if (_deletedItemNodes.ContainsKey(id))
        {
            return;
        }

        while (_deletedItemNodes.Count >= MaxTrackedDeletedIds)
        {
            var oldest = _deletedItemOrder.First!;
            _deletedItemOrder.RemoveFirst();
            _deletedItemNodes.Remove(oldest.Value);
        }

        var node = _deletedItemOrder.AddLast(id);
        _deletedItemNodes.Add(id, node);
    }
}
