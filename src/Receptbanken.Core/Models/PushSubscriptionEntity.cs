namespace Receptbanken.Core.Models;

/// <summary>
/// Persists a browser Web Push subscription for a family device.
/// </summary>
public class PushSubscriptionEntity
{
    public const int MaxEndpointLength = 2048;
    public const int MaxP256dhLength = 256;
    public const int MaxAuthLength = 128;
    public const int MaxDeviceLabelLength = 120;

    public int Id { get; set; }

    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public string? DeviceLabel { get; set; }

    public bool IsActive { get; set; } = true;
    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
}
