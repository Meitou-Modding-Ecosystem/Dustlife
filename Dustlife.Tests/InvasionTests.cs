// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// Warfare's targets, invasions, patrols, comebacks and doctrines (port step 2c) on a small synthetic map: two Shek bases and two Holy
/// Nation bases 40 km apart, a Holy farm and a Holy ruin between them, and an empty ruin.
/// </summary>
public sealed class InvasionTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    sealed record Map(World W, Warfare War, int Hn, int Sk, Relations Rel)
    {
        public int Place(string name) => W.Places.FindIndex(p => p.Name == name);

        public void Days(float days)
        {
            for (int i = 0; i < days * 24 * 4; i++) W.Tick(0.25f, Rel);
        }

        public bool Marched(int f, string target) =>
            W.Events.Concat(W.Chronicle).Any(e => e.Type == EventType.Invasion && e.A == f && e.Place == Place(target));
    }

    static Map NewMap(ulong seed = 1, params Doctrine[] doctrines)
    {
        var w = new World();
        w.Rng.State = seed;
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        w.AddPlace("squin", "Squin", new Vector2(-40000, 0), PlaceKind.Base, 3, sk);
        w.AddPlace("admag", "Admag", new Vector2(-40000, 20000), PlaceKind.Base, 3, sk);
        w.AddPlace("stack", "Stack", new Vector2(40000, 0), PlaceKind.Base, 3, hn);
        w.AddPlace("blister", "Blister Hill", new Vector2(40000, 20000), PlaceKind.Base, 3, hn);
        w.AddPlace("farm", "Holy Farm", new Vector2(0, 0), PlaceKind.Resource, 2, hn);
        w.AddPlace("ruin", "Holy Ruin", new Vector2(0, 15000), PlaceKind.Territory, 1, hn);
        w.AddPlace("empty", "Old Ruin", new Vector2(-10000, -15000), PlaceKind.Territory, 1, -1);
        var cfg = new WarfareConfig { ExpansionAggression = 1, Doctrines = [.. doctrines] };
        var war = new Warfare(cfg);
        w.AddModule(war);
        w.StartNew();
        return new Map(w, war, hn, sk, new Relations((hn, sk)));
    }

    [Fact]
    public void Bases_pick_targets_and_march_on_them()
    {
        var m = NewMap();
        m.Days(10);

        Assert.True(m.War.Fired.Invasions > 0);
        Assert.True(m.War.Fired.Claims > 0);                    // the empty ruin is claimed, not attacked
        Assert.Contains(m.W.Events.Concat(m.W.Chronicle), e => e.Key == "march");
        Assert.True(m.War.Fired.Captures > 0);
    }

    [Fact]
    public void Patrols_go_out_to_empty_or_own_places()
    {
        var m = NewMap();
        m.Days(4);

        Assert.True(m.War.Fired.Patrols > 0);
    }

    [Fact]
    public void Never_is_never_attacked_and_order_comes_first()
    {
        var shek = new Doctrine { Faction = "sk", Never = ["Holy Farm"], Order = ["Stack"] };
        var m = NewMap(1, shek);
        m.Days(10);

        Assert.False(m.Marched(m.Sk, "Holy Farm"));
        Assert.True(m.War.Fired.Filtered > 0);
        Assert.True(m.War.Fired.Ordered > 0);
        Assert.True(m.Marched(m.Sk, "Stack"));
    }

    [Fact]
    public void A_faction_that_holds_and_defends_never_invades()
    {
        var m = NewMap(1, new Doctrine { Faction = "hn", NoInvade = true });
        m.Days(10);

        Assert.DoesNotContain(m.W.Events.Concat(m.W.Chronicle), e => e.Type == EventType.Invasion && e.A == m.Hn);
        Assert.True(m.War.Fired.NoInvade > 0);
    }

    [Fact]
    public void A_weak_gate_waits_while_the_defenders_are_strong()
    {
        // the Shek attack only places defended below 1 power: every Holy garrison is stronger, so no Shek march on a held place
        var m = NewMap(1, new Doctrine { Faction = "sk", WeakBelow = 1 });
        m.Days(6);

        Assert.True(m.War.Fired.WeakWait > 0);
        Assert.DoesNotContain(m.W.Events.Concat(m.W.Chronicle), e => e.Type == EventType.Invasion && e.A == m.Sk && e.Key == "march"
            && m.W.SettledPower(e.Place, m.Hn) >= 1);
    }

    [Fact]
    public void Enemies_limit_the_targets_and_a_front_opens_a_border_war()
    {
        // the Shek fight only the UC (nobody here), so nothing Holy is a target, except inside a front round the ruin
        var front = new Front { Center = new Vector2(0, 15000), Radius = 3000, Enemies = ["hn"], TerritoryOnly = true, EveryHours = 48 };
        var m = NewMap(1, new Doctrine { Faction = "sk", Enemies = ["uc"], Fronts = [front] });
        m.Days(12);

        Assert.False(m.Marched(m.Sk, "Stack"));
        Assert.False(m.Marched(m.Sk, "Holy Farm"));
        Assert.True(m.War.Fired.FrontTargets > 0);
        Assert.True(m.Marched(m.Sk, "Holy Ruin"));
    }

    [Fact]
    public void Ease_off_stops_the_main_war_but_not_a_border_war()
    {
        // the Shek hold two former Holy places, over the limit of 1 (0 means no rule), so their main war eases off; the border war goes on
        var front = new Front { Center = new Vector2(0, 15000), Radius = 3000, Enemies = ["hn"], TerritoryOnly = true, Raids = true };
        var shek = new Doctrine { Faction = "sk", EaseFaction = "sk", EaseVictim = "hn", EaseAbove = 1, Fronts = [front] };
        var m = NewMap(1, shek);
        foreach (var name in (string[])["Holy Farm", "Old Ruin"])
        {
            var p = m.W.Places[m.Place(name)];
            (p.Owner, p.OrigOwner) = (m.Sk, name == "Old Ruin" ? m.Hn : p.OrigOwner);
            m.W.Bands.Where(b => b.Home == p.Id).ToList().ForEach(m.W.Kill);
            var holder = m.W.FindBand(m.W.SpawnBand(m.Sk, p.Id, BandRole.Garrison, p.Pos, "warfare"))!;
            holder.Size = holder.SizeMax = 200;                 // holds it for the whole run
            holder.Skill = 3;
        }

        Assert.True(Warfare.Easing(m.W, shek));
        m.Days(10);

        Assert.True(m.War.Fired.Eased > 0);
        Assert.False(m.Marched(m.Sk, "Stack"));
        Assert.False(m.Marched(m.Sk, "Blister Hill"));
        Assert.True(m.Marched(m.Sk, "Holy Ruin"));
        Assert.True(m.War.Fired.Raids > 0);
    }

    [Fact]
    public void Odds_and_skill_doctrines_act_on_shek_bands()
    {
        var m = NewMap(1, new Doctrine { Faction = "sk", OddsAbove = 50, SkillMult = 1.5f });
        m.Days(6);

        Assert.True(m.War.Fired.Skilled > 0);
        Assert.True(m.War.Fired.OddsWait > 0);                 // 50 times the defenders' power is never reached
        Assert.DoesNotContain(m.W.Events.Concat(m.W.Chronicle), e => e.Type == EventType.Invasion && e.A == m.Sk && e.Key == "march");
    }

    [Fact]
    public void A_home_garrison_never_goes_below_its_keep()
    {
        var m = NewMap(1, new Doctrine { Faction = "sk", Home = ["Squin"], HomeKeep = 3 });
        int squin = m.Place("Squin");
        int lowest = int.MaxValue;
        for (int h = 0; h < 10 * 24; h++)
        {
            for (int q = 0; q < 4; q++) m.W.Tick(0.25f, m.Rel);
            if (m.W.Places[squin].Owner == m.Sk && m.War.Fired.HomeKept > 0) lowest = Math.Min(lowest, m.W.CountSettled(squin, m.Sk));
        }

        Assert.True(m.War.Fired.HomeKept > 0);
        Assert.True(lowest >= 3 || lowest == int.MaxValue);
    }

    [Fact]
    public void A_faction_without_bases_comes_back()
    {
        var m = NewMap();
        foreach (var p in m.W.Places.Where(p => p.Owner == m.Sk)) p.Owner = m.Hn;     // the Shek lose everything
        m.W.Bands.Where(b => b.Faction == m.Sk).ToList().ForEach(m.W.Kill);
        m.Days(6);                                              // the first comeback is due within 96 hours

        Assert.True(m.War.Fired.Resurgences > 0);
        Assert.Contains(m.W.Events.Concat(m.W.Chronicle), e => e.Type == EventType.Resurgence && e.A == m.Sk);
    }

    [Fact]
    public void A_counter_strike_goes_for_an_empty_capital()
    {
        // the Holy Nation strikes Squin when the Shek have under 1 band settled at their bases
        var m = NewMap(1, new Doctrine { Faction = "hn", CounterTarget = "Squin", CounterFaction = "sk", CounterHomeBelow = 1 });
        m.W.Bands.Where(b => b.Faction == m.Sk).ToList().ForEach(m.W.Kill);
        m.Days(1);

        Assert.True(m.War.Fired.Counter > 0);
    }

    [Fact]
    public void War_pacing_holds_a_faction_to_one_assault_at_a_time()
    {
        var m = NewMap(1, new Doctrine { Faction = "sk", WarEveryHours = 1000 });
        m.Days(10);

        Assert.True(m.War.Fired.WarPaced > 0);
        Assert.True(m.W.Events.Concat(m.W.Chronicle).Count(e => e.Type == EventType.Invasion && e.A == m.Sk && e.Key == "march") <= 1);
    }

    [Fact]
    public void A_patient_captain_goes_with_fewer_men_after_waiting()
    {
        var m = NewMap(1);
        m.War.Cfg.MusterPatienceHours = 6;
        m.W.Cfg.MaxBands = 12;                                  // too few bands for full-size invasions
        m.Days(6);

        Assert.True(m.War.Fired.MusterShrunk > 0);
    }

    [Fact]
    public void A_border_war_waits_while_the_border_place_is_strongly_held()
    {
        var front = new Front { Center = new Vector2(0, 0), Radius = 3000, Enemies = ["hn"], WeakBelow = 1, Raids = true };
        var m = NewMap(1, new Doctrine { Faction = "sk", Enemies = ["uc"], Fronts = [front] });
        m.Days(6);

        Assert.True(m.War.Fired.FrontWait > 0);
        Assert.False(m.Marched(m.Sk, "Holy Farm"));             // its garrison is always stronger than 1
    }

    [Fact]
    public void The_same_seed_gives_the_same_war()
    {
        static List<string> Run(ulong seed)
        {
            var m = NewMap(seed, new Doctrine { Faction = "sk", Order = ["Stack"], OddsAbove = 1.3f });
            m.Days(8);
            return [.. m.W.Chronicle.Concat(m.W.Events).Select(e => m.W.Say(e))];
        }

        Assert.Equal(Run(3), Run(3));
        Assert.NotEqual(Run(3), Run(4));
    }
}
