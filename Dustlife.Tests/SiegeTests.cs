// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// F1 sieges (`tools/euler/siege_check.py`): a besieged place holds for its endurance, the besiegers stay for their patience,
/// the two sides hold fire until the place is ready to fall, the phases are told, a home town is overrun only at its endurance, a place
/// taken is nobody's target for the hold time, and a war host's leader rides out of a rout.
/// </summary>
public sealed class SiegeTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));
        public bool HostileToPlayer(int faction) => false;
    }

    sealed record Setup(World W, Warfare War, int Hn, int Sk, int Stack, Relations Rel);

    static Setup NewSiege(bool sieges = true, float hold = 0)
    {
        var w = new World();
        w.Rng.State = 7;
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        int stack = w.AddPlace("stack", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        var war = new Warfare(new WarfareConfig { SeedGarrisons = false, Sieges = sieges, HoldHours = hold });
        w.AddModule(war);
        w.StartNew();
        war.Ps[stack].NextDefense = war.Ps[stack].NextPatrol = 1e9;                       // no fresh garrisons or patrols: the siege alone is tested
        return new Setup(w, war, hn, sk, stack, new Relations((hn, sk)));
    }

    static void Hours(Setup s, double hours)
    {
        for (int i = 0; i < hours * 4; i++) s.W.Tick(0.25f, s.Rel);
    }

    static Band Besiege(Setup s, float patience)
    {
        var b = s.W.FindBand(s.W.SpawnBand(s.Sk, s.Stack, BandRole.Invader, new Vector2(900, 0), "warfare"))!;
        b.Leader[Axis.Patience] = patience;
        b.Target = s.Stack;
        b.TargetPos = Vector2.Zero;
        s.War.OnArrive(s.W, b, s.Rel);
        return b;
    }

    [Fact]
    public void Endurance_and_patience_are_the_checked_formulas()
    {
        var s = NewSiege();
        var stack = s.W.Places[s.Stack];
        Assert.Equal(24.0 * 10 * 1 * 1.0, s.War.EnduranceHours(s.W, stack), 6);           // a base, no garrison, food not short (no economy)
        for (int k = 0; k < 2; k++) s.W.SpawnBand(s.Hn, s.Stack, BandRole.Garrison, Vector2.Zero, "warfare");
        Assert.Equal(24.0 * 10 * 1.3, s.War.EnduranceHours(s.W, stack), 6);               // two bands: 1 + 0.15 * 2
        var b = Besiege(s, 0.5f);
        Assert.Equal(24.0 * (3 + 7 * 0.5), s.War.PatienceHours(s.W, b), 6);
        Assert.Equal(s.W.Now + 24.0 * (3 + 7 * 0.5), b.StayUntil, 6);
    }

    [Fact]
    public void Besiegers_and_defenders_hold_fire_until_the_place_is_ready_to_fall()
    {
        var s = NewSiege();
        var guard = s.W.FindBand(s.W.SpawnBand(s.Hn, s.Stack, BandRole.Garrison, Vector2.Zero, "warfare"))!;
        var b = Besiege(s, 1f);                                                            // patience 10 days, endurance 11.5 days
        Hours(s, 1);
        Assert.True(s.War.HoldsFire(s.W, b, guard));
        Assert.Equal(1, s.War.Ps[s.Stack].Phase);
        Hours(s, 24 * 6);
        Assert.Equal(2, s.War.Ps[s.Stack].Phase);                                          // starving at half its endurance
        Assert.Contains(s.W.Events, e => e.Key == "siege_starving");
        Assert.True(guard.Alive);                                                          // nobody fought yet
        Assert.Equal(guard.SizeMax, guard.Size);
    }

    [Fact]
    public void A_home_town_is_overrun_only_when_its_endurance_is_out()
    {
        var s = NewSiege();
        var b = Besiege(s, 1f);
        b.StayUntil = 1e9;                                                                 // a besieger who never leaves
        Hours(s, 24 * 9);
        Assert.Equal(s.Hn, s.W.Places[s.Stack].Owner);                                     // the old rule overran it after 12 h
        Hours(s, 24 * 2);
        Assert.Contains(s.W.Events, e => e.Key == "siege_ready");
        Assert.Contains(s.W.Events, e => e.Key == "overrun");
        var overrun = s.W.Events.First(e => e.Key == "overrun");
        Assert.InRange(overrun.Time, 240 - 0.5, 240 + 0.5);                                 // exactly at its endurance: 10 days, no garrison
        // (what follows is another rule: the Holy Nation, its only base lost, comes back for it)
    }

    [Fact]
    public void Off_by_default_the_old_rules_hold()
    {
        var s = NewSiege(sieges: false);
        var b = Besiege(s, 0.5f);
        Assert.Equal(s.W.Now + 24.0 + 48.0 * 0.5, b.StayUntil, 6);
        Hours(s, 13);
        Assert.NotEqual(s.Hn, s.W.Places[s.Stack].Owner);                                  // the 12 h home guard
    }

    [Fact]
    public void A_place_just_taken_is_nobodys_target_for_the_hold_time()
    {
        var s = NewSiege(hold: 7 * 24);
        int squin = s.W.AddPlace("squin", "Squin", new Vector2(30000, 0), PlaceKind.Base, 3, s.Sk);
        s.War.Ps[s.Stack].HoldUntil = s.W.Now + 7 * 24;
        for (int k = 0; k < 6; k++) s.W.SpawnBand(s.Sk, squin, BandRole.Garrison, s.W.Places[squin].Pos, "warfare");
        Hours(s, 24 * 3);
        Assert.DoesNotContain(s.War.Ps[squin].Targets, t => t == s.Stack);
        Assert.True(s.War.Fired.Held > 0);
    }

    [Fact]
    public void A_war_host_leader_rides_out_of_a_rout()
    {
        var w = new World();
        w.Rng.State = 3;
        w.Cfg.WarlordsSurvive = true;
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        var rel = new Relations((hn, sk));
        w.StartNew();
        int escapes = 0, routs = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            var at = new Vector2(0, trial * 100000);
            var lead = w.FindBand(w.SpawnBand(sk, -1, BandRole.Garrison, at, "t"))!;
            var refuge = w.FindBand(w.SpawnBand(sk, -1, BandRole.Garrison, at + new Vector2(0, 20000), "t"))!;   // out of the fight
            var enemy = w.FindBand(w.SpawnBand(hn, -1, BandRole.Garrison, at + new Vector2(300, 0), "t"))!;
            (lead.Size, enemy.Size, enemy.Skill) = (1, 10, 3);
            w.Factions[sk].Warlord = lead.Leader.Id;
            var warlord = lead.Leader;
            for (int k = 0; k < 40 && lead.Alive; k++) w.Tick(0.25f, rel);
            if (!lead.Alive) routs++;
            if (refuge.Leader == warlord) escapes++;
            foreach (var b in new[] { lead, refuge, enemy }) if (b.Alive) w.Kill(b);
        }

        Assert.True(routs > 100, $"only {routs} routs");
        Assert.InRange((double)escapes / routs, 0.6, 0.9);                                 // three in four, give or take
        Assert.Equal(escapes, w.WarlordEscapes);
    }

    [Fact]
    public void An_invested_town_raises_no_garrison_and_sends_no_patrols()
    {
        var s = NewSiege();
        s.War.Ps[s.Stack].NextDefense = s.War.Ps[s.Stack].NextPatrol = 0;                   // it would raise and patrol at once
        var b = Besiege(s, 1f);
        Hours(s, 0.5);                                                                     // the siege clock starts: phase 1
        int before = s.W.Bands.Count(x => x.Alive && x.Faction == s.Hn);
        Hours(s, 48);
        Assert.Equal(before, s.W.Bands.Count(x => x.Alive && x.Faction == s.Hn));
        Assert.True(s.War.Fired.CutOff > 0);
    }
}
