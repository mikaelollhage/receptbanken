namespace Receptbanken.Web.Components.Common;

public enum FeedbackSeverity
{
    Success,
    Warning,
    Error
}

public enum FeedbackPlacement
{
    Top,
    Bottom
}

/// <summary>
/// Local component state holder for contextual feedback messages.
/// Tracks a monotonically increasing <see cref="Version"/> so repeated confirmations
/// reliably reset auto-dismiss timers without global state.
/// </summary>
public sealed class FeedbackState
{
    public string? Message { get; private set; }
    public string? Title { get; private set; }
    public FeedbackSeverity Severity { get; private set; } = FeedbackSeverity.Success;
    public FeedbackPlacement Placement { get; private set; } = FeedbackPlacement.Top;
    public long Version { get; private set; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message) || !string.IsNullOrWhiteSpace(Title);
    public bool IsSuccess => HasMessage && Severity == FeedbackSeverity.Success;
    public bool IsWarning => HasMessage && Severity == FeedbackSeverity.Warning;
    public bool IsError => HasMessage && Severity == FeedbackSeverity.Error;

    public void ShowSuccess(string message, string? title = null, FeedbackPlacement placement = FeedbackPlacement.Top)
    {
        Message = message;
        Title = title;
        Severity = FeedbackSeverity.Success;
        Placement = placement;
        Version++;
    }

    public void ShowWarning(string message, string? title = null, FeedbackPlacement placement = FeedbackPlacement.Top)
    {
        Message = message;
        Title = title;
        Severity = FeedbackSeverity.Warning;
        Placement = placement;
        Version++;
    }

    public void ShowError(string message, string? title = null, FeedbackPlacement placement = FeedbackPlacement.Top)
    {
        Message = message;
        Title = title;
        Severity = FeedbackSeverity.Error;
        Placement = placement;
        Version++;
    }

    public void Clear()
    {
        if (HasMessage)
        {
            Message = null;
            Title = null;
            Version++;
        }
    }
}
