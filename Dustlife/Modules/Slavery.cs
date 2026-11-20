// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Modules;

/// <summary>
/// Prisoners and the slave economy (after KVS), ported from the reference: a slaver faction that wins a fight takes some of the losers in
/// chains; slave labour adds resource points up to a cap; some slaves escape or die every day.
/// </summary>
public sealed class Slavery : IModule, ISavedModule
{
    public int MaxSlaves = 100000;
    public int MaxPrisoners = 3;
    public int SlavesPerResource = 10;
    public int MaxBonus = 5;
    public float LossPerDay = 0.02f;
    /// <summary>Slaves by faction index.</summary>
    public List<int> Slaves { get; } = [];
    readonly List<float> _lossAcc = [];

    public string Name => "slavery";

    public void Save(BinaryWriter w)
    {
        w.Write(Slaves.Count);
        for (int i = 0; i < Slaves.Count; i++) { w.Write(Slaves[i]); w.Write(_lossAcc[i]); }
    }

    public Action Load(BinaryReader r, SaveMap map)
    {
        int n = WorldSave.Count(r);
        var rows = new List<(int F, int S, float A)>();
        for (int i = 0; i < n; i++) rows.Add((map.Faction(i), Math.Clamp(r.ReadInt32(), 0, MaxSlaves), WorldSave.Fin(r)));
        return () =>
        {
            foreach (var (f, s, a) in rows)
            {
                if (f < 0) continue;
                while (Slaves.Count <= f) { Slaves.Add(0); _lossAcc.Add(0); }
                (Slaves[f], _lossAcc[f]) = (s, a);
            }
        };
    }

    void Sync(World w)
    {
        while (Slaves.Count < w.Factions.Count) { Slaves.Add(0); _lossAcc.Add(0); }
    }

    public void Tick(World w, float dt, IRelations rel)
    {
        Sync(w);
        for (int i = 0; i < w.Factions.Count; i++)
        {
            if (Slaves[i] <= 0) continue;
            w.Factions[i].Resources += Math.Min(MaxBonus, Slaves[i] / Math.Max(1, SlavesPerResource));   // slave labour feeds the economy
            _lossAcc[i] += Slaves[i] * LossPerDay * dt / 24.0f;                                          // some escape or die every day
            if (_lossAcc[i] < 1.0f) continue;
            int lost = Math.Min((int)_lossAcc[i], Slaves[i]);
            _lossAcc[i] -= (int)_lossAcc[i];
            Slaves[i] -= lost;
            if (lost < 3) continue;
            int home = w.NearestOwnedPlace(Vector2.Zero, i);
            if (home >= 0)
                w.Emit(EventType.Escape, i, -1, home, w.Places[home].Pos, 1, "escape_near", World.Num(lost), w.Factions[i].Name, w.Places[home].Name);
            else w.Emit(EventType.Escape, i, -1, -1, Vector2.Zero, 1, "escape", World.Num(lost), w.Factions[i].Name);
        }
    }

    public void OnFightEnd(World w, Band? winner, Band? loser, IRelations rel)
    {
        if (winner == null || loser == null || !loser.Alive) return;
        if (!w.Factions[winner.Faction].Slaver || w.Factions[loser.Faction].Fauna || w.Factions[winner.Faction].Fauna) return;
        Sync(w);
        int taken = w.Rng.Int(0, Math.Min(loser.Size, MaxPrisoners));
        if (winner.Leader[Axis.Greed] > 0.7f && taken < loser.Size) taken++;      // greedy captains take more
        if (taken <= 0) return;
        loser.Size -= taken;
        if (Slaves[winner.Faction] < MaxSlaves) Slaves[winner.Faction] += taken;
        w.Emit(EventType.Slaves, winner.Faction, loser.Faction, -1, loser.Pos, 1, "slaves", w.Factions[winner.Faction].Name, World.Num(taken),
            w.Factions[loser.Faction].Name, w.Where(loser.Pos));
        if (loser.Size <= 0) w.Kill(loser);
    }
}
