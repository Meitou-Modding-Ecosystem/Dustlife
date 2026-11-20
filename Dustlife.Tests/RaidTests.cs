// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// Raid parties on a raid front (the Hub theatre's second try): small parties from the nearest place that keeps a band home, at the
/// weakest enemy place on the front, paced; never a base where the front says so.
/// </summary>
public sealed class RaidTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    static (World W, Warfare War, int Hn, int Sk, Relations Rel) Front(int atCamp, float weakBelow = 17)
    {
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        w.AddPlace("squin", "Squin", new Vector2(-80000, 0), PlaceKind.Base, 3, sk);                  // far from the front
        int camp = w.AddPlace("camp", "Shek Camp", new Vector2(-30000, 0), PlaceKind.Territory, 1, sk);
        w.AddPlace("mine", "Holy Mine", new Vector2(-20000, 0), PlaceKind.Resource, 2, hn);
        w.AddPlace("farm", "Holy Farm", new Vector2(-18000, 6000), PlaceKind.Resource, 2, hn);
        w.AddPlace("stack", "Stack", new Vector2(-15000, -2000), PlaceKind.Base, 3, hn);             // on the front, but a base
        var front = new Front
        {
            Center = new Vector2(-22000, 0), Radius = 15000, Enemies = ["hn"], WeakBelow = weakBelow, EveryHours = 24, NoBases = true, Raids = true,
            PartySize = 2,
        };
        var war = new Warfare(new WarfareConfig
        {
            SeedGarrisons = false, ClaimsHold = true, ExpansionAggression = 0,
            Doctrines = [new Doctrine { Faction = "sk", Enemies = ["hn"], Fronts = [front] }],
        });
        w.AddModule(war);
        w.StartNew();
        for (int i = 0; i < atCamp; i++) w.SpawnBand(sk, camp, BandRole.Garrison, w.Places[camp].Pos, "warfare");
        var guard = w.FindBand(w.SpawnBand(hn, w.FindPlace("farm"), BandRole.Garrison, w.Places[w.FindPlace("farm")].Pos, "warfare"))!;
        _ = guard;                                                       // the farm is held by one band; the mine by none: the weakest
        return (w, war, hn, sk, new Relations((hn, sk)));
    }

    [Fact]
    public void A_party_of_two_leaves_the_nearest_place_for_the_weakest_enemy_place_and_one_band_stays_home()
    {
        var (w, war, _, sk, rel) = Front(atCamp: 3);
        w.Tick(0.25f, rel);

        int mine = w.FindPlace("mine"), camp = w.FindPlace("camp");
        Assert.Equal(1, war.Fired.RaidParties);
        Assert.Equal(2, w.Bands.Count(b => b.Faction == sk && b.Role == BandRole.Invader && b.Target == mine));
        Assert.Equal(1, w.CountSettled(camp, sk));
        var e = w.Events.Last(x => x.Key == "raid");
        Assert.StartsWith("Shek raid Holy Mine from Shek Camp: 2 bands under ", w.Say(e));

        for (int i = 0; i < 8; i++) w.Tick(0.25f, rel);                  // paced: not even a try within the 24 h
        Assert.Equal(1, war.Fired.RaidParties + war.Fired.RaidNoParty + war.Fired.RaidNoTarget);
    }

    [Fact]
    public void No_party_when_no_place_can_spare_one_and_no_target_when_the_front_has_only_bases_or_strong_places()
    {
        var (w, war, _, _, rel) = Front(atCamp: 2);                      // two at the camp: a party of two would leave nobody home
        w.Tick(0.25f, rel);
        Assert.Equal(0, war.Fired.RaidParties);
        Assert.Equal(1, war.Fired.RaidNoParty);

        var (w2, war2, hn, _, rel2) = Front(atCamp: 3, weakBelow: 0.01f);   // every enemy place with anyone in it is too strong
        foreach (var p in w2.Places.Where(p => p.Owner == hn && p.Kind == PlaceKind.Resource))
            w2.SpawnBand(hn, p.Id, BandRole.Garrison, p.Pos, "warfare");
        w2.Tick(0.25f, rel2);
        Assert.Equal(0, war2.Fired.RaidParties);
        Assert.Equal(1, war2.Fired.RaidNoTarget);                        // and Stack, held by nobody, is a base: never raided
        Assert.DoesNotContain(w2.Bands, b => b.Target == w2.FindPlace("stack"));
    }

    [Fact]
    public void Without_a_party_size_the_front_sends_no_parties()
    {
        var (w, war, _, _, rel) = Front(atCamp: 3);
        war.Cfg.Doctrines[0].Fronts[0].PartySize = 0;
        for (int i = 0; i < 8; i++) w.Tick(0.25f, rel);
        Assert.Equal(0, war.Fired.RaidParties + war.Fired.RaidNoParty + war.Fired.RaidNoTarget);
    }
}
