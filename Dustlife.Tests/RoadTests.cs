// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// The road graph (port step 4): road distance, supply along open roads, cut roads and the lifeline motive, on synthetic places.
/// </summary>
public sealed class RoadTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    [Fact]
    public void Distance_follows_the_roads_and_falls_back_to_twice_the_straight_line()
    {
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int a = w.AddPlace("a", "A", Vector2.Zero, PlaceKind.Base, 3, hn);
        int b = w.AddPlace("b", "B", new Vector2(10000, 0), PlaceKind.Territory, 1, hn);
        int c = w.AddPlace("c", "C", new Vector2(20000, 0), PlaceKind.Territory, 1, hn);
        int d = w.AddPlace("d", "D", new Vector2(0, 5000), PlaceKind.Territory, 1, hn);
        var roads = new Roads();
        Assert.Equal(0, roads.Add(a, b, 12000));
        Assert.Equal(1, roads.Add(b, c, 13000));
        Assert.Equal(-1, roads.Add(b, a, 1));                   // the pair once
        Assert.Equal(-1, roads.Add(c, c, 1));                   // never to itself
        Assert.Equal(-1, roads.Add(a, c, float.NaN));
        w.Roads = roads;

        Assert.Equal(25000, w.TravelDist(a, c), 0.01f);
        Assert.Equal(25000, w.TravelDist(c, a), 0.01f);
        Assert.Equal(10000, w.TravelDist(a, d), 0.01f);         // no road: twice the straight line
        roads.Add(a, c, 21000);                                 // a new road clears the kept paths
        Assert.Equal(21000, w.TravelDist(a, c), 0.01f);
        w.Roads = null;
        Assert.Equal(20000, w.TravelDist(a, c), 0.01f);
    }

    [Fact]
    public void With_roads_a_base_aims_at_what_is_near_by_road_not_as_the_crow_flies()
    {
        foreach (bool withRoads in (bool[])[true, false])
        {
            var w = new World();
            int hn = w.AddFaction("hn", "Holy Nation", false, false);
            int sk = w.AddFaction("sk", "Shek", false, false);
            int home = w.AddPlace("h", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
            int cliff = w.AddPlace("x", "Across the Cliff", new Vector2(10000, 0), PlaceKind.Territory, 2, sk);   // near, but the road goes round
            int road = w.AddPlace("y", "Down the Road", new Vector2(0, 30000), PlaceKind.Territory, 2, sk);
            int far = w.AddPlace("z", "Far Camp", new Vector2(0, -60000), PlaceKind.Territory, 2, sk);
            if (withRoads)
            {
                w.Roads = new Roads();
                w.Roads.Add(home, road, 30000);
                w.Roads.Add(road, cliff, 190000);
                w.Roads.Add(home, far, 60000);
            }

            var war = new Warfare(new WarfareConfig { SeedGarrisons = false, ExpansionAggression = 1 });
            w.AddModule(war);
            w.StartNew();
            for (int i = 0; i < 16; i++) w.Tick(0.25f, new Relations((hn, sk)));

            var t = war.Ps[home].Targets;
            Assert.Equal(withRoads ? road : cliff, t[0]);
            if (withRoads) Assert.DoesNotContain(cliff, t);     // round by road it is further than the far camp
        }
    }

    /// <summary>A Holy Nation base, its farm beyond a town, and the Shek who may hold the town.</summary>
    static (World W, Economy E, int Hn, int Sk, int Home, int Town, int Farm, Relations Rel) Line(bool shekTown, MotiveConfig? motives = null)
    {
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        int home = w.AddPlace("h", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        int town = w.AddPlace("t", "Waystation", new Vector2(30000, 0), PlaceKind.Territory, 1, shekTown ? sk : -1);
        int farm = w.AddPlace("f", "Holy Farm", new Vector2(60000, 0), PlaceKind.Resource, 2, hn);
        w.AddPlace("q", "Squin", new Vector2(30000, 50000), PlaceKind.Base, 3, sk);
        w.Roads = new Roads();
        w.Roads.Add(home, town, 30000);
        w.Roads.Add(town, farm, 30000);
        w.Roads.Add(town, 3, 50000);
        var e = new Economy(new EconomyConfig { CutRoads = true });
        w.AddModule(e);
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false, ExpansionAggression = 1, ClaimsHold = true, UseEconomy = true, Motives = motives }));
        w.StartNew();
        return (w, e, hn, sk, home, town, farm, new Relations((hn, sk)));
    }

    [Fact]
    public void A_place_whose_road_home_runs_through_an_enemy_town_is_cut_off_and_produces_nothing()
    {
        var (w, e, hn, _, home, town, farm, rel) = Line(shekTown: true);
        w.Tick(0.25f, rel);
        var ok = e.Supplied(w, hn);
        Assert.True(ok[home]);
        Assert.False(ok[farm]);
        Assert.Equal(3, e.ProdFood[hn], 0.001f);                // the base's food only: the farm's never arrives
        Assert.True(e.CutHours > 0);
        Assert.Contains(town, e.Cutters(w, hn));

        var open = Line(shekTown: false);                       // the town empty: the road is open
        open.W.Tick(0.25f, open.Rel);
        Assert.True(open.E.Supplied(open.W, open.Hn)[open.Farm]);
        Assert.Equal(3 + 2.5f, open.E.ProdFood[open.Hn], 0.001f);
        Assert.Empty(open.E.Cutters(open.W, open.Hn));
    }

    [Fact]
    public void A_band_camped_on_the_road_cuts_it_one_camped_at_a_town_does_not()
    {
        var (w, _, hn, sk, home, _, farm, rel) = Line(shekTown: false);
        var raiders = w.FindBand(w.SpawnBand(sk, -1, BandRole.Hunt, new Vector2(45000, 800), "test"))!;
        raiders.State = BandState.Settled;
        var ok = w.Roads!.Supplied(w, hn, rel);
        Assert.False(ok[farm]);
        Assert.True(w.Roads.BlockedByBand > 0);

        raiders.Pos = new Vector2(30000, 500);                  // in the empty town itself, not on the road
        Assert.True(w.Roads.Supplied(w, hn, rel)[farm]);
        raiders.State = BandState.Moving;                       // on the march, not camped: the road is open
        raiders.Pos = new Vector2(45000, 800);
        Assert.True(w.Roads.Supplied(w, hn, rel)[farm]);
        _ = home;
    }

    [Fact]
    public void The_town_that_cuts_the_road_becomes_a_lifeline_target()
    {
        var (w, e, hn, sk, home, town, _, rel) = Line(shekTown: true, new MotiveConfig());
        for (int i = 0; i < 16; i++) w.Tick(0.25f, rel);
        var war = (Warfare)w.Modules.First(m => m is Warfare);
        Assert.Equal(town, war.Ps[home].Targets[0]);
        Assert.Equal(Motive.Lifeline, war.WhyTarget(hn, town));
        _ = (e, sk);
    }

    [Fact]
    public void A_place_off_the_road_graph_is_never_cut()
    {
        var (w, e, hn, _, _, _, _, rel) = Line(shekTown: true);
        int hut = w.AddPlace("o", "Lone Hut", new Vector2(-40000, 0), PlaceKind.Territory, 2, hn);
        w.Tick(0.25f, rel);
        Assert.True(e.Supplied(w, hn)[hut]);
    }
}
