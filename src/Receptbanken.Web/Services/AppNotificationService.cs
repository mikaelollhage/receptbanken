namespace Receptbanken.Web.Services;

public enum AppNotificationType
{
    Success,
    Warning,
    Error,
    Info
}

/// <summary>
/// Scoped service for persistent application banners rendered at the top of the layout (above the header).
/// Survives page navigation across the Blazor Server circuit.
/// </summary>
public class AppNotificationService
{
    public string? Title { get; private set; }
    public string? Message { get; private set; }
    public AppNotificationType Type { get; private set; } = AppNotificationType.Info;

    public bool HasNotification => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Message);
    public bool IsSuccess => Type == AppNotificationType.Success;
    public bool IsWarning => Type == AppNotificationType.Warning;

    public event Action? OnChange;

    public void ShowSuccess(string title, string message)
    {
        Title = title;
        Message = message;
        Type = AppNotificationType.Success;
        NotifyStateChanged();
    }

    public void ShowWarning(string title, string message)
    {
        Title = title;
        Message = message;
        Type = AppNotificationType.Warning;
        NotifyStateChanged();
    }

    public void Clear()
    {
        if (HasNotification)
        {
            Title = null;
            Message = null;
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}

