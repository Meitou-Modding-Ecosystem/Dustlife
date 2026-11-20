// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// The economy, fauna and slavery (port step 3) on synthetic places.
/// </summary>
public sealed class EconomyTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    static void Hours(World w, IRelations rel, float hours)
    {
        for (int i = 0; i < hours * 4; i++) w.Tick(0.25f, rel);
    }

    static (World W, Economy E, Warfare War, int Uc, int St) NewEconomy(bool production = true)
    {
        var w = new World();
        int uc = w.AddFaction("uc", "United Cities", false, false);
        int st = w.AddFaction("st", "Slave Traders", false, true);
        w.AddPlace("heft", "Heft", Vector2.Zero, PlaceKind.Base, 3, uc);
        w.AddPlace("farm", "Slave Farm", new Vector2(20000, 0), PlaceKind.Resource, 2, st);
        var e = new Economy(new EconomyConfig { Production = production, Profiles = [new EconomyProfile { Faction = "st", PoolOf = "uc", FoodMult = 2 }] });
        var war = new Warfare(new WarfareConfig { UseEconomy = true });
        w.AddModule(e);
        w.AddModule(war);
        w.StartNew();
        return (w, e, war, uc, st);
    }

    [Fact]
    public void A_bloc_pools_its_output_and_its_upkeep()
    {
        var (w, e, _, uc, st) = NewEconomy();
        w.Tick(0.25f, new Relations());

        Assert.Equal(uc, e.PoolOf(w, st));
        Assert.Equal(3 + 2.5f * 2, e.ProdFood[uc], 0.001f);     // the base's food, and the slave farm's at its profile's double
        Assert.Equal(0, e.ProdFood[st]);
        Assert.Equal(w.Bands.Sum(b => b.Size) * 0.1f, e.Upkeep[uc], 0.001f);
    }

    [Fact]
    public void Without_production_the_garrisons_shrink()
    {
        // the reference's falsifier: with nothing produced the stock runs out and men desert
        var (w, e, _, uc, _) = NewEconomy(production: false);
        int men = w.Bands.Sum(b => b.Size);
        Hours(w, new Relations(), 24 * 60);

        Assert.True(e.Deserted[uc] + e.Deserted[1] > 0);
        Assert.True(w.Bands.Sum(b => b.Size) < men);
        Assert.True(e.HungryHours[uc] > 0);
    }

    [Fact]
    public void A_band_is_raised_only_when_the_pool_can_arm_and_feed_it()
    {
        var (w, e, _, uc, _) = NewEconomy();
        e.Food[uc] = 1000;                                      // food enough, but no arms
        e.Arms[uc] = 0;
        Assert.False(e.CanRaise(w, uc, 6));
        e.Arms[uc] = 1000;                                      // arms enough, but no food
        e.Food[uc] = 0;
        Assert.False(e.CanRaise(w, uc, 6));
        e.Arms[uc] = 1000;
        e.Food[uc] = 1000;
        Assert.True(e.CanRaise(w, uc, 6));
        float arms = e.Arms[uc];
        e.PayRaise(w, uc, 6);
        Assert.Equal(arms - 6 * 0.25f, e.Arms[uc], 0.001f);
        Assert.Equal(1, e.Raised[uc]);
    }

    [Fact]
    public void Animals_fill_their_nests_and_never_hold_a_place()
    {
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int beak = w.AddFaction("beak", "Beak Things", true, false);
        int nest = w.AddPlace("nest", "Nest", Vector2.Zero, PlaceKind.Lair, 0, beak);
        int ruin = w.AddPlace("ruin", "Ruins", new Vector2(10000, 0), PlaceKind.Territory, 1, hn);
        var fauna = new Fauna();
        w.AddModule(fauna);
        w.AddModule(new Warfare(new WarfareConfig { ClaimsHold = true }));
        w.StartNew();

        Assert.Single(w.Bands, b => b.Faction == beak && b.Home == nest);
        Hours(w, new Relations(), 24 * 5);

        Assert.All(w.Bands.Where(b => b.Faction == beak), b => Assert.Equal("fauna", b.Owner));
        Assert.DoesNotContain(w.Places, p => p.Owner == beak && p.Id != nest);
        Assert.Equal(hn, w.Places[ruin].Owner);
    }

    [Fact]
    public void A_slaver_who_wins_takes_prisoners()
    {
        int taken = 0;
        for (ulong seed = 1; seed <= 100 && taken == 0; seed++)
        {
            var w = new World();
            w.Rng.State = seed;
            int st = w.AddFaction("st", "Slave Traders", false, true);
            int sk = w.AddFaction("sk", "Shek", false, false);
            int p = w.AddPlace("p", "Slave Farm", Vector2.Zero, PlaceKind.Resource, 2, st);
            var slavery = new Slavery();
            w.AddModule(slavery);
            var strong = w.FindBand(w.SpawnBand(st, p, BandRole.Garrison, Vector2.Zero, "test", BandTier.Veteran))!;
            strong.Size = strong.SizeMax = 12;                  // stronger, but not enough to wipe the losers out: they break and run
            var weak = w.FindBand(w.SpawnBand(sk, p, BandRole.Invader, Vector2.Zero, "test", BandTier.Novice))!;
            weak.Size = weak.SizeMax = 10;
            w.Tick(0.25f, new Relations((st, sk)));
            taken = slavery.Slaves.Count > st ? slavery.Slaves[st] : 0;
            if (taken > 0) Assert.Contains(w.Events, e => e.Type == EventType.Slaves && e.A == st);
        }

        Assert.True(taken > 0);
    }
}
