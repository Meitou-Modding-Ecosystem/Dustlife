// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

// A stand-in for Rivalry Core's public surface (rivalry.core 0.6.3: IRivalryApi, IRivalryState, HostileCandidate, PartyClaim, PartyKind),
// with the same full names, so RivalryLink finds it by reflection as it finds the real one.
namespace Rivalry
{
    public enum PartyKind { None, Hostile, Friendly }

    public sealed record HostileCandidate(string Source, string Key, int Faction, float Grudge, double TravelHours, double ClaimHours = 6);

    public sealed record PartyClaim(string Source, PartyKind Kind, string Key, double Hours, int Faction = -1);

    public interface IRivalryState
    {
        double HostileRestUntil { get; }
        int PartiesOut(PartyKind kind);
        bool ClaimParty(PartyClaim claim);
        bool ReleaseParty(string source, string key);
        bool Propose(HostileCandidate candidate);
    }

    public interface IRivalryApi
    {
        IRivalryState? In(object world);
    }

    public sealed class FakeRivalry : IRivalryApi, IRivalryState
    {
        public bool Admit;
        public readonly List<string> Calls = [];
        public bool Runs = true;

        public IRivalryState? In(object world) => Runs ? this : null;

        public double HostileRestUntil => 30;

        public int PartiesOut(PartyKind kind) => Calls.Count(c => c.StartsWith("claim", StringComparison.Ordinal)) > 0 ? 1 : 0;

        public bool ClaimParty(PartyClaim claim)
        {
            Calls.Add($"claim {claim.Key} {claim.Kind} {claim.Hours} {claim.Faction}");
            return true;
        }

        public bool ReleaseParty(string source, string key)
        {
            Calls.Add($"release {source} {key}");
            return true;
        }

        public bool Propose(HostileCandidate candidate)
        {
            Calls.Add($"propose {candidate.Source} {candidate.Key} f{candidate.Faction} travel {candidate.TravelHours}");
            return Admit;
        }
    }
}

namespace Dustlife.Tests
{
    /// <summary>
    /// Dustlife under Rivalry Core's limits: a war band hostile to the player becomes a real squad near them only once Rivalry Core's queue
    /// lets it go; its claim is renewed while near and released when it marches away or is gone. A band not hostile to the player passes.
    /// </summary>
    public sealed class RivalryLinkTests
    {
        sealed class Host : ISquadHost
        {
            public readonly Dictionary<int, (bool Dead, int Size, Vector2 Pos)> Squads = [];
            int _next = 100;

            public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
            {
                Squads[_next] = (false, 8, at);
                return _next++;
            }

            public void Send(int squad, Vector2 goal) { }

            public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => Squads.TryGetValue(squad, out var s) ? s : null;

            public IEnumerable<(int Squad, string Tag)> Tagged() => [];
        }

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

        static readonly Vector2 Near = new(60000, 5000);

        [Fact]
        public void A_hostile_band_waits_for_Rivalry_Cores_queue_then_holds_a_claim_while_near()
        {
            var (w, band) = Marching();
            var rivalry = new Rivalry.FakeRivalry();
            var link = new RivalryLink(rivalry, f => f + 10, _ => true);
            var squads = new Squads(Lore.Squads()) { Gate = link };
            var host = new Host();
            Assert.True(link.Bind(new object()));

            squads.Sync(w, host, [Near]);                                                     // the queue says wait
            Assert.Empty(host.Squads);
            Assert.Equal(1, squads.HeldByGate);
            Assert.Equal($"propose pza.dustlife.war band:{band.Id} f11 travel 0", rivalry.Calls[0]);

            rivalry.Admit = true;
            squads.Sync(w, host, [Near]);                                                     // its turn: it goes
            Assert.Single(host.Squads);
            Assert.Equal(1, link.Holding);

            squads.Sync(w, host, [Near]);                                                     // still near: the claim is renewed
            Assert.Contains(rivalry.Calls, c => c == $"claim band:{band.Id} Hostile 6 11");

            squads.Sync(w, host, [new Vector2(-90000, 0)]);                                   // the player is far now: released
            Assert.Contains(rivalry.Calls, c => c == $"release pza.dustlife.war band:{band.Id}");
            Assert.Equal(0, link.Holding);
        }

        [Fact]
        public void A_beaten_squad_releases_its_claim()
        {
            var (w, band) = Marching();
            var rivalry = new Rivalry.FakeRivalry { Admit = true };
            var link = new RivalryLink(rivalry, f => f, _ => true);
            var squads = new Squads(Lore.Squads()) { Gate = link };
            var host = new Host();
            link.Bind(new object());
            squads.Sync(w, host, [Near]);
            host.Squads[host.Squads.Keys.Single()] = (true, 0, Near);                         // the game's squad is beaten
            squads.Sync(w, host, [Near]);
            Assert.Contains(rivalry.Calls, c => c == $"release pza.dustlife.war band:{band.Id}");
            Assert.False(band.Alive);
        }

        [Fact]
        public void A_band_not_hostile_to_the_player_passes_by_without_asking()
        {
            var (w, _) = Marching();
            var rivalry = new Rivalry.FakeRivalry();
            var link = new RivalryLink(rivalry, f => f, _ => false);
            var squads = new Squads(Lore.Squads()) { Gate = link };
            var host = new Host();
            link.Bind(new object());
            squads.Sync(w, host, [Near]);
            Assert.Single(host.Squads);
            Assert.Empty(rivalry.Calls);
        }

        [Fact]
        public void Without_Rivalry_Core_or_its_world_nothing_is_asked()
        {
            var none = new RivalryLink(null, f => f, _ => true);
            Assert.False(none.Bind(new object()));
            Assert.StartsWith("Rivalry Core: not loaded", none.Summary(0));

            var idle = new RivalryLink(new Rivalry.FakeRivalry { Runs = false }, f => f, _ => true);
            Assert.False(idle.Bind(new object()));                                            // loaded, but this world doesn't run it

            var notApi = new RivalryLink(new object(), f => f, _ => true);
            Assert.False(notApi.Bind(new object()));
            Assert.Equal("Rivalry Core: loaded, but its API was not found", notApi.Summary(0));
        }

        [Fact]
        public void The_summary_says_what_happened()
        {
            var (w, _) = Marching();
            var rivalry = new Rivalry.FakeRivalry();
            var link = new RivalryLink(rivalry, f => f, _ => true);
            var squads = new Squads(Lore.Squads()) { Gate = link };
            link.Bind(new object());
            squads.Sync(w, new Host(), [Near]);
            string s = link.Summary(10);
            Assert.Contains("linked (queue)", s);
            Assert.Contains("resting 20 h more", s);
            Assert.Contains("Dustlife proposed 1, 0 went, 1 waited", s);
        }
    }
}
