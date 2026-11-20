// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Globalization;
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>What the game can do with squads for Dustlife (the engine's world hooks; a fake in the tests).</summary>
public interface ISquadHost
{
    /// <summary>
    /// Spawns a real squad of <paramref name="template"/> for Dustlife's faction <paramref name="faction"/>, homed at Dustlife's place
    /// <paramref name="home"/>, near <paramref name="at"/>, tagged, held and sent toward <paramref name="goal"/>. The squad's id, or -1.
    /// </summary>
    int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag);

    /// <summary>Sends a held squad toward a point.</summary>
    void Send(int squad, Vector2 goal);

    /// <summary>The squad's state, or null when the game has no such squad (beaten and removed, or never saved).</summary>
    (bool Dead, int Size, Vector2 Pos)? Look(int squad);

    /// <summary>Every squad with a Dustlife tag (after a load: squad ids are new, the tags are kept).</summary>
    IEnumerable<(int Squad, string Tag)> Tagged();
}

/// <summary>The hand-over's numbers.</summary>
public sealed class SquadConfig
{
    /// <summary>A marching band this close to one of the player's people is handed to the game; never closer than the minimum (not in view).</summary>
    public float HandOverRadius = 7000, MinRadius = 2500;
    /// <summary>At most this many bands are the game's squads at once, and one more per sync.</summary>
    public int MaxSquads = 4;
    /// <summary>Squad templates by faction key: an invader's and a patrol's.</summary>
    public Dictionary<string, (string Invader, string Patrol)> Templates = [];
}

/// <summary>
/// Hands Dustlife's bands to the game as real squads near the player (port step 5c), the KVS fidelity step: a marching invader or patrol
/// within <see cref="SquadConfig.HandOverRadius"/> becomes the game's squad of its faction's war template, tagged <c>dustlife:&lt;band&gt;</c>,
/// walking to the band's goal. The band is then external: its position and losses come from the squad, and if the squad is gone the band
/// is gone. The engine has no despawn, so a squad stays the game's for good; that is why only marching bands near the player are handed
/// over, and never more than a few.
/// </summary>
public sealed class Squads(SquadConfig cfg)
{
    public const string TagPrefix = "dustlife:";
    readonly Dictionary<int, int> _squadOf = [];
    readonly Dictionary<int, Vector2> _goalOf = [];
    bool _relinked;

    public SquadConfig Cfg { get; } = cfg;

    /// <summary>Bands handed over, bands lost with their squad, squads found again after a load (a rule that never fires is a failed test).</summary>
    public long HandedOver, LostWithSquad, Relinked;

    public IReadOnlyDictionary<int, int> SquadOf => _squadOf;

    public static string TagOf(Band b) => TagPrefix + b.Id.ToString(CultureInfo.InvariantCulture);

    public static int BandOf(string tag) =>
        tag.StartsWith(TagPrefix, StringComparison.Ordinal) && int.TryParse(tag.AsSpan(TagPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            ? id : -1;

    /// <summary>
    /// One sync: find the squads again (first sync after a start or load), follow the handed-over bands' squads, hand over new ones.
    /// </summary>
    public void Sync(World w, ISquadHost host, IReadOnlyList<Vector2> players)
    {
        if (!_relinked) Relink(w, host);
        Follow(w, host);
        HandOver(w, host, players);
    }

    void Relink(World w, ISquadHost host)
    {
        _relinked = true;
        _squadOf.Clear();
        foreach (var (squad, tag) in host.Tagged())
        {
            int band = BandOf(tag);
            if (w.FindBand(band) is { Alive: true, External: true } b) { _squadOf[b.Id] = squad; _goalOf[b.Id] = b.TargetPos; Relinked++; }
        }

        // an external band whose squad the save no longer has was beaten (the game removes a squad whose members all died): never re-place it
        foreach (var b in w.Bands.Where(x => x.Alive && x.External && !_squadOf.ContainsKey(x.Id)).ToList()) Lose(w, b);
    }

    void Follow(World w, ISquadHost host)
    {
        foreach (var (bandId, squad) in _squadOf.ToList())
        {
            if (w.FindBand(bandId) is not { Alive: true } b) { _squadOf.Remove(bandId); continue; }
            if (host.Look(squad) is not { Dead: false } s || s.Size <= 0) { Lose(w, b); continue; }
            b.Size = Math.Min(b.Size, s.Size);
            b.Pos = s.Pos;
            if (_goalOf.GetValueOrDefault(bandId) != b.TargetPos && b.State is BandState.Moving or BandState.Retreat)
            {
                host.Send(squad, b.TargetPos);
                _goalOf[bandId] = b.TargetPos;
            }
        }
    }

    void Lose(World w, Band b)
    {
        _squadOf.Remove(b.Id);
        LostWithSquad++;
        w.Kill(b);
    }

    void HandOver(World w, ISquadHost host, IReadOnlyList<Vector2> players)
    {
        if (players.Count == 0 || _squadOf.Count >= Cfg.MaxSquads) return;
        Band? pick = null;
        float best = float.MaxValue;
        foreach (var b in w.Bands)
        {
            if (!b.Alive || b.External || b.Owner != "warfare" || b.State != BandState.Moving || b.Role is not (BandRole.Invader or BandRole.Patrol)) continue;
            if (!Cfg.Templates.ContainsKey(w.Factions[b.Faction].Key)) continue;
            float d = players.Min(p => Vector2.Distance(p, b.Pos));
            if (d > Cfg.HandOverRadius || d < Cfg.MinRadius || d >= best) continue;
            (best, pick) = (d, b);
        }

        if (pick == null) return;
        var (invader, patrol) = Cfg.Templates[w.Factions[pick.Faction].Key];
        string template = pick.Role == BandRole.Invader ? invader : patrol;
        int squad = host.Spawn(pick.Faction, template, pick.Home, pick.Pos, pick.TargetPos, TagOf(pick));
        if (squad < 0) return;
        pick.External = true;
        _squadOf[pick.Id] = squad;
        _goalOf[pick.Id] = pick.TargetPos;
        HandedOver++;
    }
}
