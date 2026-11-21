// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Dustlife;

/// <summary>
/// Dustlife's world system, one per game world (each new game and load builds it afresh), at the end of the tick (port step 5). It builds
/// the war from the game's own towns, factions and roads, runs it on the game's clock off screen, follows the game's town changes, keeps
/// it in the save, writes the war's news to the log, and hands marching bands near the player to the game as real squads (5c).
/// </summary>
public sealed class DustlifeSystem(PopulationData data, PopulationSystem? population, DustlifeSettings settings, Action<string> log)
    : ITickSystem, IModSaveData
{
    Squads? _squads;
    EngineSquads? _host;
    Core.World? _war;
    /// <summary>A new world clears the API at once, Dustlife on or off (a disabled world must not show the last world's war).</summary>
    readonly long _generation = DustlifeApi.NewWorld();
    GameRelations? _rel;
    readonly TownWatch _watch = new();
    readonly WastelandLink _twr = new();
    bool _twrSaid;
    long _newsSeq;
    byte[]? _pendingSave;
    bool _failed;

    /// <summary>
    /// The war, once built (null before the first tick).
    /// </summary>
    public Core.World? War => _war;

    public void SlowWorld(Meitou.Simulation.World world)
    {
        if (!settings.Enabled || _failed) return;
        if (_war == null) Start(world);
        if (_war == null || _rel == null) return;

        var towns = GameSource.Versions(data).ToList();
        _watch.Poll(_war, towns, _rel);
        _war.PlayerPos.Clear();
        _war.PlayerPos.AddRange(GameSource.PlayerPositions(world, data));
        int steps = _war.Advance(world.GameHours, _rel);
        if (steps > 0 && _squads != null && _host != null) _squads.Sync(_war, _host, _war.PlayerPos);
        News();
    }

    /// <summary>
    /// Builds the war from the game's data: from the save when the load gave one, else a new war seeded from the world's seed.
    /// </summary>
    void Start(Meitou.Simulation.World world)
    {
        DustlifeApi.Attach(null);
        try
        {
            (_war, _rel) = GameSource.Build(data, settings);
            bool loaded = false;
            if (_pendingSave != null)
            {
                try
                {
                    var map = WorldSave.Read(_war, new MemoryStream(_pendingSave));
                    loaded = true;
                    log($"war loaded: {_war.Bands.Count} bands, {map.MissingPlaces} saved places and {map.MissingFactions} factions no longer in the world");
                }
                catch (InvalidDataException e)
                {
                    log($"the save's war could not be read ({e.Message}): a new war starts");
                    (_war, _rel) = GameSource.Build(data, settings);
                }

                _pendingSave = null;
            }

            if (!loaded)
            {
                _war.Rng.State = world.Seed == 0 ? 0x9E3779B97F4A7C15UL : world.Seed ^ 0xD057_11FE_0000_0001UL;
                _war.StartNew();
                _war.Advance(world.GameHours, _rel);                         // the clock starts at the game's hour
                log($"a new war: {_war.Factions.Count} factions, {_war.Places.Count} places, {_war.Roads?.All.Count ?? 0} roads, {_war.Bands.Count} bands");
            }

            _watch.Prime(GameSource.Versions(data));
            DustlifeApi.Attach(_war);
            if (settings.RealSquads && population != null)
            {
                _squads = new Squads(Lore.Squads());
                _host = new EngineSquads(world, population, _rel.GameIndex, GameSource.SiteOfPlace(data, _war));
            }
            _newsSeq = _war.Events.Count == 0 ? 0 : _war.Events[^1].Seq;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"Dustlife could not start and is off for this world: {e.Message}");
            _war = null;
            _failed = true;
            DustlifeApi.Attach(null);
        }
    }

    void News()
    {
        if (_war == null) return;
        List<WorldEvent>? fresh = null;
        foreach (var e in _war.Events)
        {
            if (e.Seq <= _newsSeq) continue;
            _newsSeq = e.Seq;
            if (settings.NewsInLog && e.Importance >= 2) log(_war.Say(e));
            (fresh ??= []).Add(e);
        }

        if (fresh == null || !settings.WastelandNews) return;
        _twr.Forward(_war, fresh);
        if (_twr.Bound && !_twrSaid)
        {
            _twrSaid = true;
            log("war news goes to The Wasteland Remembers' news board");
        }
    }

    public void Hash(ref StateHasher hasher)
    {
        if (_war == null) return;
        hasher.Add(BitConverter.DoubleToInt64Bits(_war.Now));
        hasher.Add(_war.Rng.State);
        hasher.Add(_war.NameRng.State);
        hasher.Add(_war.Bands.Count);
        foreach (var p in _war.Places) hasher.Add(p.Owner);
    }

    public void Write(Stream stream)
    {
        if (_war == null) return;
        WorldSave.Write(_war, stream);
    }

    /// <summary>
    /// Kept until the first tick, when the game's data is ready to build the war it loads into.
    /// </summary>
    public void Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        _pendingSave = ms.ToArray();
    }
}

/// <summary>
/// The mod's settings (Settings, Mods page). Read when a world starts: a change takes effect at the next new game or load.
/// </summary>
public sealed class DustlifeSettings
{
    [Setting("Dustlife war", Hint = "The factions' war goes on off screen.")]
    public bool Enabled { get; set; } = true;

    [Setting("Reasons for war", Hint = "Factions take places only for a reason (hunger, revenge, their creed...) and the news says which.")]
    public bool Motives { get; set; } = true;

    [Setting("Cut roads", Hint = "A place whose road home an enemy blocks produces nothing for its faction.")]
    public bool CutRoads { get; set; } = true;

    [Setting("Real squads near you", Hint = "A war band marching near your people becomes the game's own squad of its faction.")]
    public bool RealSquads { get; set; } = true;

    [Setting("Hub theatre", Hint = "The Shek and the Holy Nation raid each other's mines, farms and ruins round the Hub (a test: off by default).")]
    public bool HubTheatre { get; set; }

    [Setting("War news for The Wasteland Remembers", Hint = "With The Wasteland Remembers installed, towns near a fight talk about it: places taken, sieges given up, marches and raids.")]
    public bool WastelandNews { get; set; } = true;

    [Setting("War news in the log", Hint = "Writes the war's bigger events to the game's log.")]
    public bool NewsInLog { get; set; } = true;
}
