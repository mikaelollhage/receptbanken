namespace Receptbanken.Core.Interfaces;

/// <summary>
/// Abstraction over random number generation so weighted recipe selection can be deterministically tested.
/// </summary>
public interface IRandomProvider
{
    /// <summary>
    /// Returns a random floating-point number that is greater than or equal to 0.0, and less than 1.0.
    /// </summary>
    double NextDouble();
}

