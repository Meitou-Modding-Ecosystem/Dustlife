// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// On code-mod API 1 and 2 (Meitou 0.22.1 and older) a hold also stops a loaded squad's members until the mod walks them, so a squad held
/// by Dustlife stood still once it loaded near the player (the 2026-11-21 runs: four squads near Stack for 26 days, never in a fight). Dustlife
/// releases it while loaded and holds it again once it is a stand-in. Since API 3 a hold is off screen only, and nothing is needed.
/// </summary>
public sealed class SquadLoadedTests
{
    sealed class Host(bool holdStopsLoaded) : ISquadHost
    {
        public readonly Dictionary<int, (bool Dead, int Size, Vector2 Pos)> Squads = [];
        public readonly HashSet<int> LoadedNow = [], Held = [];
        public readonly List<(int Squad, string Tag)> Tags = [];
        public readonly List<string> Calls = [];
        int _next = 100;

        public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
        {
            Squads[_next] = (false, 8, at);
            Held.Add(_next);
            Tags.Add((_next, tag));
            return _next++;
        }

        public void Send(int squad, Vector2 goal) => Calls.Add($"send {squad}");

        public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => Squads.TryGetValue(squad, out var s) ? s : null;

        public IEnumerable<(int Squad, string Tag)> Tagged() => Tags;

        public bool Loaded(int squad) => LoadedNow.Contains(squad);

        public bool HoldStopsLoaded => holdStopsLoaded;

        public void SetHeld(int squad, bool held)
        {
            Calls.Add($"{(held ? "hold" : "release")} {squad}");
            if (held) Held.Add(squad);
            else Held.Remove(squad);
        }
    }

    static readonly Vector2 Near = new(60000, 5000);

    static (World W, Band Band) Marching()
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
        return (w, band);
    }

    [Fact]
    public void On_API_2_a_loaded_squad_is_let_loose_and_held_again_once_unloaded()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(holdStopsLoaded: true);
        squads.Sync(w, host, [Near]);
        int id = squads.SquadOf[band.Id];
        Assert.Contains(id, host.Held);

        host.LoadedNow.Add(id);
        squads.Sync(w, host, [Near]);
        Assert.DoesNotContain(id, host.Held);                                             // its members' own minds run: they can fight
        squads.Sync(w, host, [Near]);
        Assert.Equal(1, squads.ReleasedLoaded);                                           // once, not every sync

        host.LoadedNow.Clear();
        host.Calls.Clear();
        squads.Sync(w, host, [Near]);
        Assert.Contains(id, host.Held);
        Assert.Equal([$"hold {id}", $"send {id}"], host.Calls);                           // held and sent on to the band's goal
        Assert.Equal(1, squads.HeldAgain);
        Assert.Contains("1 let loose while loaded, 1 held again", squads.Summary(null));
    }

    [Fact]
    public void On_API_3_the_hold_is_left_alone()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(holdStopsLoaded: false);
        squads.Sync(w, host, [Near]);
        int id = squads.SquadOf[band.Id];
        host.LoadedNow.Add(id);
        squads.Sync(w, host, [Near]);
        Assert.Contains(id, host.Held);
        Assert.Equal(0, squads.ReleasedLoaded);
    }

    [Fact]
    public void After_a_load_the_squad_is_held_and_sent_again()
    {
        var (w, band) = Marching();
        var host = new Host(holdStopsLoaded: false);
        new Squads(Lore.Squads()).Sync(w, host, [Near]);
        int id = host.Tags.Single().Squad;
        host.Held.Clear();                                                                // an unnamed hold isn't saved

        var loaded = new Squads(Lore.Squads());
        loaded.Sync(w, host, [Near]);
        Assert.Equal(1, loaded.Relinked);
        Assert.Contains(id, host.Held);
        Assert.Contains($"send {id}", host.Calls);
    }
}
