// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Tests;

/// <summary>
/// The core world on synthetic places (port step 2a, RivalryCore#29): no game, no install.
/// </summary>
public sealed class CoreTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    static World NewWorld(ulong seed = 1)
    {
        var w = new World();
        w.Rng.State = seed;
        return w;
    }

    [Fact]
    public void The_generator_matches_the_reference_bit_for_bit()
    {
        // xorshift64* from the default state; the values were computed from the algorithm, independently of this code
        var r = new Rng();
        Assert.Equal(0x0d83b3e29a21487aUL, r.Next());
        Assert.Equal(0x54c44c79f1fe9d67UL, r.Next());
        Assert.Equal(0xa845f342007a0e78UL, r.Next());
        Assert.Equal(0.052790820598602295f, new Rng().Uniform());
    }

    [Fact]
    public void A_sentence_fills_its_arguments_and_never_reads_one_as_a_format()
    {
        var t = new Text();
        Assert.Equal("Krul's men broke and ran.", t.Format("rout", ["Krul"]));
        Assert.Equal("{1}'s men broke and ran.", t.Format("rout", ["{1}"]));
        Assert.Equal("no_such_key", t.Get("no_such_key"));
    }

    [Fact]
    public void Bad_places_never_get_in()
    {
        var w = NewWorld();
        int f = w.AddFaction("hn", "Holy Nation", false, false);
        int nan = w.AddPlace("p1", "Nowhere", new Vector2(float.NaN, 5), PlaceKind.Base, 500, 99);

        var p = w.Places[nan];
        Assert.Equal(Vector2.Zero, p.Pos);
        Assert.Equal(1, w.Repairs);
        Assert.Equal(-1, p.Owner);
        Assert.Equal(100, p.Resource);
        Assert.Equal(4, p.Cap);
        Assert.Equal(nan, w.AddPlace("p1", "Again", Vector2.One, PlaceKind.Lair, 0, f));    // a key is added once
        Assert.Equal(f, w.AddFaction("hn", "Again", true, true));
    }

    [Fact]
    public void A_band_is_sized_by_its_quality_and_refused_at_the_cap()
    {
        var w = NewWorld();
        int f = w.AddFaction("hn", "Holy Nation", false, false);
        int home = w.AddPlace("p", "Stack", Vector2.Zero, PlaceKind.Base, 3, f);
        w.Cfg.MaxBands = 20;

        for (int i = 0; i < 20; i++) Assert.True(w.SpawnBand(f, home, BandRole.Garrison, Vector2.Zero, "test") > 0);

        Assert.Equal(-1, w.SpawnBand(f, home, BandRole.Garrison, Vector2.Zero, "test"));
        Assert.All(w.Bands, b => Assert.InRange(b.Size, w.Cfg.SizeMin, w.Cfg.SizeMax));
        Assert.All(w.Bands, b => Assert.InRange(b.Skill, 0.5f, 3.0f));
        Assert.Equal(-1, w.SpawnBand(f, home, BandRole.Garrison, new Vector2(float.PositiveInfinity, 0), "test"));
        Assert.Equal(-1, w.SpawnBand(7, home, BandRole.Garrison, Vector2.Zero, "test"));
    }

    [Fact]
    public void A_band_walks_at_the_set_speed_and_settles_on_arrival()
    {
        var w = NewWorld();
        int f = w.AddFaction("hn", "Holy Nation", false, false);
        int a = w.AddPlace("a", "Stack", Vector2.Zero, PlaceKind.Base, 3, f);
        int b = w.AddPlace("b", "Bad Teeth", new Vector2(30000, 0), PlaceKind.Base, 3, f);
        var band = w.FindBand(w.SpawnBand(f, a, BandRole.Garrison, Vector2.Zero, "test"))!;
        var rel = new Relations();

        w.Send(band, b, BandRole.Garrison);
        float start = Vector2.Distance(band.Pos, band.TargetPos);
        for (int i = 0; i < 8 * 4; i++) w.Tick(0.25f, rel);       // 8 hours at 3,000 units an hour: 24,000 units

        Assert.Equal(BandState.Moving, band.State);
        Assert.Equal(start - 24000, Vector2.Distance(band.Pos, band.TargetPos), 1f);
        for (int i = 0; i < 4 * 4; i++) w.Tick(0.25f, rel);
        Assert.Equal(BandState.Settled, band.State);
        Assert.True(w.Settled(band, b));
    }

    [Fact]
    public void Hostile_bands_fight_and_a_dead_band_is_counted_once()
    {
        var w = NewWorld();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        int p = w.AddPlace("p", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        var rel = new Relations((hn, sk));
        for (int i = 0; i < 3; i++)
        {
            w.SpawnBand(hn, p, BandRole.Garrison, Vector2.Zero, "test");
            w.SpawnBand(sk, p, BandRole.Invader, Vector2.Zero, "test");
        }

        int men = w.Bands.Sum(b => b.Size);
        for (int i = 0; i < 400 && w.Bands.Select(b => b.Faction).Distinct().Count() > 1; i++) w.Tick(0.25f, rel);

        Assert.Contains(w.Events, e => e.Type == EventType.Battle);
        Assert.True(w.Bands.Sum(b => b.Size) < men);
        int wiped = w.Events.Count(e => e.Type == EventType.Wiped);
        Assert.Equal(wiped, w.Factions[hn].Lost + w.Factions[sk].Lost);
        Assert.All(w.Bands, b => Assert.True(b.Alive));          // the dead are pruned each tick
    }

    [Fact]
    public void Friends_never_fight_fauna_fight_people_and_not_each_other()
    {
        var w = NewWorld();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int uc = w.AddFaction("uc", "United Cities", false, false);
        int beak = w.AddFaction("beak", "Beak Things", true, false);
        int gor = w.AddFaction("gor", "Gorillos", true, false);
        var rel = new Relations();

        Assert.False(w.Hostile(hn, uc, rel));
        Assert.False(w.Hostile(hn, hn, rel));
        Assert.True(w.Hostile(hn, beak, rel));
        Assert.False(w.Hostile(beak, gor, rel));
        w.Cfg.AllOutWar = true;
        Assert.True(w.Hostile(hn, uc, rel));
        Assert.False(w.Hostile(beak, gor, rel));
    }

    [Fact]
    public void A_broken_band_runs_home()
    {
        // over many seeds: every rout sends the loser back to its home place, at retreat speed
        int routs = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var w = NewWorld(seed);
            int hn = w.AddFaction("hn", "Holy Nation", false, false);
            int sk = w.AddFaction("sk", "Shek", false, false);
            int home = w.AddPlace("home", "Squin", new Vector2(50000, 0), PlaceKind.Base, 3, sk);
            int p = w.AddPlace("p", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
            var rel = new Relations((hn, sk));
            w.SpawnBand(hn, p, BandRole.Garrison, Vector2.Zero, "test", BandTier.Veteran);
            w.SpawnBand(hn, p, BandRole.Garrison, Vector2.Zero, "test", BandTier.Veteran);
            var raider = w.FindBand(w.SpawnBand(sk, home, BandRole.Invader, Vector2.Zero, "test", BandTier.Novice))!;

            w.Tick(0.25f, rel);

            if (w.Events.Any(e => e.Type == EventType.Rout && e.B == sk))
            {
                routs++;
                Assert.Equal(BandState.Retreat, raider.State);
                Assert.Equal(home, raider.Target);
            }
        }

        Assert.True(routs > 0);
    }

    [Fact]
    public void The_same_seed_gives_the_same_war()
    {
        static List<string> Run(ulong seed)
        {
            var w = NewWorld(seed);
            int hn = w.AddFaction("hn", "Holy Nation", false, false);
            int sk = w.AddFaction("sk", "Shek", false, false);
            int p = w.AddPlace("p", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
            var rel = new Relations((hn, sk));
            for (int i = 0; i < 4; i++)
            {
                w.SpawnBand(hn, p, BandRole.Garrison, Vector2.Zero, "test");
                w.SpawnBand(sk, p, BandRole.Invader, Vector2.Zero, "test");
            }

            for (int i = 0; i < 200; i++) w.Tick(0.25f, rel);
            return [.. w.Events.Select(e => w.Say(e))];
        }

        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    [Fact]
    public void Catching_up_is_capped_and_never_runs_on_a_bad_clock()
    {
        var w = NewWorld();
        var rel = new Relations();

        Assert.Equal(0, w.Advance(100, rel));                    // the first call only sets the clock
        Assert.Equal(48, w.Advance(100 + 24 * 365, rel));        // a year away: 48 steps now, the rest on later calls
        Assert.True(w.Now >= 100 + 24 * 365 - 24 * 30);          // never more than a month behind
        Assert.Equal(0, w.Advance(double.NaN, rel));
        double now = w.Now;
        w.Tick(float.NaN, rel);
        w.Tick(-1, rel);
        Assert.Equal(now, w.Now);
    }
}
