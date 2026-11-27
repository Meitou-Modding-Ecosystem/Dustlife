// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Globalization;
using System.Numerics;

namespace Dustlife.Core;

/// <summary>
/// The Dustlife core, ported from the reference (`dl::World`): places, factions, bands, time, movement and combat. Modules (Warfare and the
/// rest) run on top through <see cref="IModule"/>. No game code here; the game feeds it places and relations, and reads its bands.
/// Not yet here (later port steps): saving, the game bridge (real squads near the player) and the presence grid.
/// </summary>
public sealed class World
{
    static readonly int[] KindCap = [4, 2, 1, 2];              // Warfare's max_population analogue, by kind
    static readonly float[] BaseSkill = [0.8f, 1.2f, 1.7f];
    static readonly string[] Epithets = ["ep_scarred", "ep_one_armed", "ep_iron_hide", "ep_limping", "ep_half_face", "ep_unbroken", "ep_grim"];
    static readonly string[] NameA = ["Ka", "Ro", "Sha", "Gri", "Mo", "Tal", "Ve", "Dru", "Ash", "Ke", "Ba", "Zo", "Hi", "Ul", "Re", "No"];
    static readonly string[] NameB = ["rik", "do", "van", "t", "sa", "mak", "len", "gor", "ro", "shi", "ta", "bel", "nus", "ka"];
    static readonly string[] NameC = ["", "", "", "a", "o", "en", "us", "ai"];

    readonly List<IModule> _modules = [];
    int _worldBases = 1, _worldResources = 1;

    public CoreConfig Cfg { get; } = new();
    public List<Place> Places { get; } = [];
    public List<Faction> Factions { get; } = [];
    public List<Band> Bands { get; } = [];
    /// <summary>Recent events, newest last (the last 300 at most).</summary>
    public List<WorldEvent> Events { get; } = [];
    /// <summary>The war's history: every event of importance 2 or more, oldest first (the last 400 at most).</summary>
    public List<WorldEvent> Chronicle { get; } = [];
    /// <summary>Warlords who rode out of their band's rout (<see cref="CoreConfig.WarlordsSurvive"/>).</summary>
    public long WarlordEscapes;
    /// <summary>Game hours.</summary>
    public double Now { get; private set; }
    public double LastTick { get; private set; } = -1;
    public long TickCount { get; private set; }
    public Rng Rng { get; } = new();
    /// <summary>A second stream for leaders' names, so names don't shift every other roll.</summary>
    public Rng NameRng { get; } = new() { State = 0x2545F4914F6CDD1DUL };
    /// <summary>The player's characters, set by the game each tick.</summary>
    public List<Vector2> PlayerPos { get; } = [];
    /// <summary>The sentences for event text and names.</summary>
    public Text Text { get; set; } = new();
    /// <summary>Bad values the core had to repair (a position that is not a number, an unknown owner).</summary>
    public long Repairs { get; private set; }
    public bool Started { get; private set; }
    /// <summary>The road graph (Logistics Core's edges by place index), or null: distances are then straight, as in the reference.</summary>
    public Roads? Roads { get; set; }

    /// <summary>
    /// How far apart two places are for planning: along the roads when there are any (a pair no road joins counts as twice the straight
    /// line), else straight.
    /// </summary>
    public float TravelDist(int a, int b)
    {
        if (PlaceById(a) is not { } pa || PlaceById(b) is not { } pb) return float.PositiveInfinity;
        float straight = Vector2.Distance(pa.Pos, pb.Pos);
        if (Roads == null) return straight;
        float d = Roads.Distance(a, b);
        return float.IsPositiveInfinity(d) ? straight * 2 : d;
    }

    int _nextBandId = 1, _nextLeaderId = 1;
    long _nextEventSeq = 1;

    public IReadOnlyList<IModule> Modules => _modules;

    /// <summary>The bases on the map (at least 1), as of the last tick.</summary>
    public int WorldBases => _worldBases;

    /// <summary>The resource points on the map (at least 1), as of the last tick.</summary>
    public int WorldResources => _worldResources;

    public void AddModule(IModule module) => _modules.Add(module);

    public IModule? GetModule(string name) => _modules.Find(m => m.Name == name);

    // ------------------------------------------------------------------ building

    /// <summary>
    /// Adds a faction (or returns the one with this key).
    /// </summary>
    public int AddFaction(string key, string name, bool fauna, bool slaver)
    {
        int found = FindFaction(key);
        if (found >= 0) return found;
        var f = new Faction
        {
            Id = Factions.Count, Key = key, Name = name, Fauna = fauna, Slaver = slaver,
            Aggression = fauna ? 0.8f : 0.35f + 0.4f * Rng.Uniform(),
        };
        Factions.Add(f);
        return f.Id;
    }

    /// <summary>
    /// Adds a place (or returns the one with this key). Bad input never gets in: an unknown owner is nobody, a position that is not a
    /// number is the origin (counted in <see cref="Repairs"/>), resource points are clamped to 0..100.
    /// </summary>
    public int AddPlace(string key, string name, Vector2 pos, PlaceKind kind, int resource, int owner, bool playerOwned = false, bool real = true)
    {
        int found = FindPlace(key);
        if (found >= 0) return found;
        if (owner < 0 || owner >= Factions.Count) owner = -1;
        if (!Finite(pos.X) || !Finite(pos.Y)) { pos = Vector2.Zero; Repairs++; }
        if (!Enum.IsDefined(kind)) kind = PlaceKind.Territory;
        var p = new Place
        {
            Id = Places.Count, Key = key, Name = name, Pos = pos, Kind = kind, Resource = Math.Clamp(resource, 0, 100),
            Owner = owner, OrigOwner = owner, PlayerOwned = playerOwned, Real = real, Cap = KindCap[(int)kind],
        };
        Places.Add(p);
        return p.Id;
    }

    public int FindFaction(string key) => Factions.FindIndex(f => f.Key == key);

    public int FindPlace(string key) => Places.FindIndex(p => p.Key == key);

    public Place? PlaceById(int id) => id >= 0 && id < Places.Count ? Places[id] : null;

    public Band? FindBand(int id) => Bands.Find(b => b.Id == id);

    /// <summary>
    /// No save: every module seeds the world.
    /// </summary>
    public void StartNew()
    {
        UpdateStats();
        foreach (var m in _modules) m.OnNewWorld(this);
        Started = true;
        Emit(EventType.Info, -1, -1, -1, Vector2.Zero, 0, "started", Num(Factions.Count), Num(Places.Count), Num(Bands.Count));
    }

    // ------------------------------------------------------------------ services

    public static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static bool Finite(float v) => !float.IsNaN(v) && v < 1e18f && v > -1e18f;

    static bool Finite(double v) => !double.IsNaN(v) && v < 1e18 && v > -1e18;

    /// <summary>
    /// The leader's name with its epithet, in the current language.
    /// </summary>
    public string FullName(Leader l) => l.Epithet.Length == 0 ? l.Name : l.Name + " " + Text.Get(l.Epithet);

    /// <summary>
    /// An event's text in the current language.
    /// </summary>
    public string Say(WorldEvent e) => Text.Format(e.Key, e.Args);

    public void Emit(EventType type, int a, int b, int place, Vector2 pos, int importance, string key, params string[] args)
    {
        var e = new WorldEvent(_nextEventSeq++, Now, type, a, b, place, pos, importance, key, args);
        Events.Add(e);
        if (Events.Count > 300) Events.RemoveRange(0, 100);
        if (importance < 2) return;
        Chronicle.Add(e);
        if (Chronicle.Count > 400) Chronicle.RemoveRange(0, 100);
    }

    /// <summary>
    /// Marks the band dead and counts the loss once.
    /// </summary>
    public void Kill(Band b)
    {
        if (b.State == BandState.Dead) return;
        b.Size = 0;
        b.State = BandState.Dead;
        if (b.Faction >= 0 && b.Faction < Factions.Count) Factions[b.Faction].Lost++;
    }

    /// <summary>
    /// Whether two factions fight: never a faction itself; fauna fight people but not each other; else all-out war or the game's relations.
    /// </summary>
    public bool Hostile(int a, int b, IRelations rel)
    {
        if (a < 0 || b < 0 || a == b || a >= Factions.Count || b >= Factions.Count) return false;
        bool fa = Factions[a].Fauna, fb = Factions[b].Fauna;
        if (fa && fb) return false;
        if (fa || fb) return true;
        return Cfg.AllOutWar || rel.Hostile(a, b);
    }

    /// <summary>
    /// A band sleeps while its home place is frozen.
    /// </summary>
    public bool Asleep(Band b) => PlaceById(b.Home) is { Active: false };

    public bool Settled(Band b, int placeId) =>
        PlaceById(placeId) is { } p && b.State == BandState.Settled && Vector2.DistanceSquared(b.Pos, p.Pos) < Cfg.PlaceRadius * Cfg.PlaceRadius;

    /// <summary>
    /// The places some living band is besieging now.
    /// </summary>
    public HashSet<int> BesiegedPlaces() => [.. Bands.Where(b => b.Alive && b.State == BandState.Siege && b.Target >= 0).Select(b => b.Target)];

    public int CountSettled(int placeId, int faction) => Bands.Count(b => b.Alive && b.Faction == faction && Settled(b, placeId));

    public int CountHeadingTo(int placeId, int faction) =>
        Bands.Count(b => b.Alive && b.Faction == faction && b.Target == placeId && b.State != BandState.Retreat);

    public float SettledPower(int placeId, int faction)
    {
        float p = 0;
        foreach (var b in Bands) if (b.Alive && b.Faction == faction && Settled(b, placeId)) p += b.Power;
        return p;
    }

    public int NearestPlace(Vector2 at)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < Places.Count; i++)
        {
            float d = Vector2.DistanceSquared(at, Places[i].Pos);
            if (d < bd) (bd, best) = (d, i);
        }

        return best;
    }

    public int NearestOwnedPlace(Vector2 at, int faction, int exclude = -1)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int i = 0; i < Places.Count; i++)
        {
            if (Places[i].Owner != faction || i == exclude) continue;
            float d = Vector2.DistanceSquared(at, Places[i].Pos);
            if (d < bd) (bd, best) = (d, i);
        }

        return best;
    }

    public float NearestPlayerDist(Vector2 at)
    {
        float best = float.MaxValue;
        foreach (var p in PlayerPos)
            if (Finite(p.X) && Finite(p.Y)) best = MathF.Min(best, Vector2.Distance(at, p));
        return best;
    }

    /// <summary>
    /// "at X" / "near X" / "in the wastes", in the current language.
    /// </summary>
    public string Where(Vector2 at)
    {
        int n = NearestPlace(at);
        if (n < 0) return Text.Get("where_wastes");
        float d = Vector2.Distance(at, Places[n].Pos);
        if (d < Cfg.PlaceRadius * 1.5f) return Text.Format("where_at", [Places[n].Name]);
        if (d < Cfg.PlaceRadius * 8.0f) return Text.Format("where_near", [Places[n].Name]);
        return Text.Get("where_wastes");
    }

    /// <summary>
    /// A new leader for a band of <paramref name="faction"/>: a name from the name stream, axes from the main one, leaning on the culture.
    /// </summary>
    public Leader MakeLeader(int faction)
    {
        string name = NameA[NameRng.Int(0, 15)] + NameB[NameRng.Int(0, 13)] + NameC[NameRng.Int(0, 7)];
        var l = new Leader { Id = _nextLeaderId++, Name = char.ToUpperInvariant(name[0]) + name[1..] };
        for (int i = 0; i < Leader.AxisCount; i++) l.Axes[i] = Rng.Range(0.05f, 0.95f);
        if (faction >= 0 && faction < Factions.Count)
        {
            l[Axis.Aggression] = Math.Clamp(0.5f * l[Axis.Aggression] + 0.5f * Factions[faction].Aggression + Rng.Range(-0.1f, 0.1f), 0, 1);
            if (Factions[faction].Slaver) l[Axis.Greed] = Math.Clamp(l[Axis.Greed] + 0.2f, 0, 1);
        }

        return l;
    }

    /// <summary>
    /// Warfare's quality curves: advanced is a parabola peaking at half the world's resources, veteran a line reaching 100% at all of
    /// them. A Kenshi faction rarely holds half the world, so its share is stretched three times.
    /// </summary>
    public BandTier RollQuality(int faction)
    {
        float x = Math.Clamp((float)Factions[faction].Resources / _worldResources * 3.0f, 0, 1);
        float adv = 1.0f - (x - 0.5f) / 0.5f * ((x - 0.5f) / 0.5f);
        float vet = 2.0f * x - 1.0f;
        float u = Rng.Uniform();
        return u <= vet ? BandTier.Veteran : u <= adv ? BandTier.Advanced : BandTier.Novice;
    }

    /// <summary>
    /// A new band of <paramref name="faction"/> at <paramref name="at"/>, settled, homed at <paramref name="placeId"/>. Refused (-1) at the
    /// band cap, for an unknown faction, or at a position that is not a number.
    /// </summary>
    public int SpawnBand(int faction, int placeId, BandRole role, Vector2 at, string ownerModule, BandTier? quality = null)
    {
        if (Bands.Count >= Cfg.MaxBands || faction < 0 || faction >= Factions.Count) return -1;
        if (!Finite(at.X) || !Finite(at.Y)) { Repairs++; return -1; }
        var place = PlaceById(placeId);
        var f = Factions[faction];
        var b = new Band { Id = _nextBandId++, Faction = faction, Quality = quality ?? (f.Fauna ? BandTier.Advanced : RollQuality(faction)) };
        int lo = Math.Max(1, Cfg.SizeMin), hi = Math.Min(200, Math.Max(lo, Cfg.SizeMax));
        float mid = 0.5f * (lo + hi);
        float u = Rng.Uniform();
        float sz = b.Quality switch
        {
            BandTier.Novice => lo + (mid - lo) * u,
            BandTier.Advanced => mid - 1 + 3 * u,
            _ => mid + (hi - mid) * u,
        };
        b.Size = b.SizeMax = (int)Math.Clamp(sz + 0.5f, lo, hi);
        b.Skill = Math.Clamp(BaseSkill[(int)b.Quality] + Rng.Range(-0.15f, 0.15f), 0.5f, 3.0f);
        b.Leader = MakeLeader(faction);
        float jx = Rng.Range(-300, 300), jz = Rng.Range(-300, 300);
        b.Pos = new Vector2(at.X + jx, at.Y + jz);
        b.Role = role;
        b.Home = placeId;
        b.Target = placeId;
        b.TargetPos = place?.Pos ?? at;
        b.StayUntil = Now + Rng.Range(1.0f, 4.0f);
        b.Born = Now;
        b.Owner = ownerModule;
        Bands.Add(b);
        f.Bands++;
        return b.Id;
    }

    /// <summary>
    /// Sends a band to a place (a spot within 400 units of it).
    /// </summary>
    public void Send(Band b, int placeId, BandRole role)
    {
        if (PlaceById(placeId) is not { } p || !Finite(p.Pos.X) || !Finite(p.Pos.Y)) return;
        b.Target = placeId;
        b.Role = role;
        float jx = Rng.Range(-400, 400), jz = Rng.Range(-400, 400);
        b.TargetPos = new Vector2(p.Pos.X + jx, p.Pos.Y + jz);
        b.State = BandState.Moving;
    }

    /// <summary>
    /// Sends a band to a free position.
    /// </summary>
    public void SendTo(Band b, Vector2 pos, BandRole role)
    {
        if (!Finite(pos.X) || !Finite(pos.Y)) { Repairs++; return; }
        b.Target = -1;
        b.Role = role;
        b.TargetPos = pos;
        b.State = BandState.Moving;
    }

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Catches the world up to the game clock, at most <paramref name="maxSteps"/> ticks per call (the rest follows on later calls, so a
    /// long absence never stalls a frame); never more than a month behind; after a long absence in coarse steps. Returns the ticks run.
    /// </summary>
    public int Advance(double gameHoursNow, IRelations rel, int maxSteps = 48)
    {
        if (!Finite(gameHoursNow)) return 0;
        if (LastTick < 0 || gameHoursNow < LastTick) { LastTick = gameHoursNow; Now = gameHoursNow; return 0; }
        float tick = Finite(Cfg.TickHours) && Cfg.TickHours > 0.01f ? Math.Min(Cfg.TickHours, 6.0f) : 0.01f;
        maxSteps = Math.Clamp(maxSteps, 1, 48);
        if (gameHoursNow - LastTick > 24 * 30) LastTick = gameHoursNow - 24 * 30;
        int steps = 0;
        while (gameHoursNow - LastTick >= tick && steps < maxSteps)
        {
            double remaining = gameHoursNow - LastTick;
            float dt = Math.Min(remaining > tick * 48 ? (float)(remaining / 48.0) : tick, 6.0f);
            Now = LastTick;
            Tick(dt, rel);
            LastTick += dt;
            steps++;
        }

        Now = LastTick;
        return steps;
    }

    /// <summary>
    /// One step of <paramref name="dt"/> game hours (ignored when not a positive number; capped at 6).
    /// </summary>
    public void Tick(float dt, IRelations rel)
    {
        if (!Finite(dt) || dt <= 0) return;
        dt = Math.Min(dt, 6.0f);
        Now += dt;
        TickCount++;
        UpdateStats();
        UpdateTiers();
        foreach (var m in _modules) m.Tick(this, dt, rel);
        ThinkAndMove(dt, rel);
        Combat(rel);
        foreach (var m in _modules) m.PostTick(this, dt, rel);
        Bands.RemoveAll(b => !b.Alive);
        if ((TickCount & 3) == 0) UpdateRanks();
    }

    void UpdateStats()
    {
        _worldBases = 0;
        _worldResources = 0;
        foreach (var f in Factions) (f.Resources, f.Bases, f.PlacesHeld, f.Bands, f.Power) = (0, 0, 0, 0, 0);
        foreach (var p in Places)
        {
            if (p.Owner >= Factions.Count) { p.Owner = -1; Repairs++; }
            if (p.Kind == PlaceKind.Base) _worldBases++;
            if (p.Kind != PlaceKind.Lair) _worldResources += p.Resource;
            if (p.Owner < 0) continue;
            var f = Factions[p.Owner];
            f.PlacesHeld++;
            f.Resources += p.Resource;
            if (p.Kind == PlaceKind.Base) f.Bases++;
        }

        _worldResources = Math.Max(1, _worldResources);
        _worldBases = Math.Max(1, _worldBases);
        foreach (var b in Bands)
        {
            if (!b.Alive) continue;
            Factions[b.Faction].Bands++;
            Factions[b.Faction].Power += b.Power;
        }
    }

    void UpdateTiers()
    {
        foreach (var b in Bands)
        {
            if (!Finite(b.Pos.X) || !Finite(b.Pos.Y))
            {
                b.Pos = PlaceById(b.Home)?.Pos ?? Vector2.Zero;
                if (b.State != BandState.Dead) b.State = BandState.Settled;
                Repairs++;
            }

            if (!Finite(b.TargetPos.X) || !Finite(b.TargetPos.Y)) { b.TargetPos = b.Pos; Repairs++; }
            if (!Finite(b.Morale)) { b.Morale = 0.5f; Repairs++; }
            if (!Finite(b.Skill)) { b.Skill = 1.0f; Repairs++; }
            float d = NearestPlayerDist(b.Pos);
            b.Tier = d < Cfg.Tier1Radius ? 1 : d < Cfg.Tier2Radius ? 2 : 3;
        }
    }

    /// <summary>
    /// Each faction's war host follows its most renowned living captain. A challenger must clearly outshine the sitting warlord; when the
    /// warlord is gone, the best captain steps up (the first one needs a name for himself and a host of three bands).
    /// </summary>
    void UpdateRanks()
    {
        int n = Factions.Count;
        var best = new Band?[n];
        var cur = new Band?[n];
        var count = new int[n];
        foreach (var b in Bands)
        {
            if (!b.Alive || Factions[b.Faction].Fauna) continue;
            count[b.Faction]++;
            if (b.Leader.Id > 0 && b.Leader.Id == Factions[b.Faction].Warlord) cur[b.Faction] = b;
            if (best[b.Faction] is not { } bb || b.Leader.Renown > bb.Leader.Renown) best[b.Faction] = b;
        }

        for (int fi = 0; fi < n; fi++)
        {
            var f = Factions[fi];
            if (f.Fauna || !f.Enabled) continue;
            if (cur[fi] is { } sitting)
            {
                f.WarlordName = FullName(sitting.Leader);
                // F1 (WarlordsSurvive): a challenger must outshine the sitting warlord by far more (the early renowns are small, and the old
                // margin let five warlords follow each other in the first days of every run)
                float margin = Cfg.WarlordsSurvive ? sitting.Leader.Renown * 1.5f + 6.0f : sitting.Leader.Renown * 1.25f + 2.0f;
                if (best[fi] is { } challenger && challenger != sitting && challenger.Leader.Renown > margin)
                {
                    f.Warlord = challenger.Leader.Id;
                    f.WarlordName = FullName(challenger.Leader);
                    Emit(EventType.Warlord, fi, -1, -1, challenger.Pos, 2, "warlord_new", f.WarlordName, f.Name);
                }

                continue;
            }

            bool had = f.Warlord > 0;
            if (best[fi] is { } nb && count[fi] >= 3 && (had || nb.Leader.Renown >= 6.0f) && nb.Leader.Id > 0)
            {
                string old = f.WarlordName;
                f.Warlord = nb.Leader.Id;
                f.WarlordName = FullName(nb.Leader);
                if (had && old.Length > 0) Emit(EventType.Warlord, fi, -1, -1, nb.Pos, 2, "warlord_succ", old, f.WarlordName, f.Name);
                else Emit(EventType.Warlord, fi, -1, -1, nb.Pos, 2, "warlord_new", f.WarlordName, f.Name);
            }
        }
    }

    void ThinkAndMove(float dt, IRelations rel)
    {
        // modules may add bands while thinking: walk the bands that existed at the start of the step
        int count = Bands.Count;
        for (int i = 0; i < count; i++)
        {
            var b = Bands[i];
            if (!b.Alive || Asleep(b)) continue;
            if (PlaceById(b.Target) is { } t && (!t.Active || t.Locked)) Reroute(b);

            if (b.State is BandState.Settled or BandState.Siege)
            {
                if (b.State == BandState.Settled) b.Morale = Math.Clamp(b.Morale + 0.03f * dt, 0, 1);
                if (b.Tier == 3 && ((TickCount + b.Id) & 3) != 0) continue;    // far bands think one tick in four (KVS fidelity)
                bool handled = false;
                foreach (var m in _modules)
                {
                    if (handled) break;
                    handled = m.Think(this, b, rel);
                }

                if (!handled && b.State == BandState.Settled && Now >= b.StayUntil && b.Target < 0)
                {
                    // nobody claims this band and it is out in the open: it walks to the nearest place its faction holds
                    int dest = NearestOwnedPlace(b.Pos, b.Faction);
                    if (dest >= 0) Send(b, dest, BandRole.Garrison);
                    else b.StayUntil = Now + 24;
                }

                continue;
            }

            if (b.State is not (BandState.Moving or BandState.Retreat)) continue;
            float step = Cfg.Speed * (b.State == BandState.Retreat ? 1.2f : 1.0f) * dt;
            float d = Vector2.Distance(b.Pos, b.TargetPos);
            if (b.External && !(Finite(d) && d <= Cfg.ExternalArrival)) continue;          // the game walks it
            if (!b.External && Finite(d) && Finite(step) && d > step && d > 50.0f)
            {
                b.Pos += (b.TargetPos - b.Pos) / d * step;
                continue;
            }

            b.Pos = b.TargetPos;
            bool arrived = false;
            if (b.State == BandState.Moving)
            {
                foreach (var m in _modules)
                {
                    if (arrived) break;
                    arrived = m.OnArrive(this, b, rel);
                }
            }

            if (!arrived)
            {
                b.State = BandState.Settled;
                b.StayUntil = Now + Rng.Range(1.0f, 4.0f);
            }
        }
    }

    /// <summary>
    /// A band whose target was frozen or locked goes to the nearest other place its faction holds, or waits where it stands.
    /// </summary>
    void Reroute(Band b)
    {
        int old = b.Target, dest = -1;
        float bd = float.MaxValue;
        foreach (var o in Places)
        {
            if (o.Owner != b.Faction || !o.Active || o.Locked || o.Id == old) continue;
            float d = Vector2.DistanceSquared(b.Pos, o.Pos);
            if (d < bd) (bd, dest) = (d, o.Id);
        }

        if (dest >= 0) { Send(b, dest, b.Role == BandRole.Invader ? BandRole.Garrison : b.Role); return; }
        b.Target = -1;
        b.TargetPos = b.Pos;
        b.State = BandState.Settled;
        b.StayUntil = Now + 24;
    }

    void LoseMembers(Band w, int n, Band? killer)
    {
        if (n <= 0 || !w.Alive) return;
        n = Math.Min(n, w.Size);
        int before = w.Size;
        w.Size -= n;
        w.Morale = Math.Clamp(w.Morale - 1.2f * n / Math.Max(1, w.SizeMax), 0, 1);
        if (killer != null)
        {
            killer.Leader.Kills = Math.Clamp(killer.Leader.Kills + n, 0, 1000000);
            killer.Xp += n * w.Skill;
        }

        if (w.Size <= 0)
        {
            if (Cfg.WarlordsSurvive && w.Leader.Id > 0 && Factions[w.Faction].Warlord == w.Leader.Id && Rng.Chance(0.75f)
                && Bands.Where(o => o != w && o.Alive && o.Faction == w.Faction && !o.External).OrderByDescending(o => o.Power).ThenBy(o => o.Id).FirstOrDefault() is { } refuge)
            {
                // the war host's leader rides out of the rout and takes command of the strongest band left (F1: commanders last)
                refuge.Leader = w.Leader;
                w.Leader = MakeLeader(w.Faction);
                WarlordEscapes++;
                Emit(EventType.Warlord, w.Faction, -1, -1, refuge.Pos, 1, "warlord_escaped", FullName(refuge.Leader), Factions[w.Faction].Name);
            }

            Kill(w);
            if (killer != null)
                Emit(EventType.Wiped, killer.Faction, w.Faction, -1, w.Pos, 1, "wiped_by", FullName(w.Leader), Factions[w.Faction].Name,
                    Factions[killer.Faction].Name, Where(w.Pos));
            else Emit(EventType.Wiped, -1, w.Faction, -1, w.Pos, 1, "wiped", FullName(w.Leader), Factions[w.Faction].Name, Where(w.Pos));
            return;
        }

        // battle scars (KVS): the leader may fall, or come out scarred and harder
        float leaderRisk = 0.5f * n / before / w.Leader.Toughness;
        if (Cfg.WarlordsSurvive && w.Leader.Id > 0 && Factions[w.Faction].Warlord == w.Leader.Id) leaderRisk *= 0.25f;   // he leads from behind the line
        if (Rng.Chance(leaderRisk))
        {
            string old = FullName(w.Leader);
            float grudge = w.Leader.Grudge;
            bool wasWarlord = w.Leader.Id > 0 && Factions[w.Faction].Warlord == w.Leader.Id;
            w.Leader = MakeLeader(w.Faction);
            w.Leader.Grudge = grudge * 0.5f;          // the new leader remembers, a little
            Emit(EventType.LeaderDied, killer?.Faction ?? -1, w.Faction, -1, w.Pos, wasWarlord ? 2 : 1, "leader_fell", old,
                Factions[w.Faction].Name, Where(w.Pos), w.Leader.Name);
            if (wasWarlord)
            {
                // the war host loses heart when its leader falls in battle (band against band only: nothing the player does to one
                // leader changes another)
                foreach (var o in Bands)
                    if (o.Alive && o.Faction == w.Faction) o.Morale = Math.Clamp(o.Morale - 0.15f, 0, 1);
            }
        }
        else if (Rng.Chance(0.25f))
        {
            if (w.Leader.Scars < 1000) w.Leader.Scars++;
            w.Leader.Toughness = Math.Clamp(w.Leader.Toughness + 0.12f, 1.0f, 2.5f);
            int e = Math.Min(w.Leader.Scars - 1, 6);
            if (w.Leader.Epithet.Length == 0 || Rng.Chance(0.4f)) w.Leader.Epithet = Epithets[Rng.Int(0, e)];
            Emit(EventType.Scar, w.Faction, -1, -1, w.Pos, 0, "scar", w.Leader.Name, Where(w.Pos), FullName(w.Leader));
        }
    }

    void Fight(Band a, Band b, IRelations rel)
    {
        int startA = a.Size, startB = b.Size;
        a.Leader.Battles++;
        b.Leader.Battles++;
        Band? loser = null, winner = null;
        for (int r = 0; r < Cfg.RoundsPerFight && r < 64 && a.Alive && b.Alive; r++)
        {
            float pa = a.Power, pb = b.Power, tot = pa + pb + 0.001f;
            int killB = Rng.StochRound(Cfg.Lethality * a.Size * 2.0f * pa / tot);
            int killA = Rng.StochRound(Cfg.Lethality * b.Size * 2.0f * pb / tot);
            LoseMembers(b, killB, a);
            LoseMembers(a, killA, b);
            // morale break (KVS: rounds and morale breaks); bold and loyal leaders hold longer
            float brkA = Cfg.BreakMorale * (1.5f - 0.5f * a.Leader[Axis.Aggression] - 0.4f * a.Leader[Axis.Loyalty]);
            float brkB = Cfg.BreakMorale * (1.5f - 0.5f * b.Leader[Axis.Aggression] - 0.4f * b.Leader[Axis.Loyalty]);
            bool aBreaks = a.Alive && a.Morale < brkA, bBreaks = b.Alive && b.Morale < brkB;
            if (aBreaks && !bBreaks) { (loser, winner) = (a, b); break; }
            if (bBreaks && !aBreaks) { (loser, winner) = (b, a); break; }
            if (aBreaks && bBreaks) break;
        }

        if (loser == null)
        {
            if (!a.Alive && b.Alive) winner = b;
            else if (!b.Alive && a.Alive) winner = a;
        }

        int ni = NearestPlace(a.Pos);
        if (ni >= 0 && Vector2.Distance(a.Pos, Places[ni].Pos) < Cfg.PlaceRadius * 2) Places[ni].LastFight = Now;
        Emit(EventType.Battle, a.Faction, b.Faction, ni, a.Pos, 1, "battle", Factions[a.Faction].Name, Factions[b.Faction].Name, Where(a.Pos),
            Num(startA), Num(startB), Num(startA - a.Size), Num(startB - b.Size));
        if (a.Alive) a.Skill = Math.Clamp(a.Skill + 0.03f, 0.5f, 3.0f);
        if (b.Alive) b.Skill = Math.Clamp(b.Skill + 0.03f, 0.5f, 3.0f);
        foreach (var m in _modules) m.OnFightEnd(this, winner, loser, rel);

        if (loser is { Alive: true })
        {
            loser.State = BandState.Retreat;
            loser.Target = loser.Home;
            if (PlaceById(loser.Home) is { } h) loser.TargetPos = h.Pos;
            Emit(EventType.Rout, winner?.Faction ?? -1, loser.Faction, -1, loser.Pos, 0, "rout", FullName(loser.Leader));
        }
    }

    /// <summary>
    /// Hostile bands within the combat radius fight, each band at most once a tick (two retreating bands let each other go).
    /// </summary>
    bool HoldsFire(Band a, Band b)
    {
        foreach (var m in _modules) if (m.HoldsFire(this, a, b)) return true;
        return false;
    }

    void Combat(IRelations rel)
    {
        var fought = new bool[Bands.Count];
        float r2 = Cfg.CombatRadius * Cfg.CombatRadius;
        for (int i = 0; i < Bands.Count; i++)
        {
            var a = Bands[i];
            if (!a.Alive || fought[i] || Asleep(a) || a.External) continue;           // the game fights an external band for real
            for (int j = i + 1; j < Bands.Count; j++)
            {
                var b = Bands[j];
                if (!b.Alive || fought[j] || Asleep(b) || b.External || a.Faction == b.Faction) continue;
                if (a.State == BandState.Retreat && b.State == BandState.Retreat) continue;
                if (Vector2.DistanceSquared(a.Pos, b.Pos) > r2 || !Hostile(a.Faction, b.Faction, rel)) continue;
                if (HoldsFire(a, b)) continue;
                fought[i] = fought[j] = true;
                Fight(a, b, rel);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// What a save holds of the core, parsed but not yet applied.
    /// </summary>
    internal sealed class CoreRecord
    {
        public double Now, LastTick;
        public long TickCount, Repairs, NextEventSeq;
        public bool Started;
        public ulong Rng, NameRng;
        public int NextBandId, NextLeaderId;
        public string[] PlaceKeys = [], FactionKeys = [];
        public int[] PlaceOwner = [];
        public double[] PlaceLastFight = [];
        public (bool Enabled, float Aggression, int Captures, int Lost, int Warlord, string WarlordName)[] FactionState = [];
        public List<Band> Bands = [];
        public List<WorldEvent> Events = [], Chronicle = [];
    }

    internal void SaveCore(BinaryWriter w)
    {
        w.Write(Now);
        w.Write(LastTick);
        w.Write(TickCount);
        w.Write(Repairs);
        w.Write(_nextEventSeq);
        w.Write(Started);
        w.Write(Rng.State);
        w.Write(NameRng.State);
        w.Write(_nextBandId);
        w.Write(_nextLeaderId);

        w.Write(Factions.Count);
        foreach (var f in Factions)
        {
            w.Write(f.Key);
            w.Write(f.Enabled);
            w.Write(f.Aggression);
            w.Write(f.Captures);
            w.Write(f.Lost);
            w.Write(f.Warlord);
            w.Write(f.WarlordName);
        }

        w.Write(Places.Count);
        foreach (var p in Places)
        {
            w.Write(p.Key);
            w.Write(p.Owner);
            w.Write(p.LastFight);
        }

        w.Write(Bands.Count);
        foreach (var b in Bands) WriteBand(w, b);
        WriteEvents(w, Events);
        WriteEvents(w, Chronicle);
    }

    static void WriteBand(BinaryWriter w, Band b)
    {
        w.Write(b.Id);
        w.Write(b.Faction);
        w.Write(b.Size);
        w.Write(b.SizeMax);
        w.Write((int)b.Quality);
        w.Write(b.Skill);
        w.Write(b.Morale);
        w.Write(b.Xp);
        WorldSave.WriteVec(w, b.Pos);
        w.Write((int)b.State);
        w.Write((int)b.Role);
        w.Write(b.Home);
        w.Write(b.Target);
        WorldSave.WriteVec(w, b.TargetPos);
        w.Write(b.StayUntil);
        w.Write(b.Born);
        w.Write(b.Owner);
        w.Write(b.Tier);
        w.Write(b.External);
        var l = b.Leader;
        w.Write(l.Id);
        w.Write(l.Name);
        w.Write(l.Epithet);
        foreach (float a in l.Axes) w.Write(a);
        w.Write(l.Kills);
        w.Write(l.Battles);
        w.Write(l.Scars);
        w.Write(l.Captures);
        w.Write(l.PlayerWins);
        w.Write(l.PlayerLosses);
        w.Write(l.Toughness);
        w.Write(l.Grudge);
    }

    static Band ReadBand(BinaryReader r)
    {
        var b = new Band
        {
            Id = r.ReadInt32(), Faction = r.ReadInt32(), Size = r.ReadInt32(), SizeMax = r.ReadInt32(), Quality = ReadEnum<BandTier>(r),
            Skill = WorldSave.Fin(r), Morale = WorldSave.Fin(r), Xp = WorldSave.Fin(r), Pos = FinVec(r), State = ReadEnum<BandState>(r),
            Role = ReadEnum<BandRole>(r), Home = r.ReadInt32(), Target = r.ReadInt32(), TargetPos = FinVec(r), StayUntil = WorldSave.FinD(r),
            Born = WorldSave.FinD(r), Owner = r.ReadString(), Tier = r.ReadInt32(), External = r.ReadBoolean(),
        };
        if (b.Size < 0 || b.SizeMax < 0 || b.Size > 100000 || b.SizeMax > 100000) throw new InvalidDataException("bad band size");
        var l = b.Leader;
        l.Id = r.ReadInt32();
        l.Name = r.ReadString();
        l.Epithet = r.ReadString();
        for (int i = 0; i < Leader.AxisCount; i++) l.Axes[i] = Math.Clamp(WorldSave.Fin(r), 0, 1);
        l.Kills = r.ReadInt32();
        l.Battles = r.ReadInt32();
        l.Scars = r.ReadInt32();
        l.Captures = r.ReadInt32();
        l.PlayerWins = r.ReadInt32();
        l.PlayerLosses = r.ReadInt32();
        l.Toughness = WorldSave.Fin(r);
        l.Grudge = WorldSave.Fin(r);
        return b;
    }

    static T ReadEnum<T>(BinaryReader r) where T : struct, Enum
    {
        int v = r.ReadInt32();
        var e = (T)(object)v;
        if (!Enum.IsDefined(e)) throw new InvalidDataException($"bad {typeof(T).Name} {v}");
        return e;
    }

    static Vector2 FinVec(BinaryReader r) => new(WorldSave.Fin(r), WorldSave.Fin(r));

    static void WriteEvents(BinaryWriter w, List<WorldEvent> list)
    {
        w.Write(list.Count);
        foreach (var e in list)
        {
            w.Write(e.Seq);
            w.Write(e.Time);
            w.Write((int)e.Type);
            w.Write(e.A);
            w.Write(e.B);
            w.Write(e.Place);
            WorldSave.WriteVec(w, e.Pos);
            w.Write(e.Importance);
            w.Write(e.Key);
            w.Write(e.Args.Count);
            foreach (string a in e.Args) w.Write(a);
        }
    }

    static List<WorldEvent> ReadEvents(BinaryReader r)
    {
        int n = WorldSave.Count(r);
        var list = new List<WorldEvent>(Math.Min(n, 1000));
        for (int i = 0; i < n; i++)
        {
            long seq = r.ReadInt64();
            double time = WorldSave.FinD(r);
            var type = ReadEnum<EventType>(r);
            int a = r.ReadInt32(), b = r.ReadInt32(), place = r.ReadInt32();
            var pos = FinVec(r);
            int imp = r.ReadInt32();
            string key = r.ReadString();
            int argc = WorldSave.Count(r);
            if (argc > 64) throw new InvalidDataException("bad event");
            var args = new string[argc];
            for (int k = 0; k < argc; k++) args[k] = r.ReadString();
            list.Add(new WorldEvent(seq, time, type, a, b, place, pos, imp, key, args));
        }

        return list;
    }

    internal static CoreRecord ReadCore(BinaryReader r)
    {
        var c = new CoreRecord
        {
            Now = WorldSave.FinD(r), LastTick = WorldSave.FinD(r), TickCount = r.ReadInt64(), Repairs = r.ReadInt64(), NextEventSeq = r.ReadInt64(),
            Started = r.ReadBoolean(), Rng = r.ReadUInt64(), NameRng = r.ReadUInt64(), NextBandId = r.ReadInt32(), NextLeaderId = r.ReadInt32(),
        };
        int nf = WorldSave.Count(r);
        c.FactionKeys = new string[nf];
        c.FactionState = new (bool, float, int, int, int, string)[nf];
        for (int i = 0; i < nf; i++)
        {
            c.FactionKeys[i] = r.ReadString();
            c.FactionState[i] = (r.ReadBoolean(), Math.Clamp(WorldSave.Fin(r), 0, 1), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadString());
        }

        int np = WorldSave.Count(r);
        c.PlaceKeys = new string[np];
        c.PlaceOwner = new int[np];
        c.PlaceLastFight = new double[np];
        for (int i = 0; i < np; i++)
        {
            c.PlaceKeys[i] = r.ReadString();
            c.PlaceOwner[i] = r.ReadInt32();
            c.PlaceLastFight[i] = WorldSave.FinD(r);
        }

        int nb = WorldSave.Count(r);
        for (int i = 0; i < nb; i++) c.Bands.Add(ReadBand(r));
        c.Events = ReadEvents(r);
        c.Chronicle = ReadEvents(r);
        return c;
    }

    /// <summary>
    /// Puts a parsed save into this world: its places and factions by key (a saved one this world lacks is dropped, and so are the bands of
    /// a missing faction), the clock, the streams, the bands and the news.
    /// </summary>
    internal SaveMap MapOf(CoreRecord c) => new([.. c.PlaceKeys.Select(FindPlace)], [.. c.FactionKeys.Select(FindFaction)]);

    internal void ApplyCore(CoreRecord c, SaveMap map)
    {
        for (int i = 0; i < c.FactionKeys.Length; i++)
        {
            if (map.Faction(i) < 0) continue;
            var f = Factions[map.Faction(i)];
            (f.Enabled, f.Aggression, f.Captures, f.Lost, f.Warlord, f.WarlordName) = c.FactionState[i];
        }

        for (int i = 0; i < c.PlaceKeys.Length; i++)
        {
            if (map.Place(i) < 0) continue;
            var p = Places[map.Place(i)];
            p.Owner = map.Faction(c.PlaceOwner[i]);
            p.LastFight = c.PlaceLastFight[i];
        }

        Bands.Clear();
        foreach (var b in c.Bands)
        {
            b.Faction = map.Faction(b.Faction);
            if (b.Faction < 0) continue;
            b.Home = map.Place(b.Home);
            b.Target = map.Place(b.Target);
            Bands.Add(b);
        }

        Now = c.Now;
        LastTick = c.LastTick;
        TickCount = c.TickCount;
        Repairs = c.Repairs;
        Started = c.Started;
        Rng.State = c.Rng == 0 ? 1 : c.Rng;
        NameRng.State = c.NameRng == 0 ? 1 : c.NameRng;
        _nextBandId = Math.Max(c.NextBandId, Bands.Count == 0 ? 1 : Bands.Max(b => b.Id) + 1);
        _nextLeaderId = Math.Max(c.NextLeaderId, 1);
        _nextEventSeq = Math.Max(c.NextEventSeq, 1);
        Events.Clear();
        Events.AddRange(c.Events.Select(e => Remap(e, map)));
        Chronicle.Clear();
        Chronicle.AddRange(c.Chronicle.Select(e => Remap(e, map)));
        UpdateStats();
    }

    static WorldEvent Remap(WorldEvent e, SaveMap map) =>
        e with { A = e.A < 0 ? -1 : map.Faction(e.A), B = e.B < 0 ? -1 : map.Faction(e.B), Place = e.Place < 0 ? -1 : map.Place(e.Place) };
}
