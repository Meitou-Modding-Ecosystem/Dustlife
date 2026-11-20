// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Dustlife.Tests;

/// <summary>
/// The game layer (port step 5): the war built from the game's towns, the game's relations, town changes followed; and, with a Kenshi
/// install, the war built from the real map.
/// </summary>
public sealed class GameTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    static readonly MapFaction[] Factions =
    [
        new("hn", "Holy Nation", false, false), new("sk", "Shek", false, false), new("beak", "Beak Things", true, false),
    ];

    [Fact]
    public void Towns_become_places_by_the_reference_rules()
    {
        var w = new Core.World();
        MapTown[] towns =
        [
            new("a", "Stack", Vector2.Zero, 2, "hn"), new("b", "Holy Farm", new Vector2(1, 0), 3, "hn"), new("c", "Ruins", new Vector2(2, 0), 4, null),
            new("d", "Nest", new Vector2(3, 0), 0, "beak"), new("e", "Empty Nest", new Vector2(4, 0), 0, null),
            new("f", "Camp", new Vector2(5, 0), 0, "sk"), new("g", "Drin", new Vector2(6, 0), 4, "sk"), new("h", "Hideout", new Vector2(7, 0), 2, "sk", Locked: true),
        ];
        int[] placeOf = GameMap.Build(w, Factions, towns);

        Assert.Equal(PlaceKind.Base, w.Places[placeOf[0]].Kind);
        Assert.Equal(PlaceKind.Resource, w.Places[placeOf[1]].Kind);
        Assert.Equal(-1, w.Places[placeOf[2]].Owner);
        Assert.Equal(PlaceKind.Lair, w.Places[placeOf[3]].Kind);             // an animal nest stays a nest
        Assert.Equal(-1, placeOf[4]);                                         // a nest held by nobody is left out
        Assert.Equal(PlaceKind.Territory, w.Places[placeOf[5]].Kind);        // a nest held by people is territory
        Assert.Equal(PlaceKind.Base, w.Places[placeOf[6]].Kind);             // Drin is the UC's base in Bast
        Assert.True(w.Places[placeOf[7]].Locked);
        Assert.True(w.Factions[2].Fauna);
    }

    [Fact]
    public void Relations_go_through_the_games_indices()
    {
        // Dustlife's 0, 1, 2 are the game's 7, 3 and none
        var rel = new GameRelations([7, 3, -1], (a, b) => (a, b) is (7, 3) or (3, 7), f => f == 3);
        Assert.True(rel.Hostile(0, 1));
        Assert.False(rel.Hostile(0, 2));
        Assert.False(rel.Hostile(5, 1));
        Assert.True(rel.HostileToPlayer(1));
        Assert.False(rel.HostileToPlayer(2));
    }

    static (Core.World W, TownWatch Watch, Relations Rel) Watched()
    {
        var w = new Core.World();
        GameMap.Build(w, Factions,
        [
            new("stack", "Stack", Vector2.Zero, 2, "hn"), new("farm", "Holy Farm", new Vector2(20000, 0), 3, "hn"),
            new("squin", "Squin", new Vector2(60000, 0), 2, "sk"),
        ]);
        w.AddModule(new Warfare(new WarfareConfig()));
        w.StartNew();
        var watch = new TownWatch();
        watch.Prime([("stack", new TownVersion("", "hn")), ("farm", new TownVersion("", "hn")), ("squin", new TownVersion("", "sk"))]);
        return (w, watch, new Relations((0, 1)));
    }

    [Fact]
    public void A_town_the_game_changes_changes_hands_and_its_old_garrison_leaves()
    {
        var (w, watch, rel) = Watched();
        int stack = w.FindPlace("stack");
        Assert.Contains(w.Bands, b => b.Faction == 0 && w.Settled(b, stack));

        // Esata dies: the game puts Stack's override version in, held by the Shek
        var changed = watch.Poll(w, [("stack", new TownVersion("stack_shek", "sk")), ("farm", new TownVersion("", "hn")), ("squin", new TownVersion("", "sk"))], rel);

        Assert.Equal([stack], changed);
        Assert.Equal(1, w.Places[stack].Owner);
        Assert.DoesNotContain(w.Bands, b => b.Faction == 0 && w.Settled(b, stack));
        Assert.Contains(w.Bands, b => b.Faction == 0 && b.Target == w.FindPlace("farm"));   // gone to the nearest place still theirs
        var e = w.Events.Last(x => x.Key == "town_changed");
        Assert.Equal("Stack has changed: Shek hold it now, not Holy Nation.", w.Say(e));
        Assert.Equal(1, watch.Switches);

        Assert.Empty(watch.Poll(w, [("stack", new TownVersion("stack_shek", "sk"))], rel));   // seen: nothing more
        watch.Poll(w, [("stack", new TownVersion("", "hn"))], rel);                           // and back again is a change too
        Assert.Equal(0, w.Places[stack].Owner);
        Assert.Equal(2, watch.Switches);
    }

    [Fact]
    public void A_town_given_to_a_faction_outside_the_war_is_emptied_and_a_band_with_nowhere_to_go_breaks_up()
    {
        var (w, watch, rel) = Watched();
        int squin = w.FindPlace("squin");
        watch.Poll(w, [("squin", new TownVersion("squin_ruined", "someone_else"))], rel);

        Assert.Equal(-1, w.Places[squin].Owner);
        Assert.DoesNotContain(w.Bands, b => b.Alive && b.Faction == 1);   // the Shek's only place: their garrison breaks up
        Assert.Contains(w.Events, x => x.Key == "town_emptied" && x.Place == squin);
    }

    [Fact]
    public void Priming_remembers_without_acting()
    {
        var (w, watch, rel) = Watched();
        watch.Prime([("stack", new TownVersion("stack_shek", "sk"))]);
        Assert.Empty(watch.Poll(w, [("stack", new TownVersion("stack_shek", "sk"))], rel));
        Assert.Equal(0, w.Places[w.FindPlace("stack")].Owner);
    }

    [Fact]
    public void On_the_real_map_the_war_builds_and_runs()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "no Kenshi install");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var levels = WorldLevelData.LoadBaseGame(install!);
        var data = PopulationData.Create(db, levels.Towns(), roads: levels.Roads());
        var (war, rel) = GameSource.Build(data, new DustlifeSettings());

        foreach (string f in (string[])[Lore.HolyNation, Lore.UnitedCities, Lore.Shek, Lore.SlaveTraders])
            Assert.True(war.FindFaction(f) >= 0, f);
        foreach (string n in (string[])["Stack", "Squin", "Admag", "Okran's Shield", "Blister Hill", "Drin"])
            Assert.Contains(war.Places, p => p.Name == n);
        Assert.True(war.Places.Count > 200);
        Assert.True(war.Roads!.All.Count > 550);                           // Logistics Core finds 618 edges between 226 ownable places
        // the base map places only nest markers; the game makes its real nests (camps, animal nests) at run time, out of a mod's reach on
        // the 0.20 engine: so no animal lairs yet, and no nest held by nobody
        Assert.DoesNotContain(war.Places, p => p.Kind == PlaceKind.Lair && p.Owner < 0);
        Assert.True(rel.Hostile(war.FindFaction(Lore.HolyNation), war.FindFaction(Lore.Shek)));

        // every war squad the lore names is a squad template of the game
        foreach (var (inv, pat) in Lore.Squads().Templates.Values)
            foreach (string id in (string[])[inv, pat])
                Assert.Equal(Meitou.Data.Fcs.FcsRecordType.SquadTemplate, db.Find(id)?.Type);

        war.StartNew();
        for (int h = 1; h <= 24 * 20; h++) war.Advance(h, rel);
        Assert.Contains(war.Events, e => e.Type is EventType.Invasion or EventType.Battle);
        Assert.Equal(0, war.Repairs);

        // a town the game changes reaches the war
        var watch = new TownWatch();
        watch.Prime(GameSource.Versions(data));
        Assert.Empty(watch.Poll(war, GameSource.Versions(data), rel));
    }
}
