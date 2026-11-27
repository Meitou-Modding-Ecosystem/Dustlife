// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

// A stand-in for Rivalry Core's public surface (rivalry.core 0.6.6: IRivalryApi, IRivalryState, HostileCandidate, PartyClaim, PartyKind),
// with the same full names, so RivalryLink finds it by reflection as it finds the real one (RivalryContractTests checks the names against the
// locked release). It keeps the real contract: Propose queues and answers false until an assessment (Assess) offers the best candidate the
// slot, then true (and confirms it); one hostile party at a time; no new hostile claim during the rest; a renewal always holds; a confirmed
// claim that ends starts the rest, an unconfirmed offer doesn't.
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
        public const double RestHours = 12;
        public readonly List<string> Calls = [];
        public bool Runs = true;
        public double Hours;
        readonly Dictionary<string, (double Until, bool Offered)> _held = [];
        readonly Dictionary<string, HostileCandidate> _queue = [];

        public IRivalryState? In(object world) => Runs ? this : null;

        public double HostileRestUntil { get; private set; }

        public IReadOnlyCollection<string> Held => _held.Keys;

        public int PartiesOut(PartyKind kind) => kind == PartyKind.Hostile ? _held.Count(h => h.Value.Until > Hours) : 0;

        public bool ClaimParty(PartyClaim claim)
        {
            Calls.Add($"claim {claim.Key} {claim.Kind} {claim.Hours} {claim.Faction}");
            string id = claim.Source + "|" + claim.Key;
            if (_held.TryGetValue(id, out var h) && h.Until > Hours)
            {
                _held[id] = (Hours + claim.Hours, false);
                return true;
            }
            if (Hours < HostileRestUntil || PartiesOut(PartyKind.Hostile) >= 1) return false;

            _held[id] = (Hours + claim.Hours, false);
            return true;
        }

        public bool ReleaseParty(string source, string key)
        {
            Calls.Add($"release {source} {key}");
            if (!_held.Remove(source + "|" + key, out var h)) return false;

            if (!h.Offered && PartiesOut(PartyKind.Hostile) == 0) HostileRestUntil = Hours + RestHours;
            return true;
        }

        public bool Propose(HostileCandidate candidate)
        {
            Calls.Add($"propose {candidate.Source} {candidate.Key} f{candidate.Faction} grudge {candidate.Grudge} travel {candidate.TravelHours}");
            string id = candidate.Source + "|" + candidate.Key;
            var claim = new PartyClaim(candidate.Source, PartyKind.Hostile, candidate.Key, candidate.ClaimHours, candidate.Faction);
            if (_held.ContainsKey(id)) return ClaimParty(claim);

            _queue[id] = candidate;
            return false;
        }

        /// <summary>An assessment: the best waiting candidate is offered the slot for 2 h when it is free and nobody rests.</summary>
        public void Assess()
        {
            if (Hours >= HostileRestUntil && PartiesOut(PartyKind.Hostile) == 0 && _queue.Values.MaxBy(c => c.Grudge) is { } best)
                _held[best.Source + "|" + best.Key] = (Hours + 2, true);
            _queue.Clear();
        }
    }
}

namespace Dustlife.Tests
{
    /// <summary>
    /// Dustlife under Rivalry Core's limits: a war band hostile to the player becomes a real squad near them only once Rivalry Core's queue
    /// lets it go; its claim is renewed while near, released when it marches away or is gone, and claimed again when it comes back or after a
    /// load. A win Dustlife won't use is given back. A band not hostile to the player passes, also while a hostile one waits.
    /// </summary>
    public sealed class RivalryLinkTests
    {
        sealed class Host : ISquadHost
        {
            public readonly Dictionary<int, (bool Dead, int Size, Vector2 Pos)> Squads = [];
            public readonly Dictionary<int, string> Tags = [];
            public bool Refuse;
            int _next = 100;

            public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
            {
                if (Refuse) return -1;

                Squads[_next] = (false, 8, at);
                Tags[_next] = tag;
                return _next++;
            }

            public void Send(int squad, Vector2 goal) { }

            public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => Squads.TryGetValue(squad, out var s) ? s : null;

            public IEnumerable<(int Squad, string Tag)> Tagged() => Tags.Select(t => (t.Key, t.Value));
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

        static readonly Vector2 Near = new(60000, 5000), Far = new(-90000, 0), TooNear = new(60000, 1000);

        sealed record Setup(World W, Band Band, Rivalry.FakeRivalry Rivalry, RivalryLink Link, Squads Squads, Host Host)
        {
            public void Sync(Vector2 player) => Squads.Sync(W, Host, [player]);

            /// <summary>Proposes the band, lets an assessment offer it the slot, and hands it over.</summary>
            public void HandOver()
            {
                Sync(Near);
                Rivalry.Assess();
                Sync(Near);
            }
        }

        static Setup Make(Func<int, bool>? hostile = null)
        {
            var (w, band) = Marching();
            var rivalry = new Rivalry.FakeRivalry();
            var link = new RivalryLink(rivalry, f => f + 10, hostile ?? (_ => true));
            var squads = new Squads(Lore.Squads()) { Gate = link };
            Assert.True(link.Bind(new object()));
            return new Setup(w, band, rivalry, link, squads, new Host());
        }

        static string Key(Band b) => $"pza.dustlife.war|band:{b.Id}";

        [Fact]
        public void A_hostile_band_waits_for_Rivalry_Cores_queue_then_holds_a_claim_while_near()
        {
            var t = Make();

            t.Sync(Near);                                                                     // the queue says wait
            Assert.Empty(t.Host.Squads);
            Assert.Equal(1, t.Squads.HeldByGate);
            Assert.Equal($"propose pza.dustlife.war band:{t.Band.Id} f11 grudge 60 travel 0", t.Rivalry.Calls[0]);

            t.Rivalry.Assess();
            t.Sync(Near);                                                                     // its turn: it goes
            Assert.Single(t.Host.Squads);
            Assert.Equal(1, t.Link.Holding);

            t.Sync(Near);                                                                     // still near: the claim is renewed
            Assert.Equal(1, t.Link.Renewed);

            t.Sync(Far);                                                                      // the player is far now: released
            Assert.Contains(t.Rivalry.Calls, c => c == $"release pza.dustlife.war band:{t.Band.Id}");
            Assert.Equal(0, t.Link.Holding);
        }

        [Fact]
        public void A_beaten_squad_releases_its_claim()
        {
            var t = Make();
            t.HandOver();

            t.Host.Squads[t.Host.Squads.Keys.Single()] = (true, 0, Near);                     // the game's squad is beaten
            t.Sync(Near);

            Assert.Empty(t.Rivalry.Held);
            Assert.False(t.Band.Alive);
        }

        [Fact]
        public void A_win_for_a_band_no_longer_in_the_ring_is_given_back_without_a_rest()
        {
            var t = Make();
            t.Sync(Near);
            t.Rivalry.Assess();                                                               // offered the slot...

            t.Sync(TooNear);                                                                  // ...but it is too near to hand over now

            Assert.Empty(t.Rivalry.Held);
            Assert.Equal(1, t.Link.Dropped);
            Assert.Equal(0, t.Rivalry.HostileRestUntil);
        }

        [Fact]
        public void A_refused_spawn_gives_its_win_back()
        {
            var t = Make();
            t.Host.Refuse = true;

            t.HandOver();

            Assert.Equal(1, t.Squads.SpawnRefused);
            Assert.Empty(t.Rivalry.Held);
            Assert.Equal(0, t.Link.Holding);
        }

        [Fact]
        public void After_a_load_a_hostile_squad_near_the_player_renews_the_saved_claim()
        {
            var t = Make();
            t.HandOver();

            // a load: Dustlife's list of claims is new, Rivalry Core's claim was saved
            var link = new RivalryLink(t.Rivalry, f => f + 10, _ => true);
            var squads = new Squads(Lore.Squads()) { Gate = link };
            Assert.True(link.Bind(new object()));
            squads.Sync(t.W, t.Host, [Near]);

            Assert.Equal(1, squads.Relinked);
            Assert.Equal(1, link.Holding);
            Assert.Equal(1, link.Reclaimed);
        }

        [Fact]
        public void A_squad_back_near_after_marching_away_claims_again_once_the_rest_is_over()
        {
            var t = Make();
            t.HandOver();
            t.Sync(Far);                                                                      // released: the rest starts

            t.Sync(Near);
            Assert.Equal(0, t.Link.Holding);                                                  // no new hostile claim while resting

            t.Rivalry.Hours = Rivalry.FakeRivalry.RestHours + 1;
            t.Sync(Near);
            Assert.Equal(1, t.Link.Holding);
            Assert.Equal(1, t.Link.Reclaimed);
        }

        [Fact]
        public void A_hostile_band_waiting_its_turn_does_not_keep_a_band_that_needs_nothing_from_coming()
        {
            int shek = -1;
            var t = Make(f => f == shek);
            shek = t.Band.Faction;
            int hn = 1 - shek;
            var other = t.W.FindBand(t.W.SpawnBand(hn, 1, BandRole.Invader, new Vector2(62000, 0), "warfare"))!;
            t.W.Send(other, t.W.FindPlace("squin"), BandRole.Invader);

            t.Sync(Near);

            Assert.Equal(1, t.Squads.HeldByGate);                                             // the nearer hostile band waits
            Assert.True(other.External);                                                      // the farther one goes
        }

        [Fact]
        public void A_band_not_hostile_to_the_player_passes_by_without_asking()
        {
            var t = Make(_ => false);
            t.Sync(Near);
            Assert.Single(t.Host.Squads);
            Assert.Empty(t.Rivalry.Calls);
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
            var t = Make();
            t.Sync(Far);                                                                      // nothing in the ring
            t.Rivalry.Hours = 10;
            t.Rivalry.ClaimParty(new Rivalry.PartyClaim("twr.bounty", Rivalry.PartyKind.Hostile, "Me", 6));
            t.Rivalry.ReleaseParty("twr.bounty", "Me");                                       // rests until 22
            Assert.True(t.Link.Bind(new object()));
            t.Sync(Near);

            string s = t.Link.Summary(12);
            Assert.Contains("linked (queue)", s);
            Assert.Contains("resting 10 h more", s);
            Assert.Contains("Dustlife proposed 1, 0 went, 1 waited", s);
        }
    }
}
