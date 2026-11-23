// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// F1 war rhythm (`tools/euler/war_rhythm_check.py`): war-weariness from a faction's own losses, relative to its strength;
/// exhausted at 0.5 (no new offensives or raids), a truce once rested, then war again; a faction that loses nothing never tires.
/// </summary>
public sealed class WarRhythmTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));
        public bool HostileToPlayer(int faction) => false;
    }

    sealed record Setup(World W, Warfare War, int Hn, int Sk, Relations Rel);

    static Setup NewWar()
    {
        var w = new World();
        w.Rng.State = 11;
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        w.AddPlace("stack", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        w.AddPlace("squin", "Squin", new Vector2(200000, 0), PlaceKind.Base, 3, sk);
        var war = new Warfare(new WarfareConfig { SeedGarrisons = false, Rhythm = true });
        w.AddModule(war);
        w.StartNew();
        return new Setup(w, war, hn, sk, new Relations((hn, sk)));
    }

    static void Hours(Setup s, double hours)
    {
        for (int i = 0; i < hours * 4; i++) s.W.Tick(0.25f, s.Rel);
    }

    static void Lose(Setup s, int faction, int bands)
    {
        for (int k = 0; k < bands; k++)
            if (s.W.FindBand(s.W.SpawnBand(faction, -1, BandRole.Garrison, new Vector2(0, 900000 + k * 10000), "warfare")) is { } b) s.W.Kill(b);
    }

    [Fact]
    public void Losing_half_its_strength_wears_a_faction_out_then_a_truce_then_war_again()
    {
        var s = NewWar();
        for (int k = 0; k < 8; k++) s.W.SpawnBand(s.Sk, 1, BandRole.Garrison, new Vector2(200000, 0), "warfare");
        Hours(s, 1);
        Assert.Equal(WarPhase.Offensive, s.War.Fs[s.Sk].Phase);
        Lose(s, s.Sk, 4);                                                                  // 4 of 8 bands: W = 4 / 8 = 0.5
        Hours(s, 0.5);
        Assert.Equal(WarPhase.Exhausted, s.War.Fs[s.Sk].Phase);
        Assert.True(s.War.Resting(s.Sk));
        Assert.Contains(s.W.Events, e => e.Key == "war_exhausted");

        Hours(s, 24 * 4);
        Assert.Equal(WarPhase.Exhausted, s.War.Fs[s.Sk].Phase);                            // at least 5 days exhausted
        Hours(s, 24 * 6);                                                                  // W halves every 7 days: 0.5 -> 0.2 in ~9.3 days
        Assert.Equal(WarPhase.Truce, s.War.Fs[s.Sk].Phase);
        Assert.Contains(s.W.Events, e => e.Key == "war_truce");
        Hours(s, 24 * 14);
        Assert.Equal(WarPhase.Offensive, s.War.Fs[s.Sk].Phase);
        Assert.Contains(s.W.Events, e => e.Key == "war_renewed");
        Assert.Equal((1, 1, 1), (s.War.Fired.Exhausted, s.War.Fired.Truces, s.War.Fired.Renewed));
    }

    [Fact]
    public void A_faction_that_loses_nothing_never_tires()
    {
        var s = NewWar();
        for (int k = 0; k < 8; k++) s.W.SpawnBand(s.Hn, 0, BandRole.Garrison, Vector2.Zero, "warfare");
        Hours(s, 24 * 60);
        Assert.Equal(WarPhase.Offensive, s.War.Fs[s.Hn].Phase);
        Assert.Equal(0, s.War.Fs[s.Hn].Weariness, 9);
    }

    [Fact]
    public void A_big_faction_needs_more_losses_than_a_small_one()
    {
        var s = NewWar();
        for (int k = 0; k < 20; k++) s.W.SpawnBand(s.Hn, 0, BandRole.Garrison, Vector2.Zero, "warfare");
        for (int k = 0; k < 4; k++) s.W.SpawnBand(s.Sk, 1, BandRole.Garrison, new Vector2(200000, 0), "warfare");
        Hours(s, 1);
        Lose(s, s.Hn, 3);
        Lose(s, s.Sk, 3);
        Hours(s, 0.5);
        Assert.Equal(WarPhase.Offensive, s.War.Fs[s.Hn].Phase);                            // 3 of 20: W 0.15
        Assert.Equal(WarPhase.Exhausted, s.War.Fs[s.Sk].Phase);                            // 3 of 4: W 0.75
    }

    [Fact]
    public void The_rhythm_survives_a_save()
    {
        var s = NewWar();
        for (int k = 0; k < 8; k++) s.W.SpawnBand(s.Sk, 1, BandRole.Garrison, new Vector2(200000, 0), "warfare");
        Hours(s, 1);
        Lose(s, s.Sk, 5);
        Hours(s, 1);
        using var ms = new MemoryStream();
        WorldSave.Write(s.W, ms);
        var t = NewWar();
        ms.Position = 0;
        WorldSave.Read(t.W, ms);
        Assert.Equal(s.War.Fs[s.Sk].Phase, t.War.Fs[t.Sk].Phase);
        Assert.Equal(s.War.Fs[s.Sk].Weariness, t.War.Fs[t.Sk].Weariness, 9);
        Assert.Equal(s.War.Fs[s.Sk].LostSeen, t.War.Fs[t.Sk].LostSeen);
    }
}
