namespace Receptbanken.Core.Interfaces;

/// <summary>
/// Provides the user's local calendar date so calendar week and today's dinner calculations
/// do not blindly rely on server UTC date and can be deterministically tested.
/// </summary>
public interface ILocalDateProvider
{
    DateOnly GetLocalToday();
    void SetClientLocalDate(DateOnly clientDate);
}

