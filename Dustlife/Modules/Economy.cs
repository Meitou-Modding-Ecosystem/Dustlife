// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using Dustlife.Core;

namespace Dustlife.Modules;

/// <summary>A faction's economic shape.</summary>
public sealed class EconomyProfile
{
    public string Faction = "";
    /// <summary>Production multipliers (1 = normal).</summary>
    public float FoodMult = 1, ArmsMult = 1;
    /// <summary>Non-empty: this faction's output and upkeep go to that faction's pool (a bloc, e.g. the Slave Traders in the UC's).</summary>
    public string PoolOf = "";
}

/// <summary>The economy's numbers (per game day).</summary>
public sealed class EconomyConfig
{
    /// <summary>Food per day by place kind (territory: times its resource points).</summary>
    public float FoodBase = 3, FoodResource = 2.5f, FoodTerritory = 0.25f;
    public float ArmsBase = 1.0f, ArmsResource = 0.1f;
    /// <summary>Food a man eats per day.</summary>
    public float FoodPerMan = 0.1f;
    /// <summary>Arms to raise one man.</summary>
    public float ArmsPerMan = 0.25f;
    /// <summary>A band is raised only if the pool then still feeds everyone this many days.</summary>
    public float RaiseReserveDays = 4;
    /// <summary>Food stock is capped at this many days of the pool's upkeep (plus 50).</summary>
    public float StockCapDays = 20;
    /// <summary>False = nothing is produced (a falsifier: garrisons must shrink).</summary>
    public bool Production = true;
    /// <summary>With the world's roads: a place its bloc can't reach from one of its bases, by roads its enemies don't block, produces
    /// nothing (a cut road). Off = the reference's economy.</summary>
    public bool CutRoads;
    public List<EconomyProfile> Profiles = [];
}

/// <summary>
/// Supply for the war, ported from the reference: every place a faction holds produces food and arms each day by its kind (a besieged
/// place produces nothing); a bloc pools its stock; every living band eats; a pool that runs dry loses men to desertion, field bands first
/// and settled garrisons last; Warfare raises a band only when the pool can feed it for a few days and arm it. Deterministic: no
/// randomness. Caravans and per-place stock come with Logistics Core.
/// </summary>
public sealed class Economy(EconomyConfig cfg) : IModule, ISavedModule
{
    public EconomyConfig Cfg { get; } = cfg;
    /// <summary>Stock by faction index; a pool holds the stock, its members' entries stay 0.</summary>
    public List<float> Food { get; } = [];
    public List<float> Arms { get; } = [];
    /// <summary>Last tick's daily rates, by pool.</summary>
    public List<float> ProdFood { get; } = [];
    public List<float> ProdArms { get; } = [];
    public List<float> Upkeep { get; } = [];
    public List<long> Deserted { get; } = [];
    public List<long> Raised { get; } = [];
    public List<long> Refused { get; } = [];
    public List<float> HungryHours { get; } = [];
    readonly List<float> _desertAcc = [];
    int[] _pool = [];
    readonly Dictionary<int, bool[]> _supplied = [];
    readonly Dictionary<int, HashSet<int>> _cutters = [];
    IRelations? _rel;
    double _suppliedHour = double.NaN;
    /// <summary>Place-hours lost to cut roads (a place that would have produced, but its road home was cut).</summary>
    public double CutHours { get; private set; }

    public string Name => "economy";

    public void Save(BinaryWriter w)
    {
        w.Write(Food.Count);
        for (int i = 0; i < Food.Count; i++)
        {
            w.Write(Food[i]);
            w.Write(Arms[i]);
            w.Write(Deserted[i]);
            w.Write(Raised[i]);
            w.Write(Refused[i]);
            w.Write(HungryHours[i]);
            w.Write(_desertAcc[i]);
        }

        w.Write(CutHours);
    }

    public Action Load(BinaryReader r, SaveMap map)
    {
        int n = WorldSave.Count(r);
        var rows = new List<(int F, float Food, float Arms, long D, long Ra, long Re, float H, float Acc)>();
        for (int i = 0; i < n; i++)
            rows.Add((map.Faction(i), Math.Max(0, WorldSave.Fin(r)), Math.Max(0, WorldSave.Fin(r)), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
                WorldSave.Fin(r), WorldSave.Fin(r)));
        double cut = WorldSave.FinD(r);
        return () => Apply(rows, cut);
    }

    void Apply(List<(int F, float Food, float Arms, long D, long Ra, long Re, float H, float Acc)> rows, double cut)
    {
        foreach (var row in rows)
        {
            if (row.F < 0) continue;
            while (Food.Count <= row.F)
            {
                Food.Add(0); Arms.Add(0); ProdFood.Add(0); ProdArms.Add(0); Upkeep.Add(0);
                Deserted.Add(0); Raised.Add(0); Refused.Add(0); HungryHours.Add(0); _desertAcc.Add(0);
            }

            (Food[row.F], Arms[row.F], Deserted[row.F], Raised[row.F], Refused[row.F], HungryHours[row.F], _desertAcc[row.F]) =
                (row.Food, row.Arms, row.D, row.Ra, row.Re, row.H, row.Acc);
        }

        CutHours = cut;
        _suppliedHour = double.NaN;
        _pool = [];
    }

    /// <summary>
    /// The economy of a world, or null.
    /// </summary>
    public static Economy? Of(World w) => w.GetModule("economy") as Economy;

    void Sync(World w)
    {
        while (Food.Count < w.Factions.Count)
        {
            Food.Add(0); Arms.Add(0); ProdFood.Add(0); ProdArms.Add(0); Upkeep.Add(0);
            Deserted.Add(0); Raised.Add(0); Refused.Add(0); HungryHours.Add(0); _desertAcc.Add(0);
        }

        if (_pool.Length != w.Factions.Count)
        {
            _pool = new int[w.Factions.Count];
            for (int i = 0; i < _pool.Length; i++)
            {
                _pool[i] = i;
                if (ProfileOf(w, i) is { PoolOf.Length: > 0 } p && w.FindFaction(p.PoolOf) is >= 0 and var to) _pool[i] = to;   // one level only
            }
        }
    }

    EconomyProfile? ProfileOf(World w, int faction) =>
        faction >= 0 && faction < w.Factions.Count ? Cfg.Profiles.Find(p => p.Faction == w.Factions[faction].Key) : null;

    /// <summary>
    /// The faction whose pool this faction uses.
    /// </summary>
    public int PoolOf(World w, int faction)
    {
        Sync(w);
        return faction >= 0 && faction < _pool.Length ? _pool[faction] : faction;
    }

    /// <summary>
    /// Whether the pool can arm <paramref name="men"/> and still feed everyone, them included, for the reserve days.
    /// </summary>
    public bool CanRaise(World w, int faction, int men)
    {
        int p = PoolOf(w, faction);
        if (p < 0 || p >= Food.Count || men <= 0) return false;
        float upkeepAfter = Upkeep[p] + men * Cfg.FoodPerMan;
        return Arms[p] >= men * Cfg.ArmsPerMan && Food[p] >= upkeepAfter * Cfg.RaiseReserveDays;
    }

    /// <summary>
    /// Pays for a raised band: its arms and its first day's rations.
    /// </summary>
    public void PayRaise(World w, int faction, int men)
    {
        int p = PoolOf(w, faction);
        if (p < 0 || p >= Food.Count || men <= 0) return;
        Arms[p] = Math.Max(0, Arms[p] - men * Cfg.ArmsPerMan);
        Food[p] = Math.Max(0, Food[p] - men * Cfg.FoodPerMan);
        Upkeep[p] += men * Cfg.FoodPerMan;
        Raised[faction]++;
    }

    /// <summary>
    /// A starting store: ten days of output for every pool.
    /// </summary>
    public void OnNewWorld(World w)
    {
        Sync(w);
        Rates(w, 0);
        for (int i = 0; i < Food.Count; i++) { Food[i] = ProdFood[i] * 10; Arms[i] = ProdArms[i] * 10; }
    }

    bool Cuts(World w) => Cfg.CutRoads && w.Roads != null && _rel != null;

    /// <summary>
    /// Which places the pool of <paramref name="faction"/> reaches from its bases by open roads (all true without cut roads), as of this tick.
    /// </summary>
    public bool[] Supplied(World w, int faction)
    {
        Sync(w);
        int pool = PoolOf(w, faction);
        if (!Cuts(w)) { var all = new bool[w.Places.Count]; Array.Fill(all, true); return all; }
        if (!_supplied.TryGetValue(pool, out var ok))
        {
            ok = w.Roads!.Supplied(w, pool, _rel!, f => PoolOf(w, f) == pool);
            _supplied[pool] = ok;
        }

        return ok;
    }

    /// <summary>
    /// The enemy-held or besieged places that cut the pool of <paramref name="faction"/> off from some of its places (empty without cut roads).
    /// </summary>
    public HashSet<int> Cutters(World w, int faction)
    {
        Sync(w);
        int pool = PoolOf(w, faction);
        if (!Cuts(w)) return [];
        if (!_cutters.TryGetValue(pool, out var c))
        {
            c = w.Roads!.Cutters(w, pool, Supplied(w, faction), _rel!, f => PoolOf(w, f) == pool);
            _cutters[pool] = c;
        }

        return c;
    }

    void Rates(World w, float dt)
    {
        int n = w.Factions.Count;
        for (int i = 0; i < n; i++) ProdFood[i] = ProdArms[i] = Upkeep[i] = 0;
        double hour = Math.Floor(w.Now);
        if (hour != _suppliedHour)                                           // supply is worked out once a game hour
        {
            _suppliedHour = hour;
            _supplied.Clear();
            _cutters.Clear();
        }
        if (Cfg.Production)
        {
            var besieged = w.BesiegedPlaces();
            foreach (var p in w.Places)
            {
                if (p.Owner < 0 || p.Owner >= n || p.PlayerOwned || p.Locked || p.Kind == PlaceKind.Lair || w.Factions[p.Owner].Fauna) continue;
                if (besieged.Contains(p.Id)) continue;                         // a besieged place produces nothing
                if (Cuts(w) && !Supplied(w, p.Owner)[p.Id]) { CutHours += dt; continue; }   // its road home is cut: nothing gets through
                (float f, float a) = p.Kind switch
                {
                    PlaceKind.Base => (Cfg.FoodBase, Cfg.ArmsBase),
                    PlaceKind.Resource => (Cfg.FoodResource, Cfg.ArmsResource),
                    _ => (Cfg.FoodTerritory * p.Resource, 0f),
                };
                if (ProfileOf(w, p.Owner) is { } prof) { f *= prof.FoodMult; a *= prof.ArmsMult; }
                int pool = PoolOf(w, p.Owner);
                ProdFood[pool] += f;
                ProdArms[pool] += a;
            }
        }

        foreach (var b in w.Bands)
            if (b.Alive && b.Faction >= 0 && b.Faction < n && !w.Factions[b.Faction].Fauna) Upkeep[PoolOf(w, b.Faction)] += b.Size * Cfg.FoodPerMan;
    }

    public void Tick(World w, float dt, IRelations rel)
    {
        Sync(w);
        _rel = rel;
        Rates(w, dt);
        if (dt <= 0) return;
        float day = dt / 24.0f;
        for (int p = 0; p < w.Factions.Count; p++)
        {
            if (PoolOf(w, p) != p) continue;                                // a member: its pool does the books
            Food[p] += (ProdFood[p] - Upkeep[p]) * day;
            Arms[p] += ProdArms[p] * day;
            Food[p] = Math.Min(Food[p], Upkeep[p] * Cfg.StockCapDays + 50.0f);
            Arms[p] = Math.Min(Arms[p], 500.0f);
            if (!World.Finite(Food[p])) Food[p] = 0;
            if (!World.Finite(Arms[p])) Arms[p] = 0;
            if (Food[p] >= 0) { _desertAcc[p] = 0; continue; }

            // hungry: the unfed men leave (whole men; the rest carries over). Field bands go first, settled garrisons last, largest
            // first within each: a capital is the last place a hungry army abandons.
            HungryHours[p] += dt;
            _desertAcc[p] += -Food[p] / Cfg.FoodPerMan;
            Food[p] = 0;
            int leave = (int)_desertAcc[p];
            _desertAcc[p] -= leave;
            while (leave > 0)
            {
                Band? best = null;
                foreach (var b in w.Bands)
                {
                    if (!b.Alive || b.Faction < 0 || PoolOf(w, b.Faction) != p) continue;
                    if (best == null) { best = b; continue; }
                    bool bField = b.Role != BandRole.Garrison || b.State != BandState.Settled;
                    bool cField = best.Role != BandRole.Garrison || best.State != BandState.Settled;
                    if (bField != cField) { if (bField) best = b; continue; }
                    if (b.Size > best.Size || (b.Size == best.Size && b.Id < best.Id)) best = b;
                }

                if (best == null) break;
                best.Size--;
                leave--;
                Deserted[best.Faction]++;
                if (best.Size <= 0) w.Kill(best);
            }
        }
    }
}
