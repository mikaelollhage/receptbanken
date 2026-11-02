using Receptbanken.Core.Interfaces;

namespace Receptbanken.Core.Services;

public sealed class SystemRandomProvider : IRandomProvider
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class SeededRandomProvider : IRandomProvider
{
    private readonly Random _random;

    public SeededRandomProvider(int seed)
    {
        _random = new Random(seed);
    }

    public double NextDouble() => _random.NextDouble();
}

