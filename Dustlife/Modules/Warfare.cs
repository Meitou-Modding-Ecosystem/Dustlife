// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Modules;

/// <summary>
/// The war's numbers (the reference's <c>WarfareConfig</c>; times in game hours, distances in world units).
/// </summary>
public sealed class WarfareConfig
{
    /// <summary>The module name: also the band owner tag and the save tag ("warfare", "front.bast").</summary>
    public string Name = "warfare";
    /// <summary>Only places inside the circle take part.</summary>
    public bool Scoped;
    public Vector2 Center;
    public float Radius;
    /// <summary>Empty = every faction.</summary>
    public List<string> FactionKeys = [];
    public float DefenseHoursMin = 2, DefenseHoursMax = 8;
    public float PatrolHoursMin = 6, PatrolHoursMax = 24;
    public float TargetHoursMin = 1, TargetHoursMax = 9;
    public float DepartHoursMin = 0.5f, DepartHoursMax = 3;
    public float ResurgenceHoursMin = 36, ResurgenceHoursMax = 96;
    public float PatrolRestMin = 6, PatrolRestMax = 24;
    public float InvasionMin = 1, InvasionMax = 2.5f;
    /// <summary>Warfare's expansion roll: the chance a base looks for new targets when it re-plans.</summary>
    public float ExpansionAggression = 0.5f;
    /// <summary>How far from a held enemy place a band besieges it.</summary>
    public float SiegeOffset = 1000;
    /// <summary>Places this close score as "the same region" (a large bonus); patrols go no further.</summary>
    public float SameRegionDist = 80000;
    /// <summary>A real town's own people hold it this long when it is besieged undefended.</summary>
    public float HomeGuardHours = 12;
    /// <summary>A faction's band budget: the caps of its places plus this per base.</summary>
    public float BudgetPerBase = 8;
    public bool AllowPlayerTargets;
    public bool SeedGarrisons = true;
    /// <summary>An unguarded place keeps its owner until besieged undefended (off by default).</summary>
    public bool ClaimsHold;
    /// <summary>&gt; 0: a base that has mustered this long without enough men sends one band fewer, never fewer than the defenders + 1.</summary>
    public float MusterPatienceHours;
    /// <summary>The lore rules; empty = none.</summary>
    public List<Doctrine> Doctrines = [];
    /// <summary>Bands are raised from the economy's stock (food and arms), not the band budget alone.</summary>
    public bool UseEconomy;
    /// <summary>Non-null: targets are weighed by motive (need, grievance, retaliation, opportunity, settling) and takings say why.
    /// Null = the reference's behaviour.</summary>
    public MotiveConfig? Motives;
    /// <summary>
    /// F1 sieges (`tools/euler/siege_check.py`): a besieged place holds for its endurance E, the besiegers stay for their
    /// patience P, and neither fights the other until E is out (the investment). E days = base(kind) * (1 + 0.15 * garrison) *
    /// (0.6 + 0.4 * food); P days = 3 + 7 * patience * (1 - 0.5 * shortage). Off = the reference's 24-72 h sieges and 12 h home guard.
    /// </summary>
    public bool Sieges;
    public float EnduranceBaseDays = 10, EnduranceResourceDays = 7, EnduranceTerritoryDays = 3;
    /// <summary>&gt; 0: a place just taken is nobody's target for this many hours (no seesaw).</summary>
    public float HoldHours;
    /// <summary>
    /// F1 war rhythm (`tools/euler/war_rhythm_check.py`): a faction's war-weariness W grows by 1 / max(4, its bands) for each
    /// band it loses and halves every <see cref="WearinessHalfDays"/>; at <see cref="ExhaustedAt"/> it stops starting offensives and raids;
    /// once W is back to <see cref="RestedAt"/> after at least <see cref="MinExhaustedDays"/> it keeps a truce of
    /// <see cref="TruceDaysMin"/> to <see cref="TruceDaysMax"/> days, then renews its war. Off = the reference's war without rest.
    /// </summary>
    public bool Rhythm;
    public float WearinessHalfDays = 7, ExhaustedAt = 0.5f, RestedAt = 0.2f, MinExhaustedDays = 5, TruceDaysMin = 7, TruceDaysMax = 14;
}

/// <summary>F1: where a faction is in its war's rhythm.</summary>
public enum WarPhase { Offensive, Exhausted, Truce }

/// <summary>
/// The territory war (after STALKER Anomaly's Warfare): garrisons, budgets, targets, invasions, sieges, patrols, comebacks, who holds a
/// place, and the lore doctrines with their border wars. Ported from the reference (port steps 2b and 2c); its economy hooks come with
/// the economy step.
/// </summary>
public sealed class Warfare(WarfareConfig cfg) : IModule, ISavedModule
{
    /// <summary>A place's war state.</summary>
    public sealed class PlaceState
    {
        public int[] Targets = [-1, -1];
        /// <summary>The place its invasion is mustering for (-1 = none).</summary>
        public int InvTarget = -1;
        public int InvSize;
        public double InvReadySince = -1;
        public float InvDepartWait;
        public double NextDefense, NextPatrol, NextTargets, SiegeSince = -1, MusterSince = -1;
        /// <summary>Why its holder took it (None until taken, or without motives).</summary>
        public Motive TakenFor;
        /// <summary>F1: when the siege began (-1 = none), its endurance (hours) and phase (0 none, 1 investment, 2 starving, 3 ready to fall).</summary>
        public double Invested = -1, Endurance;
        public int Phase;
        /// <summary>F1: nobody targets the place before this game hour.</summary>
        public double HoldUntil = -1;
    }

    /// <summary>A faction's war state.</summary>
    public sealed class FactionState
    {
        public float InvasionFactor = 1;
        public int PatrolPop = 1;
        public int Budget, Bands;
        public double NextResurgence = -1, NextWar = -1;
        public double[] NextFront = [-1, -1, -1, -1];
        /// <summary>When each front's next raid party may set out.</summary>
        public double[] NextRaid = [-1, -1, -1, -1];
        /// <summary>F1 rhythm: war-weariness, the phase, when it began, when a truce ends, and the losses counted so far.</summary>
        public double Weariness, PhaseSince, TruceEnds;
        public WarPhase Phase;
        public int LostSeen;
    }

    /// <summary>How often each rule fired (a rule that never fires is a failed test).</summary>
    public sealed class Counts
    {
        public long Seeded, Respawned, Refused, Sieges, SiegesGivenUp, Overrun, Captures, ClaimsKept, Overflow;
        public long Invasions, Claims, Patrols, Resurgences, CunningWaits, MusterShrunk;
        public long Filtered, Ordered, Counter, NoInvade, WeakWait, HomeKept, Eased, FrontTargets, FrontWait, FrontPaced, WarPaced, OddsWait,
            Raids, Skilled, RaidParties, RaidNoTarget, RaidNoParty;
        /// <summary>Targets chosen and places taken, by motive; expansion targets passed over for having none.</summary>
        public long[] PickedFor = new long[Enum.GetValues<Motive>().Length], TookFor = new long[Enum.GetValues<Motive>().Length];
        public long NoMotive;
        /// <summary>F1: sieges begun, starving, ready to fall; fights held during an investment; targets passed over while held.</summary>
        public long Invested, Starving, ReadyToFall, HeldFire, Held, CutOff;
        /// <summary>F1 rhythm: wars worn out, truces made, wars renewed, offensives and raids not started while resting.</summary>
        public long Exhausted, Truces, Renewed, Resting;
    }

    public WarfareConfig Cfg { get; } = cfg;
    public List<PlaceState> Ps { get; } = [];
    public List<FactionState> Fs { get; } = [];
    public Counts Fired { get; } = new();
    readonly Dictionary<long, Motive> _why = [];
    readonly Dictionary<long, double> _lostAt = [];

    static long Key(int faction, int place) => ((long)faction << 32) | (uint)place;

    /// <summary>
    /// Why <paramref name="faction"/> has <paramref name="place"/> as a target (None = no recorded motive).
    /// </summary>
    public Motive WhyTarget(int faction, int place) => _why.GetValueOrDefault(Key(faction, place));

    void Want(World w, int faction, int place, Motive why)
    {
        if (Cfg.Motives == null) return;
        long k = Key(faction, place);
        if (_why.TryGetValue(k, out var had) && had == why) return;
        _why[k] = why;
        Fired.PickedFor[(int)why]++;
    }

    /// <summary>
    /// How short of food and of arms the faction's pool is, 0 (fine) to 1 (none), or (0, 0) without an economy.
    /// </summary>
    public (float Food, float Arms) Shortage(World w, int faction)
    {
        if (Cfg.Motives is not { } m || Economy.Of(w) is not { } e) return (0, 0);
        int p = e.PoolOf(w, faction);
        if (p < 0 || p >= e.Food.Count) return (0, 0);
        float days = e.Food[p] / Math.Max(e.Upkeep[p], 0.01f);
        float band = e.Cfg.ArmsPerMan * ((w.Cfg.SizeMin + w.Cfg.SizeMax + 1) / 2);
        float food = m.NeedDays > 0 ? Math.Clamp(1 - days / m.NeedDays, 0, 1) : 0;
        float arms = m.NeedBands > 0 && band > 0 ? Math.Clamp(1 - e.Arms[p] / (band * m.NeedBands), 0, 1) : 0;
        return (food, arms);
    }

    /// <summary>
    /// The strongest motive <paramref name="f"/> has for place <paramref name="o"/> as seen from its base <paramref name="n"/>, and the
    /// priority it adds.
    /// </summary>
    (Motive Why, float Bonus) MotiveFor(World w, Place n, int f, Place o, bool enemy, float myPow, float dist, bool retaliation)
    {
        var m = Cfg.Motives!;
        var best = Motive.None;
        float top = 0;
        void Consider(Motive why, float v) { if (v > top) (top, best) = (v, why); }

        var (food, arms) = Shortage(w, f);
        bool farms = o.Kind == PlaceKind.Resource || (o.Kind == PlaceKind.Territory && o.Resource > 0);
        if (food > 0 && farms) Consider(Motive.Food, m.NeedWeight * food);
        if (arms > 0 && o.Kind == PlaceKind.Base) Consider(Motive.Arms, m.NeedWeight * arms);
        if (Economy.Of(w) is { } econ && econ.Cutters(w, f).Contains(o.Id)) Consider(Motive.Lifeline, m.LifelineWeight);
        if (_lostAt.TryGetValue(Key(f, o.Id), out double lost) && w.Now - lost <= m.GrievanceHours) Consider(Motive.Grievance, m.GrievanceWeight);
        else if (o.OrigOwner == f) Consider(Motive.Grievance, m.HomelandWeight);
        if (retaliation) Consider(Motive.Retaliation, m.RetaliationWeight);
        if (enemy && dist < Cfg.SameRegionDist && w.SettledPower(o.Id, o.Owner) * m.OpportunityRatio <= myPow && myPow > 0)
            Consider(Motive.Opportunity, m.OpportunityWeight);
        if (o.Owner < 0 && o.Resource > 0 && dist < Cfg.SameRegionDist) Consider(Motive.Settle, m.SettleWeight);
        return (best, top);
    }

    /// <summary>
    /// Why <paramref name="taker"/> took <paramref name="p"/>: its recorded want, else a grievance (lost lately or held from the start), else a
    /// patrol that stayed on empty land.
    /// </summary>
    Motive MotiveAtTaking(World w, int taker, Place p, int from)
    {
        if (_why.Remove(Key(taker, p.Id), out var why)) return why;
        var m = Cfg.Motives!;
        if (_lostAt.TryGetValue(Key(taker, p.Id), out double lost) && w.Now - lost <= m.GrievanceHours) return Motive.Grievance;
        if (p.OrigOwner == taker) return Motive.Grievance;
        if (from < 0 && w.Bands.Any(b => b.Alive && b.Faction == taker && b.Role == BandRole.Patrol && w.Settled(b, p.Id))) return Motive.Patrol;
        return Motive.None;
    }

    public string Name => Cfg.Name;

    // ------------------------------------------------------------------ save

    public void Save(BinaryWriter w)
    {
        w.Write(Ps.Count);
        foreach (var s in Ps)
        {
            w.Write(s.Targets[0]);
            w.Write(s.Targets[1]);
            w.Write(s.InvTarget);
            w.Write(s.InvSize);
            w.Write(s.InvReadySince);
            w.Write(s.InvDepartWait);
            w.Write(s.NextDefense);
            w.Write(s.NextPatrol);
            w.Write(s.NextTargets);
            w.Write(s.SiegeSince);
            w.Write(s.MusterSince);
            w.Write((int)s.TakenFor);
        }

        w.Write(Fs.Count);
        foreach (var f in Fs)
        {
            w.Write(f.InvasionFactor);
            w.Write(f.PatrolPop);
            w.Write(f.NextResurgence);
            w.Write(f.NextWar);
            foreach (double t in f.NextFront) w.Write(t);
            foreach (double t in f.NextRaid) w.Write(t);
        }

        w.Write(_why.Count);
        foreach (var (k, v) in _why.OrderBy(x => x.Key)) { w.Write(k); w.Write((int)v); }
        w.Write(_lostAt.Count);
        foreach (var (k, v) in _lostAt.OrderBy(x => x.Key)) { w.Write(k); w.Write(v); }

        // F1 (appended: a save from before it simply ends here)
        w.Write(Ps.Count);
        foreach (var s in Ps) { w.Write(s.Invested); w.Write(s.Endurance); w.Write(s.Phase); w.Write(s.HoldUntil); }
        w.Write(Fs.Count);
        foreach (var f in Fs) { w.Write(f.Weariness); w.Write(f.PhaseSince); w.Write(f.TruceEnds); w.Write((int)f.Phase); w.Write(f.LostSeen); }
    }

    public Action Load(BinaryReader r, SaveMap map)
    {
        int np = WorldSave.Count(r);
        var ps = new List<(int Id, PlaceState S)>();
        for (int i = 0; i < np; i++)
        {
            var s = new PlaceState
            {
                Targets = [map.Place(r.ReadInt32()), map.Place(r.ReadInt32())], InvTarget = map.Place(r.ReadInt32()), InvSize = r.ReadInt32(),
                InvReadySince = WorldSave.FinD(r), InvDepartWait = WorldSave.Fin(r), NextDefense = WorldSave.FinD(r), NextPatrol = WorldSave.FinD(r),
                NextTargets = WorldSave.FinD(r), SiegeSince = WorldSave.FinD(r), MusterSince = WorldSave.FinD(r), TakenFor = ReadMotive(r),
            };
            ps.Add((map.Place(i), s));
        }

        int nf = WorldSave.Count(r);
        var fs = new List<(int Id, FactionState F)>();
        for (int i = 0; i < nf; i++)
        {
            var f = new FactionState { InvasionFactor = WorldSave.Fin(r), PatrolPop = r.ReadInt32(), NextResurgence = WorldSave.FinD(r), NextWar = WorldSave.FinD(r) };
            for (int k = 0; k < f.NextFront.Length; k++) f.NextFront[k] = WorldSave.FinD(r);
            for (int k = 0; k < f.NextRaid.Length; k++) f.NextRaid[k] = WorldSave.FinD(r);
            fs.Add((map.Faction(i), f));
        }

        var why = new List<(long, Motive)>();
        int nw = WorldSave.Count(r);
        for (int i = 0; i < nw; i++) why.Add((r.ReadInt64(), ReadMotive(r)));
        var lost = new List<(long, double)>();
        int nl = WorldSave.Count(r);
        for (int i = 0; i < nl; i++) lost.Add((r.ReadInt64(), WorldSave.FinD(r)));

        var f1 = new List<(int Id, double Inv, double End, int Phase, double Hold)>();
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            int n1 = WorldSave.Count(r);
            for (int i = 0; i < n1; i++)
            {
                double inv = WorldSave.FinD(r), end = WorldSave.FinD(r);
                int phase = r.ReadInt32();
                if (phase is < 0 or > 3) throw new InvalidDataException($"bad siege phase {phase}");
                f1.Add((map.Place(i), inv, end, phase, WorldSave.FinD(r)));
            }
        }

        var f1r = new List<(int Id, double Wear, double Since, double Truce, WarPhase Phase, int Lost)>();
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            int n2 = WorldSave.Count(r);
            for (int i = 0; i < n2; i++)
            {
                double wear = WorldSave.FinD(r), since = WorldSave.FinD(r), truce = WorldSave.FinD(r);
                int ph = r.ReadInt32(), seenLost = r.ReadInt32();
                if (!Enum.IsDefined((WarPhase)ph) || wear is < 0 or > 1e6 || seenLost < 0) throw new InvalidDataException($"bad war rhythm {ph} {wear} {seenLost}");
                f1r.Add((map.Faction(i), wear, since, truce, (WarPhase)ph, seenLost));
            }
        }

        return () =>
        {
            foreach (var (id, s) in ps) { if (id < 0) continue; while (Ps.Count <= id) Ps.Add(new PlaceState()); Ps[id] = s; }
            foreach (var (id, inv, end, phase, hold) in f1)
                if (id >= 0 && id < Ps.Count) (Ps[id].Invested, Ps[id].Endurance, Ps[id].Phase, Ps[id].HoldUntil) = (inv, end, phase, hold);
            foreach (var (id, f) in fs) { if (id < 0) continue; while (Fs.Count <= id) Fs.Add(new FactionState()); Fs[id] = f; }
            // after the faction states above are rebuilt (restoring it before them lost it: WarRhythmTests caught that)
            foreach (var (id, wear, since, truce, ph, seen) in f1r)
                if (id >= 0 && id < Fs.Count) (Fs[id].Weariness, Fs[id].PhaseSince, Fs[id].TruceEnds, Fs[id].Phase, Fs[id].LostSeen) = (wear, since, truce, ph, seen);
            _why.Clear();
            foreach (var (k, v) in why) if (Rekey(k, map) is { } nk) _why[nk] = v;
            _lostAt.Clear();
            foreach (var (k, v) in lost) if (Rekey(k, map) is { } nk) _lostAt[nk] = v;
        };
    }

    static Motive ReadMotive(BinaryReader r)
    {
        int v = r.ReadInt32();
        if (!Enum.IsDefined((Motive)v)) throw new InvalidDataException($"bad motive {v}");
        return (Motive)v;
    }

    static long? Rekey(long k, SaveMap map)
    {
        int f = map.Faction((int)(k >> 32)), p = map.Place((int)(uint)k);
        return f < 0 || p < 0 ? null : Key(f, p);
    }

    bool Mine(Band b) => b.Owner == Cfg.Name;

    /// <summary>F1: a place taken lately is nobody's target yet.</summary>
    bool Held(World w, Place o) => Cfg.HoldHours > 0 && o.Id < Ps.Count && Ps[o.Id].HoldUntil > w.Now;

    /// <summary>F1: how long a place holds out under siege (hours): its kind, its garrison and its holder's food.</summary>
    public double EnduranceHours(World w, Place p)
    {
        float baseDays = p.Kind switch { PlaceKind.Base => Cfg.EnduranceBaseDays, PlaceKind.Resource => Cfg.EnduranceResourceDays, _ => Cfg.EnduranceTerritoryDays };
        int garrison = p.Owner >= 0 ? w.CountSettled(p.Id, p.Owner) : 0;
        float food = p.Owner >= 0 ? 1 - Shortage(w, p.Owner).Food : 1;
        return 24.0 * baseDays * (1 + 0.15 * garrison) * (0.6 + 0.4 * food);
    }

    /// <summary>F1: how long a band besieges before giving up (hours): its leader's patience and its faction's shortage.</summary>
    public double PatienceHours(World w, Band b)
    {
        var (food, arms) = Shortage(w, b.Faction);
        return 24.0 * (3 + 7 * b.Leader[Axis.Patience] * (1 - 0.5 * Math.Max(food, arms)));
    }

    /// <summary>
    /// F1: besiegers and the place's own defenders hold fire until the place is ready to fall (the investment); a relief column, a sortie
    /// away from the place, or anyone else fights as before.
    /// </summary>
    public bool HoldsFire(World w, Band a, Band b)
    {
        if (!Cfg.Sieges) return false;
        if (Holds(w, a, b) || Holds(w, b, a)) { Fired.HeldFire++; return true; }
        return false;
    }

    bool Holds(World w, Band besieger, Band other)
    {
        // an invader marching on the place holds fire too: it invests the place, it doesn't storm it on arrival (seen in the balance runs:
        // bands walking in one by one fought the garrison piecemeal and the siege was "broken at its walls" within a day or two)
        bool investing = besieger.State == BandState.Siege || (besieger.State == BandState.Moving && besieger.Role == BandRole.Invader);
        if (!Mine(besieger) || !investing || w.PlaceById(besieger.Target) is not { } t) return false;
        return other.Faction == t.Owner && w.Settled(other, t.Id) && t.Id < Ps.Count && Ps[t.Id].Phase < 3;
    }

    /// <summary>F1: a place's siege clock: investment, starving at half its endurance, ready to fall at its end; reset when lifted.</summary>
    void SiegeClock(World w, Place p, PlaceState s, bool besieged)
    {
        if (!besieged || p.Owner < 0)
        {
            if (s.Invested >= 0) (s.Invested, s.Endurance, s.Phase) = (-1, 0, 0);
            return;
        }

        if (s.Invested < 0)
        {
            (s.Invested, s.Endurance, s.Phase) = (w.Now, EnduranceHours(w, p), 1);
            Fired.Invested++;
            return;
        }

        double t = w.Now - s.Invested;
        string holder = w.Factions[p.Owner].Name;
        int imp = p.Kind == PlaceKind.Base ? 2 : 1;
        if (s.Phase == 1 && t >= s.Endurance / 2)
        {
            s.Phase = 2;
            Fired.Starving++;
            w.Emit(EventType.Info, -1, p.Owner, p.Id, p.Pos, imp, "siege_starving", p.Name, holder);
        }

        if (s.Phase == 2 && t >= s.Endurance)
        {
            s.Phase = 3;
            Fired.ReadyToFall++;
            w.Emit(EventType.Info, -1, p.Owner, p.Id, p.Pos, imp, "siege_ready", p.Name, holder);
        }
    }

    /// <summary>
    /// A stable 0..1 value per index. It must not draw from the world's generator: it also runs while a save is loaded, after the
    /// generator's state has been restored.
    /// </summary>
    static float Jitter(uint i, uint salt)
    {
        uint h = unchecked((i * 2654435761u) ^ (salt * 2246822519u));
        h ^= h >> 15;
        h = unchecked(h * 2246822519u);
        h ^= h >> 13;
        return (h & 0xFFFF) / 65535.0f;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static bool Named(List<string> list, string name) => list.Contains(name);

    void Sync(World w)
    {
        while (Ps.Count < w.Places.Count)
        {
            uint i = (uint)Ps.Count;
            Ps.Add(new PlaceState
            {
                NextDefense = w.Now + Lerp(0.5f, Cfg.DefenseHoursMax, Jitter(i, 1)),
                NextPatrol = w.Now + Lerp(1.0f, Cfg.PatrolHoursMax, Jitter(i, 2)),
                NextTargets = w.Now + Lerp(0.5f, 3.0f, Jitter(i, 3)),
            });
        }

        while (Fs.Count < w.Factions.Count)
            Fs.Add(new FactionState { NextResurgence = w.Now + Lerp(Cfg.ResurgenceHoursMin, Cfg.ResurgenceHoursMax, Jitter((uint)Fs.Count, 4)) });
    }

    /// <summary>
    /// The doctrine of <paramref name="faction"/>, or null.
    /// </summary>
    public Doctrine? DoctrineOf(World w, int faction) =>
        faction >= 0 && faction < w.Factions.Count ? Cfg.Doctrines.Find(d => d.Faction == w.Factions[faction].Key) : null;

    /// <summary>
    /// A place this war covers: active, not locked, inside the circle when scoped.
    /// </summary>
    public bool InScope(World w, int placeId) =>
        w.PlaceById(placeId) is { Active: true, Locked: false } p && (!Cfg.Scoped || Vector2.Distance(p.Pos, Cfg.Center) <= Cfg.Radius);

    /// <summary>
    /// A faction this war runs: not fauna, enabled, and in the list when there is one.
    /// </summary>
    public bool FactionInScope(World w, int faction) =>
        faction >= 0 && faction < w.Factions.Count && !w.Factions[faction].Fauna && w.Factions[faction].Enabled
        && (Cfg.FactionKeys.Count == 0 || Cfg.FactionKeys.Contains(w.Factions[faction].Key));

    /// <summary>
    /// The faction's share of the world's resource points, stretched three times (a Kenshi faction rarely holds a third of the world).
    /// </summary>
    public static float ResourceShare(World w, int faction) => Math.Clamp((float)w.Factions[faction].Resources / w.WorldResources * 3.0f, 0, 1);

    /// <summary>
    /// Whether a new band may be raised: room under the world's cap, and then either the band budget or, with the economy, a pool that can
    /// arm a middle-sized band and feed it for its reserve days (a refusal is counted by the economy).
    /// </summary>
    bool CanSpawn(World w, int faction)
    {
        if (w.Bands.Count >= w.Cfg.MaxBands) return false;
        if (!Cfg.UseEconomy || Economy.Of(w) is not { } e) return Fs[faction].Bands < Fs[faction].Budget;
        bool ok = e.CanRaise(w, faction, (w.Cfg.SizeMin + w.Cfg.SizeMax + 1) / 2);
        if (!ok) e.Refused[faction]++;
        return ok;
    }

    /// <summary>
    /// The front <paramref name="o"/> is a border target on for this doctrine, or -1. An empty place counts as its first holder's.
    /// </summary>
    public int FrontOf(World w, Doctrine? doc, Place o)
    {
        if (doc == null) return -1;
        int who = o.Owner >= 0 ? o.Owner : o.OrigOwner;
        if (who < 0) return -1;
        for (int k = 0; k < doc.Fronts.Count && k < Doctrine.MaxFronts; k++)
        {
            var fr = doc.Fronts[k];
            if (fr.Radius <= 0 || fr.Enemies.Count == 0 || Vector2.Distance(o.Pos, fr.Center) > fr.Radius) continue;
            if (fr.TerritoryOnly && o.Kind != PlaceKind.Territory) continue;
            if (fr.NoBases && o.Kind == PlaceKind.Base) continue;
            if (Named(fr.Enemies, w.Factions[who].Key)) return k;
        }

        return -1;
    }

    /// <summary>
    /// Whether the doctrine's ease-off holds now: its ease faction holds more than its limit of places the victim held at the start.
    /// </summary>
    public static bool Easing(World w, Doctrine? doc) =>
        doc is { EaseAbove: > 0 } && w.FindFaction(doc.EaseFaction) is >= 0 and var ef && w.FindFaction(doc.EaseVictim) is >= 0 and var ev
        && w.Places.Count(p => p.OrigOwner == ev && p.Owner == ef) > doc.EaseAbove;

    /// <summary>
    /// Whether <paramref name="o"/> is a border-war target for this doctrine: on one of its fronts, and either a raid front or a place its
    /// main limits forbid.
    /// </summary>
    public bool IsBorder(World w, Doctrine? doc, Place o)
    {
        int fk = FrontOf(w, doc, o);
        if (fk < 0) return false;
        int who = o.Owner >= 0 ? o.Owner : o.OrigOwner;
        return doc!.Fronts[fk].Raids || (doc.OnlyPlaces.Count > 0 && !Named(doc.OnlyPlaces, o.Name))
            || (doc.Enemies.Count > 0 && who >= 0 && !Named(doc.Enemies, w.Factions[who].Key));
    }

    /// <summary>
    /// What a new band of a doctrine's faction gets (its skill multiplier).
    /// </summary>
    void PaySpawn(World w, int bandId)
    {
        if (bandId <= 0 || w.FindBand(bandId) is not { } b) return;
        if (DoctrineOf(w, b.Faction) is { } doc && doc.SkillMult != 1.0f)
        {
            b.Skill = Math.Clamp(b.Skill * doc.SkillMult, 0.5f, 3.0f);
            Fired.Skilled++;
        }

        if (Cfg.UseEconomy && Economy.Of(w) is { } e) e.PayRaise(w, b.Faction, b.Size);
    }

    // ------------------------------------------------------------------ seed and tick

    /// <summary>
    /// Warfare's starting positions: every held base starts with two garrison bands, every resource point with one.
    /// </summary>
    public void OnNewWorld(World w)
    {
        Sync(w);
        if (!Cfg.SeedGarrisons) return;
        foreach (var p in w.Places)
        {
            if (p.Owner < 0 || p.PlayerOwned || p.Kind == PlaceKind.Lair || !InScope(w, p.Id) || !FactionInScope(w, p.Owner)) continue;
            int count = p.Kind == PlaceKind.Base ? 2 : p.Kind == PlaceKind.Resource ? 1 : 0;
            // the starting garrisons are the world as found: no doctrine skill, no economy cost (as the reference)
            for (int k = 0; k < count; k++)
                if (w.SpawnBand(p.Owner, p.Id, BandRole.Garrison, p.Pos, Name) > 0) Fired.Seeded++;
        }
    }

    /// <summary>
    /// The budgets, each base's garrison, targets, invasion and patrols, then comebacks.
    /// </summary>
    public void Tick(World w, float dt, IRelations rel)
    {
        Sync(w);
        int maxBases = Math.Max(1, w.Factions.Count == 0 ? 1 : w.Factions.Max(f => f.Bases));
        foreach (var s in Fs) (s.Budget, s.Bands) = (0, 0);
        foreach (var p in w.Places)
        {
            if (p.Owner < 0 || !InScope(w, p.Id)) continue;
            Fs[p.Owner].Budget += p.Cap;
            if (p.Kind == PlaceKind.Base) Fs[p.Owner].Budget += (int)Cfg.BudgetPerBase;
        }

        foreach (var b in w.Bands) if (b.Alive && Mine(b)) Fs[b.Faction].Bands++;

        // The world has one cap on bands. When the budgets add up to more than this war's share of it, every budget shrinks by the same
        // factor: no faction is starved because others filled the cap first.
        long total = Fs.Sum(s => (long)s.Budget);
        float share = w.Cfg.MaxBands * (Cfg.Scoped ? 0.25f : 0.8f);
        if (total > 0 && total > share)
        {
            float k = share / total;
            foreach (var s in Fs) if (s.Budget > 0) s.Budget = Math.Max(2, (int)(s.Budget * k));
        }

        for (int i = 0; i < w.Factions.Count; i++)
        {
            float br = Math.Clamp((float)w.Factions[i].Bases / maxBases, 0, 1);
            Fs[i].InvasionFactor = Lerp(Cfg.InvasionMin, Cfg.InvasionMax, br) * (0.8f + 0.4f * w.Factions[i].Aggression);
            Fs[i].PatrolPop = 1 + (int)(br + 0.5f);
        }

        if (Cfg.Rhythm) WarRhythm(w, dt);

        foreach (var p in w.Places)
        {
            if (p.Kind != PlaceKind.Base || p.Owner < 0 || p.PlayerOwned || !InScope(w, p.Id) || !FactionInScope(w, p.Owner)) continue;
            BaseUpdate(w, p, rel);
        }

        Resurgence(w, rel);
        RaidParties(w, rel);
    }

    /// <summary>
    /// F1: each faction's war-weariness and its phase: offensive, exhausted, truce, then offensive again (see <see cref="WarfareConfig.Rhythm"/>).
    /// </summary>
    void WarRhythm(World w, float dt)
    {
        double fade = Math.Pow(0.5, dt / (24.0 * Math.Max(0.1, Cfg.WearinessHalfDays)));
        for (int i = 0; i < w.Factions.Count; i++)
        {
            if (!FactionInScope(w, i)) continue;
            var s = Fs[i];
            var f = w.Factions[i];
            int lost = Math.Max(0, f.Lost - s.LostSeen);
            s.LostSeen = f.Lost;
            s.Weariness = s.Weariness * fade + lost / (double)Math.Max(4, s.Bands);
            switch (s.Phase)
            {
                case WarPhase.Offensive when s.Weariness >= Cfg.ExhaustedAt:
                    (s.Phase, s.PhaseSince) = (WarPhase.Exhausted, w.Now);
                    Fired.Exhausted++;
                    w.Emit(EventType.Info, i, -1, -1, default, 2, "war_exhausted", f.Name);
                    break;
                case WarPhase.Exhausted when s.Weariness <= Cfg.RestedAt && w.Now - s.PhaseSince >= 24.0 * Cfg.MinExhaustedDays:
                    (s.Phase, s.PhaseSince) = (WarPhase.Truce, w.Now);
                    s.TruceEnds = w.Now + 24.0 * w.Rng.Range(Cfg.TruceDaysMin, Cfg.TruceDaysMax);
                    Fired.Truces++;
                    w.Emit(EventType.Info, i, -1, -1, default, 2, "war_truce", f.Name);
                    break;
                case WarPhase.Truce when w.Now >= s.TruceEnds:
                    (s.Phase, s.PhaseSince) = (WarPhase.Offensive, w.Now);
                    Fired.Renewed++;
                    w.Emit(EventType.Info, i, -1, -1, default, 2, "war_renewed", f.Name);
                    break;
            }
        }
    }

    /// <summary>F1: whether a faction is resting from its war (no new offensives or raids).</summary>
    public bool Resting(int faction) => Cfg.Rhythm && faction >= 0 && faction < Fs.Count && Fs[faction].Phase != WarPhase.Offensive;

    // ------------------------------------------------------------------ raid parties

    /// <summary>
    /// Raid parties on the doctrines' raid fronts (<see cref="Front.PartySize"/>): each front, once its pace allows, picks the weakest enemy
    /// place on it (never a base when the front says so), and the faction's nearest place that keeps a band home sends a party of one or
    /// two bands at it. Small, cheap and often: the border smoulders without stripping a town.
    /// </summary>
    void RaidParties(World w, IRelations rel)
    {
        foreach (var doc in Cfg.Doctrines)
        {
            int f = w.FindFaction(doc.Faction);
            if (f < 0 || !FactionInScope(w, f) || doc.NoInvade) continue;
            if (Resting(f)) { Fired.Resting++; continue; }
            for (int k = 0; k < doc.Fronts.Count && k < Doctrine.MaxFronts; k++)
            {
                var fr = doc.Fronts[k];
                if (fr.PartySize <= 0 || fr.Radius <= 0 || w.Now < Fs[f].NextRaid[k]) continue;
                Fs[f].NextRaid[k] = w.Now + Math.Max(1, fr.EveryHours) * w.Rng.Range(0.75f, 1.25f);
                if (RaidTarget(w, f, fr, rel) is not { } target) { Fired.RaidNoTarget++; continue; }
                if (!SendParty(w, f, fr, target)) Fired.RaidNoParty++;
            }
        }
    }

    Place? RaidTarget(World w, int f, Front fr, IRelations rel)
    {
        Place? best = null;
        float bestPow = float.MaxValue;
        foreach (var o in w.Places)
        {
            if (o.Owner < 0 || o.Owner == f || !w.Hostile(f, o.Owner, rel) || !InScope(w, o.Id) || o.Kind == PlaceKind.Lair || o.PlayerOwned) continue;
            if (Vector2.Distance(o.Pos, fr.Center) > fr.Radius || !Named(fr.Enemies, w.Factions[o.Owner].Key)) continue;
            if ((fr.NoBases && o.Kind == PlaceKind.Base) || (fr.TerritoryOnly && o.Kind != PlaceKind.Territory)) continue;
            float pow = w.SettledPower(o.Id, o.Owner);
            if (fr.WeakBelow > 0 && pow >= fr.WeakBelow) continue;
            if (w.CountHeadingTo(o.Id, f) > 0) continue;                    // one party at a time per place
            if (Held(w, o)) { Fired.Held++; continue; }
            if (pow < bestPow) (bestPow, best) = (pow, o);
        }

        return best;
    }

    bool SendParty(World w, int f, Front fr, Place target)
    {
        Place? from = null;
        float bd = float.MaxValue;
        foreach (var p in w.Places)
        {
            if (p.Owner != f || !InScope(w, p.Id) || p.Kind == PlaceKind.Lair) continue;
            if (w.CountSettled(p.Id, f) < fr.PartySize + 1) continue;          // one band stays home
            float d = w.TravelDist(p.Id, target.Id);
            if (d < bd) (bd, from) = (d, p);
        }

        if (from == null) return false;
        var party = w.Bands.Where(b => b.Alive && Mine(b) && b.Faction == f && b.Role == BandRole.Garrison && w.Settled(b, from.Id))
            .OrderByDescending(b => b.Leader[Axis.Aggression]).ThenBy(b => b.Id).Take(fr.PartySize).ToList();
        if (party.Count == 0) return false;
        foreach (var b in party) w.Send(b, target.Id, BandRole.Invader);
        Fired.RaidParties++;
        Fired.Raids++;
        Want(w, f, target.Id, Motive.Opportunity);
        w.Emit(EventType.Invasion, f, target.Owner, target.Id, from.Pos, 2, "raid", w.Factions[f].Name, target.Name, from.Name,
            World.Num(party.Count), w.FullName(party[0].Leader));
        return true;
    }

    void BaseUpdate(World w, Place p, IRelations rel)
    {
        var s = Ps[p.Id];
        int f = p.Owner;
        // F1: an invested town is cut off: no new garrison raised inside and no patrols sent out (the balance runs showed its own patrols and
        // fresh garrisons wiping the besiegers one by one, ~20 bands a run); a relief army from elsewhere can still break the siege
        bool cutOff = Cfg.Sieges && s.Phase is 1 or 2;
        if (cutOff)
        {
            Fired.CutOff++;
            if (w.Now >= s.NextTargets) { s.NextTargets = w.Now + w.Rng.Range(Cfg.TargetHoursMin, Cfg.TargetHoursMax); PickTargets(w, p, rel); }
            return;
        }

        if (w.Now >= s.NextDefense)
        {
            // garrisons come back faster the more of the world the faction holds
            s.NextDefense = w.Now + Lerp(Cfg.DefenseHoursMax, Cfg.DefenseHoursMin, ResourceShare(w, f)) * w.Rng.Range(0.75f, 1.25f)
                / Math.Max(0.05f, p.Activity);
            bool playerClose = rel.HostileToPlayer(f) && w.NearestPlayerDist(p.Pos) < w.Cfg.NoSpawnRadius;
            int soft = p.Cap + (s.InvTarget >= 0 ? s.InvSize : 0);
            if (!playerClose && w.CountHeadingTo(p.Id, f) < soft)
            {
                if (!CanSpawn(w, f)) Fired.Refused++;
                else
                {
                    int id = w.SpawnBand(f, p.Id, BandRole.Garrison, p.Pos, Name);
                    if (id > 0) { Fs[f].Bands++; Fired.Respawned++; PaySpawn(w, id); }
                }
            }
        }

        if (w.Now >= s.NextTargets)
        {
            s.NextTargets = w.Now + w.Rng.Range(Cfg.TargetHoursMin, Cfg.TargetHoursMax);
            PickTargets(w, p, rel);
        }

        StageInvasion(w, p, rel);
        Patrols(w, p, rel);
    }

    // ------------------------------------------------------------------ targets

    /// <summary>
    /// A base keeps two targets. A doctrine's own target goes first (the counter-strike when the enemy's home is thin, else the first place
    /// of its order not yet ours); then, on Warfare's expansion roll, the best-scoring places by priority over distance squared, one
    /// friendly base per target.
    /// </summary>
    void PickTargets(World w, Place n, IRelations rel)
    {
        var s = Ps[n.Id];
        int f = n.Owner;
        for (int k = 0; k < 2; k++)
        {
            if (s.Targets[k] < 0) continue;
            var tn = w.Places[s.Targets[k]];
            if (tn.Owner == f || (tn.Owner >= 0 && !w.Hostile(f, tn.Owner, rel)) || !InScope(w, tn.Id)) s.Targets[k] = -1;
        }

        if (s.Targets[0] < 0 && s.Targets[1] >= 0) (s.Targets[0], s.Targets[1]) = (s.Targets[1], -1);
        var doc = DoctrineOf(w, f);
        if (doc != null)
        {
            int forced = -1;
            if (doc.CounterTarget.Length > 0 && doc.CounterHomeBelow > 0 && w.FindFaction(doc.CounterFaction) is >= 0 and var cf)
            {
                int atHome = 0;
                foreach (var o in w.Places) if (o.Owner == cf && o.Kind == PlaceKind.Base) atHome += w.CountSettled(o.Id, cf);
                if (atHome < doc.CounterHomeBelow)
                {
                    foreach (var o in w.Places)
                    {
                        if (o.Name != doc.CounterTarget || o.Owner == f || !InScope(w, o.Id) || (o.Owner >= 0 && !w.Hostile(f, o.Owner, rel))) continue;
                        forced = o.Id;
                        Fired.Counter++;
                        Want(w, f, o.Id, Motive.Counter);
                        break;
                    }
                }
            }

            for (int k = 0; forced < 0 && k < doc.Order.Count; k++)
            {
                var o = w.Places.Find(x => x.Name == doc.Order[k]);
                if (o == null || o.Owner == f) continue;                   // ours already: the next one in order
                if (Held(w, o)) { Fired.Held++; break; }                    // just taken: the order waits rather than skipping ahead
                if (!InScope(w, o.Id) || (o.Owner >= 0 && !w.Hostile(f, o.Owner, rel))) continue;
                forced = o.Id;
                Fired.Ordered++;
                Want(w, f, o.Id, Motive.Doctrine);
            }

            if (forced >= 0 && s.Targets[0] != forced) (s.Targets[1], s.Targets[0]) = (s.Targets[0], forced);
        }

        if (!w.Rng.Chance(Math.Clamp(Cfg.ExpansionAggression * n.Aggression, 0, 1))) return;

        int[] bestId = [-1, -1];
        float[] bestSc = [0, 0];
        Motive[] bestWhy = [Motive.None, Motive.None];
        float myPow = w.SettledPower(n.Id, f);
        // one friendly base per target: mark what friends already aim at
        var claimed = new bool[w.Places.Count];
        foreach (var b in w.Places)
        {
            if (b.Id == n.Id || b.Owner < 0 || (b.Owner != f && w.Hostile(f, b.Owner, rel))) continue;
            var bs = Ps[b.Id];
            foreach (int m in (int[])[bs.Targets[0], bs.Targets[1], bs.InvTarget]) if (m >= 0 && m < claimed.Length) claimed[m] = true;
        }

        foreach (var o in w.Places)
        {
            if (o.Id == n.Id || o.Kind == PlaceKind.Lair || !InScope(w, o.Id) || (o.PlayerOwned && !Cfg.AllowPlayerTargets) || o.Owner == f) continue;
            if (Held(w, o)) { Fired.Held++; continue; }
            bool enemy = o.Owner >= 0 && w.Hostile(f, o.Owner, rel);
            if ((o.Owner >= 0 && !enemy) || o.Id == s.Targets[0] || o.Id == s.Targets[1]) continue;
            if (doc != null)
            {
                // the doctrine's limits: never these; only these places; only these enemies' places (an empty place counts as its first
                // holder's); a front lifts the last two
                int who = o.Owner >= 0 ? o.Owner : o.OrigOwner;
                bool front = FrontOf(w, doc, o) >= 0;
                if (Named(doc.Never, o.Name) || (!front && ((doc.OnlyPlaces.Count > 0 && !Named(doc.OnlyPlaces, o.Name))
                    || (doc.Enemies.Count > 0 && who >= 0 && !Named(doc.Enemies, w.Factions[who].Key)))))
                {
                    Fired.Filtered++;
                    continue;
                }

                if (front) Fired.FrontTargets++;
            }

            float d = Vector2.Distance(n.Pos, o.Pos);
            float pr = o.Kind switch
            {
                PlaceKind.Base => 10,
                PlaceKind.Resource => 5.0f * Math.Max(1, o.Resource),
                _ => o.Resource > 0 ? 5.0f * o.Resource : -5.0f,
            };
            var os = Ps[o.Id];
            bool retaliation = os.Targets[0] == n.Id || os.Targets[1] == n.Id;
            if (retaliation) pr += 2;                                        // retaliation
            if (enemy)
            {
                if (w.SettledPower(o.Id, o.Owner) > myPow) pr += 1;
                var them = w.Factions[o.Owner];
                var us = w.Factions[f];
                if (them.Bases + them.Resources * them.Resources > us.Bases + us.Resources * us.Resources) pr += 1;
            }

            if (d < Cfg.SameRegionDist) pr += 100;                          // Warfare's "same level" bonus
            if (pr <= 0 || claimed[o.Id]) continue;
            var why = Motive.None;
            if (Cfg.Motives is { } mc)
            {
                (why, float bonus) = MotiveFor(w, n, f, o, enemy, myPow, d, retaliation);
                if (why == Motive.None && mc.RequireMotive) { Fired.NoMotive++; continue; }   // no reason to go there
                pr += bonus;
            }

            float dm = (w.Roads != null ? w.TravelDist(n.Id, o.Id) : d) / 10.0f;   // metres, as Warfare measures; by road when there are roads
            float sc = pr / Math.Max(1.0f, dm * dm);
            if (sc > bestSc[0])
            {
                (bestSc[1], bestId[1], bestWhy[1]) = (bestSc[0], bestId[0], bestWhy[0]);
                (bestSc[0], bestId[0], bestWhy[0]) = (sc, o.Id, why);
            }
            else if (sc > bestSc[1]) (bestSc[1], bestId[1], bestWhy[1]) = (sc, o.Id, why);
        }

        for (int k = 0; k < 2; k++)
        {
            int id = bestId[k];
            if (id < 0) continue;
            if (s.Targets[0] < 0) s.Targets[0] = id;
            else if (s.Targets[1] < 0) s.Targets[1] = id;
            else continue;
            Want(w, f, id, bestWhy[k]);
        }
    }

    // ------------------------------------------------------------------ invasions

    /// <summary>
    /// Drops the staged invasion and puts the other target first (a strong place stays a target for later).
    /// </summary>
    static void Drop(PlaceState s, bool swap)
    {
        s.InvTarget = -1;
        s.InvReadySince = -1;
        if (swap) (s.Targets[0], s.Targets[1]) = (s.Targets[1], s.Targets[0]);
    }

    /// <summary>
    /// A base stages an invasion of a target, checks its doctrine's gates before mustering (a base never sits waiting on a target it may not
    /// attack yet), musters its garrison plus a home guard, waits a little, then sends its boldest captains first.
    /// </summary>
    void StageInvasion(World w, Place n, IRelations rel)
    {
        var s = Ps[n.Id];
        int f = n.Owner;
        var doc = DoctrineOf(w, f);
        if (doc is { NoInvade: true })
        {
            if (s.Targets[0] >= 0 || s.InvTarget >= 0) Fired.NoInvade++;   // holds and defends
            Drop(s, false);
            return;
        }

        if (s.InvTarget >= 0)
        {
            var t = w.Places[s.InvTarget];
            if (t.Owner == f || (t.Owner >= 0 && !w.Hostile(f, t.Owner, rel)) || !InScope(w, t.Id)) Drop(s, false);
            else if (Held(w, t)) { Fired.Held++; Drop(s, true); }              // taken since it was picked: wait out its hold
        }

        if (Resting(f))
        {
            // the faction rests: no new offensive, and one still mustering stands down (the war.rhythm fuzz target caught a mustered
            // invasion marching after its faction was worn out); its garrisons still hold
            if (s.Targets[0] >= 0 || s.InvTarget >= 0) Fired.Resting++;
            if (s.InvTarget >= 0) Drop(s, false);
            return;
        }

        if (s.InvTarget < 0)
        {
            foreach (int t in s.Targets)
            {
                if (t < 0 || !InScope(w, t) || w.CountHeadingTo(t, f) >= n.Cap) continue;
                if (Held(w, w.Places[t])) { Fired.Held++; continue; }
                s.InvTarget = t;
                s.InvSize = Math.Clamp((int)(w.Places[t].Cap * Fs[f].InvasionFactor * (0.5f + 0.5f * n.Aggression) + 0.5f), 1, 16);
                s.InvDepartWait = w.Rng.Range(Cfg.DepartHoursMin, Cfg.DepartHoursMax);
                s.InvReadySince = -1;
                break;
            }

            if (s.InvTarget < 0) return;
        }

        var gt = w.Places[s.InvTarget];
        int fk = FrontOf(w, doc, gt);
        var fr = fk >= 0 ? doc!.Fronts[fk] : null;
        bool border = IsBorder(w, doc, gt);
        if (!border && Easing(w, doc))
        {
            Fired.Eased++;                                                   // keeps the buffer: no main-war invasion
            Drop(s, true);
            return;
        }

        if (border && fr!.WeakBelow > 0 && gt.Owner >= 0 && w.SettledPower(gt.Id, gt.Owner) >= fr.WeakBelow)
        {
            Fired.FrontWait++;                                               // the border war stays low-intensity
            Drop(s, true);
            return;
        }

        if (border && fr!.EveryHours > 0 && w.Now < Fs[f].NextFront[fk])
        {
            Fired.FrontPaced++;                                              // one border attack at a time: the war smoulders
            Drop(s, true);
            return;
        }

        if (!border && doc is { WarEveryHours: > 0 } && gt.Owner >= 0 && w.Now < Fs[f].NextWar)
        {
            Fired.WarPaced++;                                                // one assault at a time for the whole faction
            Drop(s, true);
            return;
        }

        if (!border && doc is { WeakBelow: > 0 } && gt.Owner >= 0 && w.SettledPower(gt.Id, gt.Owner) >= doc.WeakBelow)
        {
            Fired.WeakWait++;                                                // the defenders are still strong: wait
            Drop(s, true);
            return;
        }

        var ready = w.Bands.Where(b => b.Alive && Mine(b) && b.Faction == f && b.Role == BandRole.Garrison && w.Settled(b, n.Id)).ToList();
        int threshold = s.InvSize + Math.Max(1, n.Cap / 2);
        if (ready.Count < threshold)
        {
            s.InvReadySince = -1;
            if (Cfg.MusterPatienceHours <= 0) return;
            // a patient captain: after waiting long enough he goes with one band fewer, but never with fewer than the defenders + 1
            if (s.MusterSince < 0) { s.MusterSince = w.Now; return; }
            if (w.Now - s.MusterSince < Cfg.MusterPatienceHours) return;
            int floor = Math.Max(2, (gt.Owner >= 0 ? w.CountSettled(gt.Id, gt.Owner) : 0) + 1);
            if (s.InvSize > floor) { s.InvSize--; Fired.MusterShrunk++; }
            s.MusterSince = w.Now;
            return;
        }

        s.MusterSince = -1;
        if (s.InvReadySince < 0) { s.InvReadySince = w.Now; return; }
        if (w.Now - s.InvReadySince < s.InvDepartWait) return;

        ready.Sort((a, b) => b.Leader[Axis.Aggression].CompareTo(a.Leader[Axis.Aggression]));   // the boldest captains go first
        int send = Math.Min(s.InvSize, ready.Count);
        if (doc != null && doc.HomeKeep > 0 && Named(doc.Home, n.Name) && ready.Count - send < doc.HomeKeep)
        {
            Fired.HomeKept++;                                                // the home garrison stays
            send = ready.Count - doc.HomeKeep;
            if (send <= 0) { s.InvReadySince = w.Now; return; }
        }

        float sendPow = 0, cunning = 0;
        for (int k = 0; k < send; k++) { sendPow += ready[k].Power; cunning = Math.Max(cunning, ready[k].Leader[Axis.Cunning]); }
        float defPow = gt.Owner >= 0 ? w.SettledPower(gt.Id, gt.Owner) : 0;
        if (cunning > 0.7f && defPow > sendPow * 1.3f)
        {
            // a cunning captain refuses a hopeless attack and waits for more men
            s.InvReadySince = w.Now;
            if (s.InvSize < 16) s.InvSize++;
            Fired.CunningWaits++;
            return;
        }

        if (doc is { OddsAbove: > 0 } && defPow > 0 && sendPow < defPow * doc.OddsAbove)
        {
            Fired.OddsWait++;                                                // the odds are bad: try the other target next
            Drop(s, true);
            return;
        }

        string leader = w.FullName(ready[0].Leader);
        if (!border && doc is { WarEveryHours: > 0 } && gt.Owner >= 0) Fs[f].NextWar = w.Now + doc.WarEveryHours * w.Rng.Range(0.75f, 1.25f);
        if (border && fr!.EveryHours > 0) Fs[f].NextFront[fk] = w.Now + fr.EveryHours * w.Rng.Range(0.75f, 1.25f);
        if (border && fr!.Raids) Fired.Raids++;
        for (int k = 0; k < send; k++) w.Send(ready[k], gt.Id, BandRole.Invader);
        if (gt.Owner >= 0)
        {
            Fired.Invasions++;
            w.Emit(EventType.Invasion, f, gt.Owner, gt.Id, n.Pos, gt.Kind == PlaceKind.Base ? 3 : 2, "march", w.Factions[f].Name, gt.Name, n.Name,
                World.Num(send), leader);
        }
        else
        {
            Fired.Claims++;
            w.Emit(EventType.Invasion, f, -1, gt.Id, n.Pos, gt.Kind == PlaceKind.Base ? 3 : 2, "claim", w.Factions[f].Name, World.Num(send), n.Name,
                gt.Name);
        }

        Drop(s, false);
    }

    // ------------------------------------------------------------------ patrols and comebacks

    /// <summary>
    /// Territory and resource points that are empty or ours, nearest first, within the region; a random one of the nearest few. A doctrine
    /// keeps patrols off empty places whose first holder it doesn't fight, and off <c>Never</c>.
    /// </summary>
    int FindPatrolTarget(World w, Place home, int faction)
    {
        var doc = DoctrineOf(w, faction);
        var c = new List<(float D, int Id)>();
        foreach (var o in w.Places)
        {
            if (o.Id == home.Id || o.Kind is PlaceKind.Base or PlaceKind.Lair || o.PlayerOwned || !InScope(w, o.Id)) continue;
            if (o.Owner >= 0 && o.Owner != faction) continue;
            if (o.Owner < 0 && doc != null && o.OrigOwner >= 0 && o.OrigOwner != faction)
            {
                if (Named(doc.Never, o.Name)) continue;
                if (doc.Enemies.Count > 0 && !Named(doc.Enemies, w.Factions[o.OrigOwner].Key) && FrontOf(w, doc, o) < 0) continue;
            }

            float d = Vector2.Distance(home.Pos, o.Pos);
            if (d <= Cfg.SameRegionDist) c.Add((w.Roads != null ? w.TravelDist(home.Id, o.Id) : d, o.Id));   // nearest by road when there are roads
        }

        if (c.Count == 0) return -1;
        c.Sort();
        int keep = Math.Min(Fs[faction].PatrolPop * 2 + 1, c.Count);
        return c[w.Rng.Int(0, keep - 1)].Id;
    }

    void Patrols(World w, Place n, IRelations rel)
    {
        var s = Ps[n.Id];
        int f = n.Owner;
        if (w.Now < s.NextPatrol) return;
        s.NextPatrol = w.Now + Lerp(Cfg.PatrolHoursMax, Cfg.PatrolHoursMin, ResourceShare(w, f)) * w.Rng.Range(0.75f, 1.25f) / Math.Max(0.05f, n.Activity);
        int have = w.Bands.Count(b => b.Alive && Mine(b) && b.Home == n.Id && b.Role == BandRole.Patrol);
        if (have >= Fs[f].PatrolPop || !CanSpawn(w, f)) return;
        if (rel.HostileToPlayer(f) && w.NearestPlayerDist(n.Pos) < w.Cfg.NoSpawnRadius) return;
        int t = FindPatrolTarget(w, n, f);
        if (t < 0) return;
        int id = w.SpawnBand(f, n.Id, BandRole.Patrol, n.Pos, Name);
        if (w.FindBand(id) is not { } b) return;
        PaySpawn(w, id);
        Fs[f].Bands++;
        Fired.Patrols++;
        w.Send(b, t, BandRole.Patrol);
    }

    /// <summary>
    /// A faction that lost every base comes back now and then, if it started with one: bands gather at the place nearest the hostile or
    /// empty base closest to its old home, and march on it. Never in front of the player.
    /// </summary>
    void Resurgence(World w, IRelations rel)
    {
        for (int fi = 0; fi < w.Factions.Count; fi++)
        {
            var f = w.Factions[fi];
            if (!FactionInScope(w, fi) || f.Bases > 0 || w.Now < Fs[fi].NextResurgence) continue;
            Fs[fi].NextResurgence = w.Now + w.Rng.Range(Cfg.ResurgenceHoursMin, Cfg.ResurgenceHoursMax);
            var home = w.Places.Find(p => p.OrigOwner == fi && p.Kind == PlaceKind.Base && InScope(w, p.Id));
            if (home == null) continue;                                      // only factions that started with a base come back
            Place? target = null;
            float bd = float.MaxValue;
            foreach (var o in w.Places)
            {
                if (o.Kind != PlaceKind.Base || o.PlayerOwned || !InScope(w, o.Id) || (o.Owner >= 0 && !w.Hostile(fi, o.Owner, rel))) continue;
                if (Held(w, o)) { Fired.Held++; continue; }                     // a comeback waits out the hold too
                float d = Vector2.DistanceSquared(home.Pos, o.Pos);
                if (d < bd) (bd, target) = (d, o);
            }

            if (target == null) continue;
            Place? stage = null;
            float sd = float.MaxValue;
            foreach (var o in w.Places)
            {
                if (o.Id == target.Id || o.Kind is PlaceKind.Base or PlaceKind.Lair || !InScope(w, o.Id)) continue;
                float d = Vector2.DistanceSquared(target.Pos, o.Pos);
                if (d < sd) (sd, stage) = (d, o);
            }

            var at = stage?.Pos ?? target.Pos + new Vector2(8000, 0);
            if (rel.HostileToPlayer(fi) && w.NearestPlayerDist(at) < w.Cfg.NoSpawnRadius + 1000.0f)
            {
                Fs[fi].NextResurgence = w.Now + 6.0;                         // the comeback waits until nobody is watching
                continue;
            }

            int made = 0;
            for (int k = 0; k <= target.Cap; k++)
            {
                if (w.FindBand(w.SpawnBand(fi, stage?.Id ?? target.Id, BandRole.Invader, at, Name)) is not { } b) break;
                b.Home = target.Id;
                w.Send(b, target.Id, BandRole.Invader);
                made++;
            }

            if (made == 0) continue;
            Fired.Resurgences++;
            Want(w, fi, target.Id, Motive.Grievance);                        // retaking what it lost
            w.Emit(EventType.Resurgence, fi, target.Owner, target.Id, at, 2, "resurgence", f.Name, World.Num(made), target.Name);
        }
    }

    // ------------------------------------------------------------------ arrival, thinking, holding

    /// <summary>
    /// Warfare's key rule: a band never settles on an enemy-held place; it besieges it from the edge. A patrol rests where it arrives.
    /// </summary>
    public bool OnArrive(World w, Band b, IRelations rel)
    {
        if (!Mine(b)) return false;
        if (w.PlaceById(b.Target) is { Owner: >= 0 } t && w.Hostile(b.Faction, t.Owner, rel))
        {
            float dd = Vector2.Distance(b.Pos, t.Pos);
            b.Pos = dd > 1.0f ? t.Pos + (b.Pos - t.Pos) / dd * Cfg.SiegeOffset : t.Pos + new Vector2(Cfg.SiegeOffset, 0);
            bool first = !w.Bands.Any(o => o != b && o.Alive && o.Faction == b.Faction && o.State == BandState.Siege && o.Target == t.Id);
            b.State = BandState.Siege;
            b.StayUntil = Cfg.Sieges ? w.Now + PatienceHours(w, b) : w.Now + 24.0f + 48.0f * b.Leader[Axis.Patience];
            Fired.Sieges++;
            if (first) w.Emit(EventType.SiegeBegan, b.Faction, t.Owner, t.Id, t.Pos, t.Kind == PlaceKind.Base ? 2 : 1, "siege_begin", w.Factions[b.Faction].Name, t.Name, w.Factions[t.Owner].Name);
            return true;
        }

        b.State = BandState.Settled;
        b.StayUntil = b.Role == BandRole.Patrol
            ? w.Now + w.Rng.Range(Cfg.PatrolRestMin, Cfg.PatrolRestMax) * (0.5f + b.Leader[Axis.Patience])
            : w.Now + w.Rng.Range(1.0f, 4.0f);
        return true;
    }

    public bool Think(World w, Band b, IRelations rel)
    {
        if (!Mine(b)) return false;

        if (b.State == BandState.Siege)
        {
            if (w.PlaceById(b.Target) is not { } t) { b.State = BandState.Settled; return true; }
            if (t.Owner < 0 || t.Owner == b.Faction || !w.Hostile(b.Faction, t.Owner, rel))
            {
                w.Send(b, t.Id, b.Role);           // the defenders are gone: walk in
                return true;
            }

            b.Morale = Math.Clamp(b.Morale - 0.02f, 0, 1);     // sieges wear men down
            if (w.Now > b.StayUntil)
            {
                b.State = BandState.Retreat;
                b.Target = b.Home;
                if (w.PlaceById(b.Home) is { } h) b.TargetPos = h.Pos;
                Fired.SiegesGivenUp++;
                w.Emit(EventType.Rout, t.Owner, b.Faction, t.Id, b.Pos, 1, "siege_end", w.FullName(b.Leader), t.Name);
            }

            return true;
        }

        int at = b.Target >= 0 && w.Settled(b, b.Target) ? b.Target : -1;
        if (at >= 0 && !InScope(w, at))
        {
            // standing in a place this war doesn't cover (a boss hideout): walk to the nearest own place
            int dest = w.NearestOwnedPlace(b.Pos, b.Faction, at);
            if (dest >= 0 && InScope(w, dest)) { w.Send(b, dest, BandRole.Garrison); b.Home = dest; }
            else b.StayUntil = w.Now + 24;
            return true;
        }

        if (w.PlaceById(at) is not { } p)
        {
            if (w.Now < b.StayUntil) return true;
            int dest = w.NearestOwnedPlace(b.Pos, b.Faction);
            if (dest >= 0 && InScope(w, dest)) w.Send(b, dest, BandRole.Garrison);
            else b.StayUntil = w.Now + 24;
            return true;
        }

        if (p.Owner >= 0 && p.Owner != b.Faction)
        {
            // a guest at someone else's place moves on
            if (w.Now < b.StayUntil) return true;
            int dest = b.Home;
            if (w.PlaceById(dest) is not { } home || home.Owner != b.Faction) dest = w.NearestOwnedPlace(b.Pos, b.Faction, at);
            if (dest >= 0 && dest != at && InScope(w, dest)) w.Send(b, dest, b.Role == BandRole.Patrol ? BandRole.Patrol : BandRole.Garrison);
            else b.StayUntil = w.Now + 12;
            return true;
        }

        if (b.Role == BandRole.Invader) { b.Role = BandRole.Garrison; b.Home = at; }     // the taken place becomes home
        if (b.Role == BandRole.Patrol)
        {
            if (w.Now < b.StayUntil) return true;
            if (w.PlaceById(b.Home) is { } home && home.Owner == b.Faction && FindPatrolTarget(w, home, b.Faction) is >= 0 and var t && t != at)
            {
                w.Send(b, t, BandRole.Patrol);
                return true;
            }

            b.Role = BandRole.Garrison;
            b.Home = at;
        }

        int keepAt = p.Cap + 1;
        if (Ps[at].InvTarget >= 0) keepAt = Math.Max(keepAt, Ps[at].InvSize + Math.Max(1, p.Cap / 2));   // mustering: the invasion and a home guard
        if (b.Role == BandRole.Garrison && p.Owner == b.Faction && w.Now >= b.StayUntil && w.CountSettled(at, b.Faction) > keepAt)
        {
            // overflow: the surplus walks to the nearest own place with room
            int dest = -1;
            float bd = float.MaxValue;
            foreach (var o in w.Places)
            {
                if (o.Owner != b.Faction || o.Id == at || !InScope(w, o.Id) || w.CountHeadingTo(o.Id, b.Faction) >= o.Cap) continue;
                float d = Vector2.DistanceSquared(b.Pos, o.Pos);
                if (d < bd) (bd, dest) = (d, o.Id);
            }

            if (dest >= 0) { w.Send(b, dest, BandRole.Garrison); b.Home = dest; Fired.Overflow++; }
            else b.StayUntil = w.Now + 24;
        }

        return true;
    }

    /// <summary>
    /// Who holds each place. The holder keeps it while any of its bands is settled there. A real town's own people hold it until it has been
    /// besieged undefended for <see cref="WarfareConfig.HomeGuardHours"/>; then it falls to nobody. Otherwise the faction with the most
    /// bands settled there takes it (the lowest index wins a tie), and its most renowned captain there gets the credit.
    /// </summary>
    public void PostTick(World w, float dt, IRelations rel)
    {
        Sync(w);
        var besieged = new HashSet<int>(w.Bands.Where(b => b.Alive && b.State == BandState.Siege && b.Target >= 0).Select(b => b.Target));
        var settled = new Dictionary<int, int>();
        foreach (var p in w.Places)
        {
            if (p.Kind == PlaceKind.Lair || p.PlayerOwned || !InScope(w, p.Id)) continue;
            if (p.Owner >= 0 && !FactionInScope(w, p.Owner) && Cfg.FactionKeys.Count > 0) continue;   // not this front's place
            var s = Ps[p.Id];
            if (Cfg.Sieges) SiegeClock(w, p, s, besieged.Contains(p.Id));
            settled.Clear();
            var here = w.Bands.Where(b => b.Alive && !w.Factions[b.Faction].Fauna && w.Settled(b, p.Id)).ToList();
            foreach (var b in here) settled[b.Faction] = settled.GetValueOrDefault(b.Faction) + 1;
            int o = p.Owner;
            if (o >= 0 && settled.GetValueOrDefault(o) > 0) { s.SiegeSince = -1; continue; }     // the holder keeps it

            bool homeGuard = o >= 0 && p.Real && o == p.OrigOwner && p.Kind is PlaceKind.Base or PlaceKind.Resource;
            bool underSiege = besieged.Contains(p.Id);
            if (o >= 0 && !homeGuard && Cfg.ClaimsHold && !underSiege) { Fired.ClaimsKept++; s.SiegeSince = -1; continue; }
            if (homeGuard || (o >= 0 && Cfg.ClaimsHold))
            {
                if (!underSiege) { s.SiegeSince = -1; continue; }
                if (s.SiegeSince < 0) { s.SiegeSince = w.Now; continue; }
                if (Cfg.Sieges ? s.Phase < 3 : w.Now - s.SiegeSince < Cfg.HomeGuardHours) continue;   // F1: until ready to fall
                s.SiegeSince = -1;
                p.Owner = -1;
                s.Targets[0] = s.Targets[1] = -1;
                s.InvTarget = -1;
                s.TakenFor = Motive.None;
                if (Cfg.Motives != null) _lostAt[Key(o, p.Id)] = w.Now;
                Fired.Overrun++;
                w.Emit(EventType.Lost, -1, o, p.Id, p.Pos, p.Kind == PlaceKind.Base ? 3 : 2, "overrun", p.Name, w.Factions[o].Name);
                continue;
            }

            int cand = -1, best = 0;
            foreach (var (f, n) in settled)
            {
                if (!FactionInScope(w, f)) continue;
                if (n > best || (n == best && f < cand)) (best, cand) = (n, f);
            }

            if (cand == o) continue;
            p.Owner = cand;
            s.Targets[0] = s.Targets[1] = -1;
            s.InvTarget = -1;
            s.InvReadySince = -1;
            s.TakenFor = Motive.None;
            if (o >= 0 && Cfg.Motives != null) _lostAt[Key(o, p.Id)] = w.Now;
            if (cand < 0) continue;
            if (Cfg.HoldHours > 0) s.HoldUntil = w.Now + Cfg.HoldHours;
            w.Factions[cand].Captures++;
            Fired.Captures++;
            var credit = here.Where(b => b.Faction == cand).OrderByDescending(b => b.Leader.Renown).ThenBy(b => b.Id).FirstOrDefault();
            if (credit != null && credit.Leader.Captures < 100000) credit.Leader.Captures++;
            int imp = p.Kind == PlaceKind.Base ? 3 : p.Kind == PlaceKind.Resource ? 2 : 1;
            string took = "took", hold = "hold";
            if (Cfg.Motives != null)
            {
                var why = MotiveAtTaking(w, cand, p, o);
                s.TakenFor = why;
                Fired.TookFor[(int)why]++;
                if (why != Motive.None)
                {
                    string tag = why.ToString().ToLowerInvariant();
                    (took, hold) = ("took_" + tag, "hold_" + tag);
                }
            }

            if (o >= 0) w.Emit(EventType.Capture, cand, o, p.Id, p.Pos, imp, took, w.Factions[cand].Name, p.Name, w.Factions[o].Name);
            else w.Emit(EventType.Capture, cand, -1, p.Id, p.Pos, imp, hold, w.Factions[cand].Name, p.Name);
        }
    }
}
