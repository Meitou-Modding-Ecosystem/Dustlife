// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// A handed-over squad goes back to Dustlife once it has walked far from the player's people or reached its goal unseen, where the game can
/// take a squad back (Meitou's <c>DespawnPlatoon</c>, VlcekM/MeitouClient#1173). The 2026-11-21 headless runs: four squads handed over by day 4
/// stayed "in the game now" for the next 26 days, so the cap of four stayed full and nothing else was handed over all month.
/// </summary>
public sealed class SquadGiveBackTests
{
    sealed class Host(bool canGiveBack, bool refuse = false) : ISquadHost
    {
        public readonly Dictionary<int, (bool Dead, int Size, Vector2 Pos)> Squads = [];
        public readonly HashSet<int> LoadedNow = [];
        public int GiveBacks;
        int _next = 100;

        public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
        {
            Squads[_next] = (false, 8, at);
            return _next++;
        }

        public void Send(int squad, Vector2 goal) { }

        public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => Squads.TryGetValue(squad, out var s) ? s : null;

        public IEnumerable<(int Squad, string Tag)> Tagged() => [];

        public bool CanGiveBack => canGiveBack;

        public bool GiveBack(int squad)
        {
            if (!canGiveBack || refuse) return false;
            GiveBacks++;
            return Squads.Remove(squad);
        }

        public bool Loaded(int squad) => LoadedNow.Contains(squad);

        public void Move(Vector2 to)
        {
            foreach (int id in Squads.Keys.ToList()) Squads[id] = Squads[id] with { Pos = to };
        }
    }

    sealed class Gate : IHandOverGate
    {
        public int Gone;
        public bool MayHandOver(World w, Band band) => true;
        public void InGame(World w, Band band, bool near) { }
        void IHandOverGate.Gone(Band band) => Gone++;
    }

    static readonly Vector2 Stack = Vector2.Zero, Near = new(60000, 5000), Far = new(-90000, 0);

    static (World W, Band Band) Marching()
    {
        var w = new World();
        int hn = w.AddFaction(Lore.HolyNation, "Holy Nation", false, false);
        int sk = w.AddFaction(Lore.Shek, "Shek", false, false);
        int stack = w.AddPlace("stack", "Stack", Stack, PlaceKind.Base, 3, hn);
        w.AddPlace("squin", "Squin", new Vector2(80000, 0), PlaceKind.Base, 3, sk);
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false }));
        w.StartNew();
        var band = w.FindBand(w.SpawnBand(sk, 1, BandRole.Invader, new Vector2(60000, 0), "warfare"))!;
        w.Send(band, stack, BandRole.Invader);
        return (w, band);
    }

    [Fact]
    public void Without_a_despawn_the_squad_stays_the_games_as_before()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(canGiveBack: false);
        squads.Sync(w, host, [Near]);
        for (int i = 0; i < 20; i++) squads.Sync(w, host, [Far]);
        Assert.True(band.External);
        Assert.Single(squads.SquadOf);
        Assert.Contains("none given back (this Meitou can't take a squad back)", squads.Summary(null));
    }

    [Fact]
    public void A_squad_far_from_the_player_goes_back_to_Dustlife_after_a_few_syncs()
    {
        var (w, band) = Marching();
        var gate = new Gate();
        var squads = new Squads(Lore.Squads()) { Gate = gate };
        var host = new Host(canGiveBack: true);
        squads.Sync(w, host, [Near]);
        Assert.True(band.External);
        host.Move(new Vector2(50000, 0));

        for (int i = 0; i < squads.Cfg.FarSyncs - 1; i++) squads.Sync(w, host, [Far]);
        Assert.True(band.External);                                                       // not yet: it must stay far a while

        squads.Sync(w, host, [Far]);
        Assert.False(band.External);
        Assert.True(band.Alive);
        Assert.Equal(new Vector2(50000, 0), band.Pos);                                    // it walks on from where its squad was
        Assert.Empty(host.Squads);
        Assert.Empty(squads.SquadOf);
        Assert.Equal(1, squads.GivenBackFar);
        Assert.Equal(1, gate.Gone);                                                       // Rivalry Core's claim is released
        Assert.Contains("1 given back (1 far from the player, 0 at their goal), 0 refused", squads.Summary(null));
    }

    [Fact]
    public void Coming_near_again_resets_the_count()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(canGiveBack: true);
        squads.Sync(w, host, [Near]);
        for (int i = 0; i < 10; i++)
        {
            squads.Sync(w, host, [Far]);
            squads.Sync(w, host, [Far]);
            squads.Sync(w, host, [Near]);
        }

        Assert.True(band.External);
        Assert.Equal(0, squads.GivenBackFar);
    }

    [Fact]
    public void A_squad_at_its_goal_goes_back_only_while_nobody_can_see_it()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(canGiveBack: true);
        squads.Sync(w, host, [Near]);
        int id = squads.SquadOf[band.Id];
        host.Move(band.TargetPos);
        host.LoadedNow.Add(id);
        squads.Sync(w, host, [new Vector2(3000, 0)]);                                     // at Stack, in view: kept
        Assert.True(band.External);

        host.LoadedNow.Clear();
        squads.Sync(w, host, [new Vector2(3000, 0)]);                                     // a stand-in now: back to Dustlife
        Assert.False(band.External);
        Assert.Equal(1, squads.GivenBackAtGoal);
    }

    [Fact]
    public void A_refused_give_back_is_counted_and_the_squad_kept()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(canGiveBack: true, refuse: true);
        squads.Sync(w, host, [Near]);
        for (int i = 0; i < squads.Cfg.FarSyncs + 2; i++) squads.Sync(w, host, [Far]);
        Assert.True(band.External);
        Assert.Single(squads.SquadOf);
        Assert.Equal(3, squads.GiveBackRefused);
    }

    [Fact]
    public void A_band_given_back_is_not_handed_over_again_at_once()
    {
        var (w, band) = Marching();
        var squads = new Squads(Lore.Squads());
        var host = new Host(canGiveBack: true);
        squads.Sync(w, host, [Near]);
        for (int i = 0; i < squads.Cfg.FarSyncs; i++) squads.Sync(w, host, [Far]);
        Assert.False(band.External);

        band.Pos = new Vector2(60000, 0);
        squads.Sync(w, host, [Near]);                                                     // in the ring again, but cooling down
        Assert.False(band.External);
        Assert.Equal(1, squads.HandedOver);

        for (int i = 0; i < squads.Cfg.CooldownSyncs; i++) squads.Sync(w, host, [Far]);
        band.Pos = new Vector2(60000, 0);
        squads.Sync(w, host, [Near]);
        Assert.True(band.External);
        Assert.Equal(2, squads.HandedOver);
    }
}
