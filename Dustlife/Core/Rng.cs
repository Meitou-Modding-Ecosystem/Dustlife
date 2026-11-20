// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
namespace Dustlife.Core;

/// <summary>
/// A small deterministic generator (xorshift64*), saved with the world. Bit for bit the reference's (`dl::Rng`), so its results can be
/// compared run for run.
/// </summary>
public sealed class Rng
{
    /// <summary>
    /// The state; never 0 (a zero state stays zero).
    /// </summary>
    public ulong State = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// A generator starting from <paramref name="seed"/> (0 is replaced by the default state).
    /// </summary>
    public static Rng FromSeed(ulong seed) => new() { State = seed == 0 ? 0x9E3779B97F4A7C15UL : seed };

    /// <summary>
    /// The next 64 bits.
    /// </summary>
    public ulong Next()
    {
        State ^= State >> 12;
        State ^= State << 25;
        State ^= State >> 27;
        return unchecked(State * 2685821657736338717UL);
    }

    /// <summary>
    /// A float in [0, 1): 24 bits of the next draw.
    /// </summary>
    public float Uniform() => ((Next() >> 40) & 0xFFFFFF) / 16777216.0f;

    /// <summary>
    /// A float in [lo, hi).
    /// </summary>
    public float Range(float lo, float hi) => lo + (hi - lo) * Uniform();

    /// <summary>
    /// An int in [lo, hi], both included; lo when hi is not above it.
    /// </summary>
    public int Int(int lo, int hiInclusive) => hiInclusive <= lo ? lo : lo + (int)(Next() % (ulong)(hiInclusive - lo + 1));

    /// <summary>
    /// True with probability p.
    /// </summary>
    public bool Chance(float p) => Uniform() < p;

    /// <summary>
    /// Rounds x up or down so the expected value is x (0 for anything not above 0; capped at a million).
    /// </summary>
    public int StochRound(float x)
    {
        if (!(x > 0)) return 0;
        if (x > 1e6f) x = 1e6f;
        int i = (int)x;
        return i + (Chance(x - i) ? 1 : 0);
    }
}
