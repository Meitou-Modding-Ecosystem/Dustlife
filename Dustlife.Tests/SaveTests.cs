// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// The world's save (port step 5): a loaded world goes on exactly as the one that never stopped; a changed world still loads; a damaged
/// save is refused before anything changes.
/// </summary>
public sealed class SaveTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    static readonly Relations Rel = new((0, 1), (0, 2), (1, 2), (3, 0), (3, 1), (4, 0), (4, 1), (4, 2));

    /// <summary>
    /// A small war with every module: the Holy Nation, the Shek, the slavers, a bandit faction and an animal one; bases, farms, ruins, a
    /// nest, roads between them. <paramref name="skip"/> leaves one place out (a world changed since the save).
    /// </summary>
    static World Build(ulong seed, string? skip = null)
    {
        var w = new World();
        w.Rng.State = seed;
        w.AddFaction("hn", "Holy Nation", false, false);
        w.AddFaction("sk", "Shek", false, false);
        w.AddFaction("st", "Slave Traders", false, true);
        w.AddFaction("db", "Dust Bandits", false, false);
        w.AddFaction("beak", "Beak Things", true, false);
        (string Key, string Name, float X, float Z, PlaceKind Kind, int Res, int Owner)[] places =
        [
            ("stack", "Stack", 0, 0, PlaceKind.Base, 3, 0), ("farm", "Holy Farm", 20000, 5000, PlaceKind.Resource, 2, 0),
            ("squin", "Squin", 60000, 0, PlaceKind.Base, 3, 1), ("ruins", "Shek Ruins", 40000, 15000, PlaceKind.Territory, 2, 1),
            ("slave", "Slave Farm", 30000, -30000, PlaceKind.Resource, 2, 2), ("camp", "Bandit Camp", 30000, 30000, PlaceKind.Territory, 1, 3),
            ("nest", "Nest", 10000, 40000, PlaceKind.Lair, 0, 4), ("tower", "Fallen Tower", 50000, -15000, PlaceKind.Territory, 1, -1),
        ];
        foreach (var p in places)
            if (p.Key != skip) w.AddPlace(p.Key, p.Name, new Vector2(p.X, p.Z), p.Kind, p.Res, p.Owner);
        w.Roads = new Roads();
        for (int i = 0; i < w.Places.Count; i++)
            for (int k = i + 1; k < w.Places.Count; k++)
                if (Vector2.Distance(w.Places[i].Pos, w.Places[k].Pos) < 40000)
                    w.Roads.Add(i, k, Vector2.Distance(w.Places[i].Pos, w.Places[k].Pos) * 1.2f);
        w.AddModule(new Economy(new EconomyConfig { CutRoads = true, Profiles = [new EconomyProfile { Faction = "st", PoolOf = "sk" }] }));
        w.AddModule(new Slavery());
        w.AddModule(new Fauna());
        w.AddModule(new Warfare(new WarfareConfig { UseEconomy = true, ClaimsHold = true, MusterPatienceHours = 24, Motives = new MotiveConfig() }));
        return w;
    }

    static void Days(World w, double from, int days)
    {
        for (int h = 1; h <= days * 24; h++) w.Advance(from + h, Rel);
    }

    static byte[] SaveOf(World w)
    {
        using var ms = new MemoryStream();
        WorldSave.Write(w, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Everything that moves, as one string: the clock, the streams, the holders, every band, the news since <paramref name="since"/>.
    /// </summary>
    static string State(World w, long since = 0)
    {
        var parts = new List<string> { $"{w.Now} {w.Rng.State} {w.NameRng.State}" };
        parts.AddRange(w.Places.Select(p => $"{p.Key}:{p.Owner}"));
        parts.AddRange(w.Bands.Select(b => $"{b.Id} {b.Faction} {b.Size} {b.State} {b.Role} {b.Home} {b.Target} {b.Pos.X:0.###} {b.Pos.Y:0.###} {b.Leader.Name} {b.Morale:0.####}"));
        parts.AddRange(w.Events.Where(e => e.Seq > since).Select(e => $"{e.Seq} {e.Key} {string.Join('|', e.Args)}"));
        return string.Join('\n', parts);
    }

    [Fact]
    public void A_loaded_world_goes_on_exactly_as_the_one_that_never_stopped()
    {
        foreach (ulong seed in (ulong[])[1, 7, 42])
        {
            var a = Build(seed);
            a.StartNew();
            Days(a, 0, 12);
            Assert.True(a.Events.Count(e => e.Type is EventType.Battle or EventType.Invasion or EventType.Capture) > 0);   // a war, not a quiet world
            byte[] save = SaveOf(a);

            var b = Build(seed + 1000);                         // another seed: the save must bring its own streams
            var map = WorldSave.Read(b, new MemoryStream(save));
            Assert.Equal(0, map.MissingPlaces + map.MissingFactions);
            long seq = a.Events[^1].Seq;
            Assert.Equal(State(a, seq), State(b, seq));

            Days(a, 12 * 24, 20);
            Days(b, 12 * 24, 20);
            Assert.Equal(State(a, seq), State(b, seq));
            Assert.Equal(SaveOf(a), SaveOf(b));                 // and the modules' state with it
        }
    }

    [Fact]
    public void A_save_still_loads_into_a_world_that_lost_a_place()
    {
        var a = Build(3);
        a.StartNew();
        Days(a, 0, 6);
        var b = Build(3, skip: "farm");
        var map = WorldSave.Read(b, new MemoryStream(SaveOf(a)));

        Assert.Equal(1, map.MissingPlaces);
        Assert.Equal(0, map.MissingFactions);
        Assert.DoesNotContain(b.Bands, x => x.Home >= b.Places.Count || x.Target >= b.Places.Count);
        Assert.All(b.Places, p => Assert.Equal(a.Places[a.FindPlace(p.Key)].Owner, p.Owner));
        Days(b, 6 * 24, 3);                                    // and runs on
    }

    [Fact]
    public void A_damaged_save_is_refused_and_changes_nothing()
    {
        var a = Build(5);
        a.StartNew();
        Days(a, 0, 4);
        byte[] good = SaveOf(a);
        var rng = new Random(1234);
        int refused = 0, loaded = 0;
        for (int i = 0; i < 400; i++)
        {
            byte[] bad = (byte[])good.Clone();
            if (i % 2 == 0) bad = bad[..rng.Next(0, bad.Length)];                           // cut short
            else for (int k = 0; k < 1 + i % 5; k++) bad[rng.Next(bad.Length)] ^= (byte)(1 << rng.Next(8));   // bits flipped

            var b = Build(5);
            b.StartNew();
            string before = State(b);
            try
            {
                WorldSave.Read(b, new MemoryStream(bad));
                loaded++;                                       // a flip that still reads: it must run
                Days(b, b.Now, 1);
            }
            catch (InvalidDataException)
            {
                refused++;
                Assert.Equal(before, State(b));
            }
        }

        Assert.True(refused > 200);
        Assert.True(loaded > 0);
    }
}
