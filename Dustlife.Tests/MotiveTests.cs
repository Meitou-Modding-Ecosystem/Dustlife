// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// Warfare's motive rules: a target has a reason, and a taking says which.
/// </summary>
public sealed class MotiveTests
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

    static WarfareConfig Cfg(MotiveConfig? m) => new() { SeedGarrisons = false, ExpansionAggression = 1, Motives = m };

    /// <summary>
    /// A Holy Nation base with a Shek base nearer than a Shek farm; the economy's food set by <paramref name="food"/>.
    /// </summary>
    static (World W, Warfare War, int Hn, int Base, int Farm, Relations Rel) Hunger(float food)
    {
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        int home = w.AddPlace("h", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        int baseP = w.AddPlace("b", "Squin", new Vector2(20000, 0), PlaceKind.Base, 3, sk);
        int farm = w.AddPlace("f", "Shek Farm", new Vector2(0, 25000), PlaceKind.Resource, 2, sk);
        var e = new Economy(new EconomyConfig { Production = false });
        var war = new Warfare(Cfg(new MotiveConfig { RequireMotive = false }));
        w.AddModule(e);
        w.AddModule(war);
        w.StartNew();
        var g = w.FindBand(w.SpawnBand(hn, home, BandRole.Garrison, Vector2.Zero, "test"))!;   // another module's band: eats, never marches
        g.Size = g.SizeMax = 10;
        e.Food[hn] = food;
        e.Arms[hn] = 1000;
        e.Food[sk] = e.Arms[sk] = 1000;
        _ = home;
        return (w, war, hn, baseP, farm, new Relations((hn, sk)));
    }

    [Fact]
    public void A_hungry_faction_goes_for_the_farm_a_fed_one_for_the_nearer_base()
    {
        var (w, war, hn, baseP, farm, rel) = Hunger(food: 0);
        Hours(w, rel, 4);
        Assert.Equal(farm, war.Ps[0].Targets[0]);
        Assert.Equal(Motive.Food, war.WhyTarget(hn, farm));
        Assert.True(war.Shortage(w, hn).Food > 0.5f);

        (w, war, hn, baseP, farm, rel) = Hunger(food: 50);       // ten days' food at 1 a day: fed
        Hours(w, rel, 4);
        Assert.Equal(0, war.Shortage(w, hn).Food);
        Assert.Equal(baseP, war.Ps[0].Targets[0]);
        Assert.NotEqual(Motive.Food, war.WhyTarget(hn, farm));
    }

    [Fact]
    public void Without_a_motive_a_place_is_passed_over_and_without_motives_it_is_not()
    {
        foreach (bool motives in (bool[])[true, false])
        {
            var w = new World();
            int hn = w.AddFaction("hn", "Holy Nation", false, false);
            int sk = w.AddFaction("sk", "Shek", false, false);
            int home = w.AddPlace("h", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
            int lost = w.AddPlace("l", "Holy Ruins", new Vector2(15000, 0), PlaceKind.Territory, 1, hn);   // the Holy Nation's from the start
            int other = w.AddPlace("o", "Shek Ruins", new Vector2(-15000, 0), PlaceKind.Territory, 1, sk);  // Shek land, strongly held
            var war = new Warfare(Cfg(motives ? new MotiveConfig() : null));
            w.AddModule(war);
            w.StartNew();
            w.Places[lost].Owner = sk;
            var guard = w.FindBand(w.SpawnBand(sk, other, BandRole.Garrison, w.Places[other].Pos, "test"))!;
            guard.Size = guard.SizeMax = 50;
            var g2 = w.FindBand(w.SpawnBand(sk, lost, BandRole.Garrison, w.Places[lost].Pos, "test"))!;
            g2.Size = g2.SizeMax = 50;
            Hours(w, new Relations((hn, sk)), 4);

            var targets = war.Ps[home].Targets;
            Assert.Contains(lost, targets);
            if (motives)
            {
                Assert.DoesNotContain(other, targets);
                Assert.Equal(Motive.Grievance, war.WhyTarget(hn, lost));
                Assert.True(war.Fired.NoMotive > 0);
            }
            else
            {
                Assert.Contains(other, targets);
                Assert.Equal(0, war.Fired.NoMotive);
            }
        }
    }

    [Fact]
    public void A_place_taken_back_says_so()
    {
        // the Shek's own farm, held by the Holy Nation with nobody in it: a Shek band besieges it, it falls, the Shek walk in
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        int squin = w.AddPlace("q", "Shek Camp", new Vector2(-12000, 0), PlaceKind.Territory, 0, sk);   // not a base: plans no targets
        int farm = w.AddPlace("f", "Shek Farm", Vector2.Zero, PlaceKind.Resource, 2, sk);
        var war = new Warfare(Cfg(new MotiveConfig()));
        w.AddModule(war);
        w.StartNew();
        w.Places[farm].Owner = hn;
        var rel = new Relations((hn, sk));
        var band = w.FindBand(w.SpawnBand(sk, squin, BandRole.Invader, w.Places[squin].Pos, "warfare", BandTier.Veteran))!;
        band.Leader[Axis.Patience] = 1;
        w.Send(band, farm, BandRole.Invader);
        Hours(w, rel, 30);

        Assert.Equal(0, war.Fired.PickedFor.Sum());           // nobody planned it: the taking itself finds the reason
        Assert.Equal(sk, w.Places[farm].Owner);
        Assert.Equal(Motive.Grievance, war.Ps[farm].TakenFor);
        Assert.Contains(w.Events, e => e.Type == EventType.Capture && e.Key == "hold_grievance" && e.Place == farm);
        Assert.True(war.Fired.TookFor[(int)Motive.Grievance] > 0);
        Assert.Equal("Shek are back in Shek Farm.", w.Say(w.Events.Last(e => e.Key == "hold_grievance")));
    }

    [Fact]
    public void Every_motive_has_both_sentences()
    {
        var text = new Text();
        foreach (var m in Enum.GetValues<Motive>().Where(m => m != Motive.None))
            foreach (string k in (string[])["took_", "hold_"])
                Assert.NotEqual(k + m.ToString().ToLowerInvariant(), text.Get(k + m.ToString().ToLowerInvariant()));
    }
}
