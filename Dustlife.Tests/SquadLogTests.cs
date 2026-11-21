// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// The hand-over says why it didn't fire (the 2026-11-21 headless run: four Shek armies marched on Stack with the player there, and no
/// log line could tell whether a squad was ever handed over, or why not).
/// </summary>
public sealed class SquadLogTests
{
    sealed class Host(bool refuse) : ISquadHost
    {
        public int Spawned;

        public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag) => refuse ? -1 : 100 + Spawned++;

        public void Send(int squad, Vector2 goal) { }

        public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => (false, 8, Vector2.Zero);

        public IEnumerable<(int Squad, string Tag)> Tagged() => [];
    }

    static World Marching(bool march = true)
    {
        var w = new World();
        int hn = w.AddFaction(Lore.HolyNation, "Holy Nation", false, false);
        int sk = w.AddFaction(Lore.Shek, "Shek", false, false);
        int stack = w.AddPlace("stack", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        w.AddPlace("squin", "Squin", new Vector2(80000, 0), PlaceKind.Base, 3, sk);
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false }));
        w.StartNew();
        var band = w.FindBand(w.SpawnBand(sk, 1, BandRole.Invader, new Vector2(60000, 0), "warfare"))!;
        if (march) w.Send(band, stack, BandRole.Invader);
        return w;
    }

    [Fact]
    public void Each_sync_counts_why_nothing_was_handed_over()
    {
        var squads = new Squads(Lore.Squads());
        var host = new Host(refuse: false);

        squads.Sync(Marching(), host, []);                                    // none of the player's people
        squads.Sync(Marching(), host, [new Vector2(0, 30000)]);               // the band is 67 km away
        Assert.Equal(1, squads.NoPlayers);
        Assert.Equal(1, squads.OutOfRing);
        Assert.InRange(squads.Nearest, 60000, 70000);

        squads.Sync(Marching(march: false), host, [new Vector2(60000, 5000)]); // nobody marching
        Assert.Equal(1, squads.NoMarching);
        Assert.Equal(3, squads.Syncs);
        Assert.Equal(0, squads.HandedOver);
        Assert.Contains("1 with none 2500 to 7000 away (nearest ", squads.Summary(null));
    }

    [Fact]
    public void A_refused_spawn_is_counted_and_its_reason_shown()
    {
        var squads = new Squads(Lore.Squads());
        squads.Sync(Marching(), new Host(refuse: true), [new Vector2(60000, 5000)]);
        Assert.Equal(1, squads.SpawnRefused);
        Assert.Equal(0, squads.HandedOver);
        Assert.Contains("1 spawns refused (last: no game town for place 0)", squads.Summary("no game town for place 0"));
    }

    [Fact]
    public void The_cap_is_counted()
    {
        var squads = new Squads(new SquadConfig { MaxSquads = 0, Templates = Lore.Squads().Templates });
        squads.Sync(Marching(), new Host(refuse: false), [new Vector2(60000, 5000)]);
        Assert.Equal(1, squads.AtCap);
    }
}
