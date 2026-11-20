// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// Warfare's garrisons, budgets, sieges and ownership (port step 2b) on synthetic places.
/// </summary>
public sealed class WarfareTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    sealed record Setup(World W, Warfare War, int Hn, int Sk, Relations Rel);

    static Setup NewWar(ulong seed = 1, WarfareConfig? cfg = null)
    {
        var w = new World();
        w.Rng.State = seed;
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        var war = new Warfare(cfg ?? new WarfareConfig());
        w.AddModule(war);
        return new Setup(w, war, hn, sk, new Relations((hn, sk)));
    }

    static void Hours(Setup s, float hours)
    {
        for (int i = 0; i < hours * 4; i++) s.W.Tick(0.25f, s.Rel);
    }

    [Fact]
    public void Garrisons_are_seeded_by_kind_and_only_where_the_war_runs()
    {
        var s = NewWar();
        int fauna = s.W.AddFaction("beak", "Beak Things", true, false);
        int baseP = s.W.AddPlace("b", "Stack", new Vector2(0, 0), PlaceKind.Base, 3, s.Hn);
        int res = s.W.AddPlace("r", "Holy Farm", new Vector2(20000, 0), PlaceKind.Resource, 2, s.Hn);
        s.W.AddPlace("t", "Shek Ruins", new Vector2(40000, 0), PlaceKind.Territory, 1, s.Sk);
        s.W.AddPlace("n", "Nest", new Vector2(60000, 0), PlaceKind.Lair, 0, fauna);
        s.W.AddPlace("o", "Outpost", new Vector2(80000, 0), PlaceKind.Base, 2, s.Sk, playerOwned: true);

        s.W.StartNew();

        Assert.Equal(2, s.W.Bands.Count(b => b.Home == baseP));
        Assert.Equal(1, s.W.Bands.Count(b => b.Home == res));
        Assert.Equal(3, s.W.Bands.Count);
        Assert.Equal(3, s.War.Fired.Seeded);
        Assert.All(s.W.Bands, b => Assert.Equal("warfare", b.Owner));
    }

    [Fact]
    public void A_budget_is_what_a_faction_holds_and_shrinks_evenly_at_the_cap()
    {
        var s = NewWar();
        s.W.AddPlace("b", "Stack", Vector2.Zero, PlaceKind.Base, 3, s.Hn);
        s.W.AddPlace("r", "Holy Farm", new Vector2(20000, 0), PlaceKind.Resource, 2, s.Hn);
        s.W.AddPlace("q", "Squin", new Vector2(90000, 0), PlaceKind.Base, 3, s.Sk);

        s.W.Tick(0.25f, s.Rel);
        Assert.Equal(4 + 8 + 2, s.War.Fs[s.Hn].Budget);       // the base's cap and 8 per base, and the farm's cap
        Assert.Equal(4 + 8, s.War.Fs[s.Sk].Budget);

        s.W.Cfg.MaxBands = 10;                                  // this war's share: 8 bands, against 26 asked for
        s.W.Tick(0.25f, s.Rel);
        Assert.Equal((int)(14 * 8f / 26), s.War.Fs[s.Hn].Budget);
        Assert.Equal((int)(12 * 8f / 26), s.War.Fs[s.Sk].Budget);
    }

    [Fact]
    public void A_base_raises_its_garrison_back_within_the_defence_time_unless_out_of_budget()
    {
        var s = NewWar();
        int stack = s.W.AddPlace("b", "Stack", Vector2.Zero, PlaceKind.Base, 3, s.Hn);
        s.W.StartNew();
        foreach (var b in s.W.Bands.ToList()) s.W.Kill(b);

        Hours(s, 8 * 1.25f + 1);                                // the slowest respawn: the defence time's maximum, +25%
        Assert.Contains(s.W.Bands, b => b.Faction == s.Hn && b.Home == stack);
        Assert.True(s.War.Fired.Respawned > 0);

        // out of budget: the base's budget is its cap (4, nothing per base), and 4 of the faction's bands are on a long march (they hold
        // nothing, so the budget stays 4)
        var t = NewWar(cfg: new WarfareConfig { BudgetPerBase = 0, SeedGarrisons = false });
        int home = t.W.AddPlace("b", "Stack", Vector2.Zero, PlaceKind.Base, 3, t.Hn);
        t.W.StartNew();
        for (int i = 0; i < 4; i++)
        {
            var b = t.W.FindBand(t.W.SpawnBand(t.Hn, -1, BandRole.Garrison, new Vector2(500000, 0), "warfare"))!;
            t.W.SendTo(b, new Vector2(5000000, 0), BandRole.Garrison);
        }

        Hours(t, 12);
        Assert.Equal(4, t.W.Bands.Count(b => b.Faction == t.Hn));
        Assert.DoesNotContain(t.W.Bands, b => b.Home == home);
        Assert.True(t.War.Fired.Refused > 0);
    }

    [Fact]
    public void A_band_never_settles_on_an_enemy_place_it_besieges_from_the_edge()
    {
        var s = NewWar(cfg: new WarfareConfig { SeedGarrisons = false });
        int squin = s.W.AddPlace("q", "Squin", new Vector2(-30000, 0), PlaceKind.Base, 3, s.Sk);
        int stack = s.W.AddPlace("b", "Holy Farm", Vector2.Zero, PlaceKind.Resource, 2, s.Hn);   // a farm: a home guard, but raises no bands
        s.W.StartNew();
        var band = s.W.FindBand(s.W.SpawnBand(s.Sk, squin, BandRole.Invader, s.W.Places[squin].Pos, "warfare"))!;

        s.W.Send(band, stack, BandRole.Invader);
        Hours(s, 12);

        Assert.Equal(BandState.Siege, band.State);
        Assert.Equal(1000, Vector2.Distance(band.Pos, s.W.Places[stack].Pos), 1f);
        Assert.False(s.W.Settled(band, stack));
        Assert.True(s.War.Fired.Sieges > 0);
    }

    [Fact]
    public void A_real_town_holds_out_for_the_home_guard_time_then_is_overrun()
    {
        var s = NewWar(cfg: new WarfareConfig { SeedGarrisons = false });
        int squin = s.W.AddPlace("q", "Squin", new Vector2(-12000, 0), PlaceKind.Base, 3, s.Sk);
        int stack = s.W.AddPlace("b", "Holy Farm", Vector2.Zero, PlaceKind.Resource, 2, s.Hn);   // a farm: a home guard, but raises no bands
        s.W.StartNew();
        var band = s.W.FindBand(s.W.SpawnBand(s.Sk, squin, BandRole.Invader, s.W.Places[squin].Pos, "warfare", BandTier.Veteran))!;
        band.Leader[Axis.Patience] = 1;                         // stays 72 h: longer than the home guard holds
        s.W.Send(band, stack, BandRole.Invader);

        Hours(s, 6);
        Assert.Equal(BandState.Siege, band.State);
        Assert.Equal(s.Hn, s.W.Places[stack].Owner);            // besieged, but its own people still hold it
        Hours(s, 14);

        Assert.Contains(s.W.Events, e => e.Type == EventType.Lost && e.Key == "overrun" && e.Place == stack);
        Hours(s, 6);
        Assert.Equal(s.Sk, s.W.Places[stack].Owner);            // the besiegers walked in and took it
        Assert.Contains(s.W.Events, e => e.Type == EventType.Capture && e.Key == "hold" && e.Place == stack);
        Assert.Equal(1, band.Leader.Captures);
        Assert.Equal(BandRole.Garrison, band.Role);
        Assert.Equal(stack, band.Home);
    }

    [Fact]
    public void A_holder_keeps_its_place_while_one_band_is_there()
    {
        var s = NewWar(cfg: new WarfareConfig { SeedGarrisons = false });
        int squin = s.W.AddPlace("q", "Squin", new Vector2(-12000, 0), PlaceKind.Base, 3, s.Sk);
        int ruin = s.W.AddPlace("t", "Ruins", Vector2.Zero, PlaceKind.Territory, 1, s.Hn, real: false);
        s.W.StartNew();
        var guard = s.W.FindBand(s.W.SpawnBand(s.Hn, ruin, BandRole.Garrison, Vector2.Zero, "warfare"))!;
        guard.Size = guard.SizeMax = 200;                       // can't lose the fight
        guard.Skill = 3;
        var band = s.W.FindBand(s.W.SpawnBand(s.Sk, squin, BandRole.Invader, s.W.Places[squin].Pos, "warfare"))!;
        s.W.Send(band, ruin, BandRole.Invader);

        Hours(s, 24);

        Assert.Equal(s.Hn, s.W.Places[ruin].Owner);
        Assert.DoesNotContain(s.W.Events, e => e.Type is EventType.Capture or EventType.Lost);
    }

    [Fact]
    public void With_claims_holding_an_unguarded_place_keeps_its_holder_until_besieged()
    {
        foreach (bool claims in (bool[])[true, false])
        {
            var s = NewWar(cfg: new WarfareConfig { SeedGarrisons = false, ClaimsHold = claims });
            int ruin = s.W.AddPlace("t", "Ruins", Vector2.Zero, PlaceKind.Territory, 1, s.Hn);
            s.W.StartNew();
            Hours(s, 2);

            Assert.Equal(claims ? s.Hn : -1, s.W.Places[ruin].Owner);
            Assert.Equal(claims, s.War.Fired.ClaimsKept > 0);
        }
    }

    [Fact]
    public void A_besieger_gives_up_and_goes_home_when_its_patience_runs_out()
    {
        var s = NewWar(cfg: new WarfareConfig { SeedGarrisons = false, HomeGuardHours = 1000 });
        int squin = s.W.AddPlace("q", "Squin", new Vector2(-12000, 0), PlaceKind.Base, 3, s.Sk);
        int stack = s.W.AddPlace("b", "Holy Farm", Vector2.Zero, PlaceKind.Resource, 2, s.Hn);   // a farm: a home guard, but raises no bands
        s.W.StartNew();
        var band = s.W.FindBand(s.W.SpawnBand(s.Sk, squin, BandRole.Invader, s.W.Places[squin].Pos, "warfare"))!;
        band.Leader[Axis.Patience] = 0;                         // gives up after 24 h
        s.W.Send(band, stack, BandRole.Invader);

        Hours(s, 36);

        Assert.Contains(s.W.Events, e => e.Key == "siege_end");
        Assert.Equal(squin, band.Target);
        Assert.Equal(s.Hn, s.W.Places[stack].Owner);
        Assert.True(s.War.Fired.SiegesGivenUp > 0);
    }

    [Fact]
    public void Another_modules_bands_are_left_alone()
    {
        var s = NewWar(cfg: new WarfareConfig { SeedGarrisons = false });
        int stack = s.W.AddPlace("b", "Holy Farm", Vector2.Zero, PlaceKind.Resource, 2, s.Hn);   // a farm: a home guard, but raises no bands
        s.W.StartNew();
        var band = s.W.FindBand(s.W.SpawnBand(s.Sk, stack, BandRole.Invader, new Vector2(30000, 0), "fauna"))!;

        Assert.False(s.War.Think(s.W, band, s.Rel));
        Assert.False(s.War.OnArrive(s.W, band, s.Rel));
    }
}
