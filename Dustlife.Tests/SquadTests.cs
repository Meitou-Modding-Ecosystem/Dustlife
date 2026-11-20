// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// The hand-over to the game's squads (port step 5c), with a fake game: who is handed over, the squad leads the band, a beaten squad ends
/// the band, and a load finds the squads again by their tags.
/// </summary>
public sealed class SquadTests
{
    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int a, int b) => hostile.Any(h => (h.A == a && h.B == b) || (h.A == b && h.B == a));

        public bool HostileToPlayer(int faction) => false;
    }

    /// <summary>The game, faked: squads that the test moves, hurts and removes.</summary>
    sealed class FakeGame : ISquadHost
    {
        public readonly Dictionary<int, (string Tag, string Template, bool Dead, int Size, Vector2 Pos, Vector2 Goal)> Squads = [];
        int _next = 100;

        public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
        {
            Squads[_next] = (tag, template, false, 8, at, goal);
            return _next++;
        }

        public void Send(int squad, Vector2 goal) => Squads[squad] = Squads[squad] with { Goal = goal };

        public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => Squads.TryGetValue(squad, out var s) ? (s.Dead, s.Size, s.Pos) : null;

        public IEnumerable<(int Squad, string Tag)> Tagged() => Squads.Select(x => (x.Key, x.Value.Tag));
    }

    static (World W, Band Band, Relations Rel) Marching()
    {
        var w = new World();
        int hn = w.AddFaction(Lore.HolyNation, "Holy Nation", false, false);
        int sk = w.AddFaction(Lore.Shek, "Shek", false, false);
        int stack = w.AddPlace("stack", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        w.AddPlace("squin", "Squin", new Vector2(80000, 0), PlaceKind.Base, 3, sk);
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false }));
        w.StartNew();
        var band = w.FindBand(w.SpawnBand(sk, 1, BandRole.Invader, new Vector2(60000, 0), "warfare"))!;
        w.Send(band, stack, BandRole.Invader);
        return (w, band, new Relations((hn, sk)));
    }

    [Fact]
    public void A_marching_band_near_the_player_becomes_a_real_squad_of_its_factions_war_template()
    {
        var (w, band, _) = Marching();
        var game = new FakeGame();
        var squads = new Squads(Lore.Squads());

        squads.Sync(w, game, [new Vector2(0, 30000)]);                 // far away: nothing
        Assert.Empty(game.Squads);
        squads.Sync(w, game, [new Vector2(60000, 1000)]);              // right on top of it, in view: not here either
        Assert.Empty(game.Squads);
        squads.Sync(w, game, [new Vector2(60000, 5000)]);              // near, out of view: handed over

        var s = Assert.Single(game.Squads).Value;
        Assert.Equal("dustlife:" + band.Id, s.Tag);
        Assert.Equal("64238-Dialogue.mod", s.Template);                // Shek base attack medium
        Assert.Equal(band.TargetPos, s.Goal);                          // the band's own goal, by its target
        Assert.True(Vector2.Distance(s.Goal, w.Places[0].Pos) < 1500);
        Assert.True(band.External);
        Assert.Equal(1, squads.HandedOver);
    }

    [Fact]
    public void The_squad_leads_the_band_and_a_beaten_squad_ends_it()
    {
        var (w, band, rel) = Marching();
        var game = new FakeGame();
        var squads = new Squads(Lore.Squads());
        squads.Sync(w, game, [new Vector2(60000, 5000)]);
        int id = squads.SquadOf[band.Id];

        var before = band.Pos;
        for (int i = 0; i < 8; i++) w.Tick(0.25f, rel);                // the core does not move it
        Assert.Equal(before, band.Pos);

        game.Squads[id] = game.Squads[id] with { Pos = new Vector2(30000, 0), Size = 5 };   // the game walked it and it lost men
        squads.Sync(w, game, []);
        Assert.Equal(new Vector2(30000, 0), band.Pos);
        Assert.Equal(5, band.Size);

        game.Squads[id] = game.Squads[id] with { Pos = new Vector2(1200, 0) };            // at its goal: it arrives, as any band does
        squads.Sync(w, game, []);
        w.Tick(0.25f, rel);
        Assert.NotEqual(BandState.Moving, band.State);

        game.Squads[id] = game.Squads[id] with { Dead = true };                            // beaten in the game
        squads.Sync(w, game, []);
        Assert.False(band.Alive);
        Assert.Equal(1, squads.LostWithSquad);
    }

    [Fact]
    public void After_a_load_squads_are_found_by_tag_and_a_missing_one_was_beaten()
    {
        var (w, band, _) = Marching();
        var other = w.FindBand(w.SpawnBand(1, 1, BandRole.Invader, new Vector2(60000, 0), "warfare"))!;
        other.External = true;                                         // handed over before the save; its squad did not survive
        band.External = true;
        var game = new FakeGame();
        game.Squads[555] = ("dustlife:" + band.Id, "x", false, 6, new Vector2(40000, 0), Vector2.Zero);   // new id after the load

        var squads = new Squads(Lore.Squads());
        squads.Sync(w, game, []);

        Assert.Equal(555, squads.SquadOf[band.Id]);
        Assert.Equal(new Vector2(40000, 0), band.Pos);
        Assert.False(other.Alive);                                     // never re-placed: a killed crew must not come back
        Assert.Equal(1, squads.Relinked);
    }

    [Fact]
    public void No_more_than_the_cap_and_one_a_sync_and_no_garrisons_or_untemplated_factions()
    {
        var (w, _, _) = Marching();
        for (int i = 0; i < 6; i++)
        {
            var b = w.FindBand(w.SpawnBand(1, 1, BandRole.Invader, new Vector2(60000 + i * 100, 0), "warfare"))!;
            w.Send(b, 0, BandRole.Invader);
        }

        var g = w.FindBand(w.SpawnBand(1, 1, BandRole.Garrison, new Vector2(60500, 0), "warfare"))!;
        w.Send(g, 1, BandRole.Garrison);
        var game = new FakeGame();
        var squads = new Squads(Lore.Squads());
        for (int i = 0; i < 10; i++) squads.Sync(w, game, [new Vector2(60000, 5000)]);

        Assert.Equal(4, game.Squads.Count);
        Assert.False(g.External);
        Assert.Equal(-1, Squads.BandOf("dustlife:x"));
        Assert.Equal(-1, Squads.BandOf("twr:12"));
    }
}
