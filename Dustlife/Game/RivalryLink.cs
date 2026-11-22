// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Globalization;
using System.Reflection;
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>
/// Whether a band may become the game's squad now, and what to tell the shared limits while it is one (<see cref="Squads"/>).
/// </summary>
public interface IHandOverGate
{
    /// <summary>Asks to hand <paramref name="band"/> over now; false: it waits (asked again at the next sync).</summary>
    bool MayHandOver(World w, Band band);

    /// <summary>A handed-over band's squad is still in the game; <paramref name="near"/>: within reach of the player's people.</summary>
    void InGame(World w, Band band, bool near);

    /// <summary>A handed-over band is gone (beaten, or its squad removed).</summary>
    void Gone(Band band);
}

/// <summary>
/// Dustlife under Rivalry Core's limits (docs/interop.md "Rivalry Core"): at most one hostile party in the player's area at a time, from every
/// mod together, with Rivalry Core's rest between hunts and its queue of who goes next (rivalry.core 0.6.2+, <c>IRivalryState.Propose</c>).
/// A war band of a faction hostile to the player becomes the game's squad near the player only once its candidate wins; its claim is renewed
/// while the squad stays near the player's people and released when it marches away or is gone. A band of a faction not hostile to the
/// player passes by and needs nothing. Rivalry Core is an optional dependency read by reflection (its entry through
/// <c>ModContext.Dependency&lt;object&gt;</c>), so Dustlife builds and runs without it; Rivalry Core 0.6.0 and 0.6.1 have no queue, and
/// their <c>ClaimParty</c> is used instead. Nothing throws: a call that fails counts and lets the band wait.
/// </summary>
public sealed class RivalryLink(object? api, Func<int, int> gameFaction, Func<int, bool> hostileToPlayer) : IHandOverGate
{
    public const string Source = "pza.dustlife.war";
    /// <summary>A claim lapses this many game hours after it was made or renewed (renewed at every sync while near).</summary>
    public const double ClaimHours = 6;

    object? _state;
    MethodInfo? _in, _propose, _claim, _release, _partiesOut;
    PropertyInfo? _restUntil;
    Type? _candidate, _claimType, _kind;
    readonly HashSet<int> _claimed = [];

    /// <summary>Counters for the daily log line.</summary>
    public long Proposed, Won, Waited, Renewed, Released, Failed;

    /// <summary>Whether Rivalry Core is loaded and its API found.</summary>
    public bool Linked => _in != null;

    /// <summary>Whether its queue is there (0.6.2+), else claims straight away.</summary>
    public bool HasQueue => _propose != null;

    /// <summary>Hostile parties out now from every mod, or -1 unknown.</summary>
    public int HostileOut { get; private set; } = -1;

    /// <summary>The game hour Rivalry Core's rest ends (NaN unknown).</summary>
    public double RestUntil { get; private set; } = double.NaN;

    /// <summary>The bands holding a claim now.</summary>
    public int Holding => _claimed.Count;

    /// <summary>Binds to the world's Rivalry state once per sync (serial step only); false when Rivalry Core is absent or does not run here.</summary>
    public bool Bind(object world)
    {
        if (api == null) return false;
        try
        {
            if (_in == null)
            {
                var face = api.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == "Rivalry.IRivalryApi");
                _in = face?.GetMethod("In");
                if (_in == null) return false;
                var asm = face!.Assembly;
                _candidate = asm.GetType("Rivalry.HostileCandidate");
                _claimType = asm.GetType("Rivalry.PartyClaim");
                _kind = asm.GetType("Rivalry.PartyKind");
                var stateFace = asm.GetType("Rivalry.IRivalryState");
                if (stateFace == null || _claimType == null || _kind == null) { _in = null; return false; }
                _propose = _candidate == null ? null : stateFace.GetMethod("Propose", [_candidate]);
                _claim = stateFace.GetMethod("ClaimParty", [_claimType]);
                _release = stateFace.GetMethod("ReleaseParty", [typeof(string), typeof(string)]);
                _partiesOut = stateFace.GetMethod("PartiesOut", [_kind]);
                _restUntil = stateFace.GetProperty("HostileRestUntil");
            }

            _state = _in.Invoke(api, [world]);
            if (_state == null) return false;
            HostileOut = _partiesOut?.Invoke(_state, [Hostile()]) is int n ? n : -1;
            RestUntil = _restUntil?.GetValue(_state) is double r ? r : double.NaN;
            return true;
        }
        catch (Exception)
        {
            Failed++;
            _state = null;
            return false;
        }
    }

    object Hostile() => Enum.Parse(_kind!, "Hostile");

    static string Key(Band b) => "band:" + b.Id.ToString(CultureInfo.InvariantCulture);

    bool Concerns(Band b) => hostileToPlayer(b.Faction);

    public bool MayHandOver(World w, Band band)
    {
        if (!Concerns(band)) return true;                      // passing by: not aimed at the player
        if (_state == null) return true;                       // Rivalry Core absent: Dustlife's own cap only
        try
        {
            Proposed++;
            bool go = _propose != null
                ? (bool)_propose.Invoke(_state, [Activator.CreateInstance(_candidate!, Source, Key(band), gameFaction(band.Faction), 0f, 0.0, ClaimHours)!])!
                : (bool)_claim!.Invoke(_state, [Claim(band)])!;
            if (go)
            {
                Won++;
                _claimed.Add(band.Id);
            }
            else Waited++;
            return go;
        }
        catch (Exception)
        {
            Failed++;
            return false;
        }
    }

    object Claim(Band band) => Activator.CreateInstance(_claimType!, Source, Hostile(), Key(band), ClaimHours, gameFaction(band.Faction))!;

    public void InGame(World w, Band band, bool near)
    {
        if (_state == null || !_claimed.Contains(band.Id)) return;
        try
        {
            if (near)
            {
                _claim!.Invoke(_state, [Claim(band)]);
                Renewed++;
            }
            else Release(band);                                // marched away from the player: no longer a party in their area
        }
        catch (Exception)
        {
            Failed++;
        }
    }

    public void Gone(Band band)
    {
        if (_state == null || !_claimed.Contains(band.Id)) return;
        try
        {
            Release(band);
        }
        catch (Exception)
        {
            Failed++;
        }
    }

    void Release(Band band)
    {
        _claimed.Remove(band.Id);
        _release!.Invoke(_state, [Source, Key(band)]);
        Released++;
    }

    /// <summary>One line for the log.</summary>
    public string Summary(double nowHours)
    {
        if (api == null) return "Rivalry Core: not loaded (Dustlife's own squad cap only)";
        if (!Linked) return "Rivalry Core: loaded, but its API was not found";
        string rest = double.IsNaN(RestUntil) || RestUntil <= nowHours ? "not resting" : FormattableString.Invariant($"resting {RestUntil - nowHours:0.#} h more");
        return FormattableString.Invariant(
            $"Rivalry Core: linked ({(HasQueue ? "queue" : "claims only")}), {HostileOut} hostile parties out, {rest}; Dustlife proposed {Proposed}, {Won} went, {Waited} waited, {Holding} holding a claim, {Renewed} renewals, {Released} released, {Failed} failed");
    }
}
