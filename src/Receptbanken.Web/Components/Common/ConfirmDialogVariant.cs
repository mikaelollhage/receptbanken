namespace Receptbanken.Web.Components.Common;

/// <summary>
/// Visuell variant för bekräftelseknappen i <see cref="ConfirmDialog"/>.
/// </summary>
public enum ConfirmDialogVariant
{
    /// <summary>
    /// Röd destruktiv styling (<c>btn-danger-custom</c>). Standardvärde för radering och potentiell dataförlust.
    /// </summary>
    Danger = 0,

    /// <summary>
    /// Korallfärgad primärstyling (<c>btn-primary-custom</c>) för positiva och icke-destruktiva bekräftelser.
    /// </summary>
    Primary = 1,

    /// <summary>
    /// Semantisk synonym till <see cref="Danger"/>.
    /// </summary>
    Destructive = Danger
}
