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

    /// <summary>A handed-over band is gone (beaten, or its squad removed), or its spawn was refused after the gate let it go.</summary>
    void Gone(Band band);

    /// <summary>The sync's hand-over is done: let go of what was asked for earlier but not now.</summary>
    void EndSync(World w) { }
}

/// <summary>
/// Dustlife under Rivalry Core's limits (docs/interop.md "Rivalry Core"): at most one hostile party in the player's area at a time, from every
/// mod together, with Rivalry Core's rest between hunts and its queue of who goes next (rivalry.core 0.6.2+, <c>IRivalryState.Propose</c>).
/// A war band of a faction hostile to the player becomes the game's squad near the player only once its candidate wins; its claim is renewed
/// while the squad stays near the player's people and released when it marches away or is gone. A band of a faction not hostile to the
/// player passes by and needs nothing. Rivalry Core is an optional dependency read by reflection (its entry through
/// <c>ModContext.Dependency&lt;object&gt;</c>), so Dustlife builds and runs without it; Rivalry Core 0.6.0 and 0.6.1 have no queue, and
/// their <c>ClaimParty</c> is used instead. Nothing throws: a call that fails counts and lets the band wait.
/// <para>
/// A queue win reaches Dustlife only when it proposes the same band again, so a band proposed earlier but not now (a nearer band took the
/// pick, it left the ring, the cap was reached) is released for <see cref="StaleHours"/>, and a refused spawn releases its win at once
/// (since 0.6.6 such an unconfirmed win starts no rest). A hostile squad near the player that holds no claim (after a load, or back after
/// marching away) claims again.
/// </para>
/// </summary>
public sealed class RivalryLink(object? api, Func<int, int> gameFaction, Func<int, bool> hostileToPlayer) : IHandOverGate
{
    public const string Source = "pza.dustlife.war";
    /// <summary>A claim lapses this many game hours after it was made or renewed (renewed at every sync while near).</summary>
    public const double ClaimHours = 6;

    /// <summary>
    /// A band proposed but not proposed again is released for this many game hours: longer than Rivalry Core keeps another mod's candidate
    /// in its queue (2 × its assessment interval, 4 h by default).
    /// </summary>
    public const double StaleHours = 6;

    /// <summary>The grudge a band is proposed with when Rivalry Core's <c>HuntThreshold</c> can't be read (its default).</summary>
    public const float DefaultGrudge = 60;

    object? _state;
    MethodInfo? _in, _propose, _claim, _release, _partiesOut;
    PropertyInfo? _restUntil;
    Type? _candidate, _claimType, _kind;
    readonly HashSet<int> _claimed = [];
    readonly HashSet<int> _proposedNow = [];
    readonly Dictionary<int, double> _proposedAt = [];

    /// <summary>Counters for the daily log line.</summary>
    public long Proposed, Won, Waited, Renewed, Released, Reclaimed, Dropped, Failed;

    /// <summary>
    /// The grudge a band is proposed with: Rivalry Core's <c>HuntThreshold</c>, the least grudge a hunt needs, so a war band near the player
    /// ranks like the weakest grudge hunt and the hours it has waited break ties. Its travel is 0: it is already within reach.
    /// </summary>
    public float Grudge { get; private set; } = DefaultGrudge;

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
            if (_in == null && Find().Count > 0) return false;

            _state = _in!.Invoke(api, [world]);
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

    /// <summary>
    /// Looks up Rivalry Core's members by name: the members it lacks (empty when linked). <c>Propose</c> and <c>HostileRestUntil</c> are
    /// optional (0.6.2 and 0.6.1); the rest is needed.
    /// </summary>
    public IReadOnlyList<string> Find()
    {
        var missing = new List<string>();
        var face = api?.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == "Rivalry.IRivalryApi");
        var asm = face?.Assembly;
        var stateFace = asm?.GetType("Rivalry.IRivalryState");
        _candidate = asm?.GetType("Rivalry.HostileCandidate");
        _claimType = asm?.GetType("Rivalry.PartyClaim");
        _kind = asm?.GetType("Rivalry.PartyKind");
        if (face == null || stateFace == null || _claimType == null || _kind == null)
        {
            missing.Add("Rivalry.IRivalryApi, IRivalryState, PartyClaim or PartyKind");
            return missing;
        }

        Need(missing, "IRivalryApi.In", face.GetMethod("In"));
        Need(missing, "IRivalryState.ClaimParty", _claim = stateFace.GetMethod("ClaimParty", [_claimType]));
        Need(missing, "IRivalryState.ReleaseParty", _release = stateFace.GetMethod("ReleaseParty", [typeof(string), typeof(string)]));
        Need(missing, "IRivalryState.PartiesOut", _partiesOut = stateFace.GetMethod("PartiesOut", [_kind]));
        Need(missing, "PartyClaim(string, PartyKind, string, double, int)",
            _claimType.GetConstructor([typeof(string), _kind, typeof(string), typeof(double), typeof(int)]));
        _propose = _candidate == null ? null : stateFace.GetMethod("Propose", [_candidate]);
        if (_propose != null) Need(missing, "HostileCandidate(string, string, int, float, double, double)", CandidateConstructor());
        _restUntil = stateFace.GetProperty("HostileRestUntil");
        Grudge = ReadGrudge(face);
        if (missing.Count == 0) _in = face.GetMethod("In");
        return missing;
    }

    static void Need(List<string> missing, string name, object? found)
    {
        if (found == null) missing.Add(name);
    }

    ConstructorInfo? CandidateConstructor() =>
        _candidate!.GetConstructor([typeof(string), typeof(string), typeof(int), typeof(float), typeof(double), typeof(double)]);

    /// <summary>Rivalry Core's <c>Options.HuntThreshold</c>; <see cref="DefaultGrudge"/> when it can't be read.</summary>
    float ReadGrudge(Type face)
    {
        try
        {
            object? options = face.GetProperty("Options")?.GetValue(api);
            return options?.GetType().GetProperty("HuntThreshold")?.GetValue(options) is float f && float.IsFinite(f) ? f : DefaultGrudge;
        }
        catch (Exception)
        {
            return DefaultGrudge;
        }
    }

    object Hostile() => Enum.Parse(_kind!, "Hostile");

    static string Key(Band b) => Key(b.Id);

    static string Key(int band) => "band:" + band.ToString(CultureInfo.InvariantCulture);

    bool Concerns(Band b) => hostileToPlayer(b.Faction);

    public bool MayHandOver(World w, Band band)
    {
        if (!Concerns(band)) return true;                      // passing by: not aimed at the player
        if (_state == null) return true;                       // Rivalry Core absent: Dustlife's own cap only
        try
        {
            Proposed++;
            _proposedNow.Add(band.Id);
            _proposedAt[band.Id] = w.Now;
            bool go = _propose != null
                ? (bool)_propose.Invoke(_state, [Candidate(band)])!
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

    object Candidate(Band band) =>
        CandidateConstructor()!.Invoke([Source, Key(band), gameFaction(band.Faction), Grudge, 0.0, ClaimHours]);

    object Claim(Band band) => Activator.CreateInstance(_claimType!, Source, Hostile(), Key(band), ClaimHours, gameFaction(band.Faction))!;

    public void InGame(World w, Band band, bool near)
    {
        if (_state == null || !Concerns(band)) return;
        try
        {
            if (near) Hold(band);
            else if (_claimed.Contains(band.Id)) Release(band);    // marched away from the player: no longer a party in their area
        }
        catch (Exception)
        {
            Failed++;
        }
    }

    /// <summary>
    /// Renews a near squad's claim, or claims again for one that holds none (after a load, Dustlife's list is empty while Rivalry Core's saved
    /// claim still runs; or it marched away and came back). A refused claim (the cap, the rest) is asked again at the next sync.
    /// </summary>
    void Hold(Band band)
    {
        bool known = _claimed.Contains(band.Id);
        if (!(bool)_claim!.Invoke(_state, [Claim(band)])!) return;

        if (known) Renewed++;
        else
        {
            _claimed.Add(band.Id);
            Reclaimed++;
        }
    }

    public void Gone(Band band)
    {
        if (_state == null || !Concerns(band)) return;
        try
        {
            Release(band);                                     // also a claim saved by Rivalry Core that Dustlife's list lost in a load
        }
        catch (Exception)
        {
            Failed++;
        }
    }

    public void EndSync(World w)
    {
        if (_state == null) return;
        try
        {
            DropStale(w.Now);
        }
        catch (Exception)
        {
            Failed++;
        }
        finally
        {
            _proposedNow.Clear();
        }
    }

    /// <summary>
    /// Releases the bands proposed in the last <see cref="StaleHours"/> but not in this sync and not handed over: a queue win Dustlife won't
    /// pick up would otherwise hold the hostile slot.
    /// </summary>
    void DropStale(double now)
    {
        foreach (var (id, at) in _proposedAt.ToList())
        {
            if (_proposedNow.Contains(id) || _claimed.Contains(id)) continue;
            if (now - at > StaleHours)
            {
                _proposedAt.Remove(id);
                continue;
            }

            if ((bool)_release!.Invoke(_state, [Source, Key(id)])!) Dropped++;
        }
    }

    void Release(Band band)
    {
        _claimed.Remove(band.Id);
        if ((bool)_release!.Invoke(_state, [Source, Key(band)])!) Released++;
    }

    /// <summary>One line for the log.</summary>
    public string Summary(double nowHours)
    {
        if (api == null) return "Rivalry Core: not loaded (Dustlife's own squad cap only)";
        if (!Linked) return "Rivalry Core: loaded, but its API was not found";
        string rest = double.IsNaN(RestUntil) || RestUntil <= nowHours ? "not resting" : FormattableString.Invariant($"resting {RestUntil - nowHours:0.#} h more");
        string state = FormattableString.Invariant($"linked ({(HasQueue ? "queue" : "claims only")}), {HostileOut} hostile parties out, {rest}");
        string asked = FormattableString.Invariant($"Dustlife proposed {Proposed}, {Won} went, {Waited} waited, {Holding} holding a claim");
        string kept = FormattableString.Invariant(
            $"{Renewed} renewals, {Reclaimed} claimed again, {Released} released, {Dropped} dropped wins, {Failed} failed");
        return $"Rivalry Core: {state}; {asked}, {kept}";
    }
}
