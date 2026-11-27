// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;

namespace Dustlife.Core;

/// <summary>
/// What a place is to the war (the reference's node kinds, after Warfare's smart terrains).
/// </summary>
public enum PlaceKind
{
    /// <summary>A city or military base: raises bands and sends invasions.</summary>
    Base,
    /// <summary>A village, outpost, farm or mine: resource points for its holder.</summary>
    Resource,
    /// <summary>Ruins, points of interest: held, rarely worth much.</summary>
    Territory,
    /// <summary>A nest: fauna's, never held by people.</summary>
    Lair,
}

/// <summary>
/// A place on the map. <see cref="Id"/> is its index in the world; <see cref="Key"/> its stable identity (a Logistics Core node id,
/// <c>town:&lt;InstanceId&gt;</c>, for a placed town).
/// </summary>
public sealed class Place
{
    public int Id = -1;
    public string Key = "";
    public string Name = "";
    /// <summary>Where it stands (X = world X, Y = world Z).</summary>
    public Vector2 Pos;
    public PlaceKind Kind = PlaceKind.Territory;
    /// <summary>Resource points for the holder (0..100).</summary>
    public int Resource;
    /// <summary>How many settled bands the place holds.</summary>
    public int Cap = 1;
    /// <summary>The holding faction's index, -1 for nobody.</summary>
    public int Owner = -1;
    /// <summary>The holder when the world was built.</summary>
    public int OrigOwner = -1;
    /// <summary>Backed by a town in the game (its own people defend it: Warfare's home guard).</summary>
    public bool Real = true;
    public bool PlayerOwned;
    /// <summary>Held by someone outside the simulation (a boss hideout): never a target.</summary>
    public bool Locked;
    /// <summary>False = frozen: no spawns, no targets, its bands sleep.</summary>
    public bool Active = true;
    public double LastFight = -1000;
    /// <summary>Spawn and patrol rate multiplier (1 = normal).</summary>
    public float Activity = 1;
    /// <summary>Attack eagerness multiplier (1 = normal).</summary>
    public float Aggression = 1;
}

/// <summary>
/// A faction the simulation knows. The counters are recomputed every tick.
/// </summary>
public sealed class Faction
{
    public int Id = -1;
    public string Key = "";
    public string Name = "";
    /// <summary>Animals: hostile to people, never hold places.</summary>
    public bool Fauna;
    public bool Slaver;
    public bool Enabled = true;
    /// <summary>0..1.</summary>
    public float Aggression = 0.5f;
    public int Resources, Bases, PlacesHeld, Bands;
    public float Power;
    public int Captures, Lost;
    /// <summary>The war host's leader: the faction's most renowned living captain (a leader id, 0 = none).</summary>
    public int Warlord;
    public string WarlordName = "";
}

/// <summary>
/// A leader's temperament axes (after KVS's personality matrix).
/// </summary>
public enum Axis
{
    Aggression,
    Cunning,
    Patience,
    Sociability,
    Greed,
    Loyalty,
}

/// <summary>
/// A band's leader: a persistent person with a record.
/// </summary>
public sealed class Leader
{
    public const int AxisCount = 6;
    /// <summary>Never reused; 0 = not assigned yet.</summary>
    public int Id;
    public string Name = "";
    /// <summary>A sentence key (<c>ep_scarred</c> ...), earned from battle scars; empty for none.</summary>
    public string Epithet = "";
    public float[] Axes = [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f];
    public int Kills, Battles, Scars, Captures, PlayerWins, PlayerLosses;
    /// <summary>Multiplies combat power; grows with scars (1..2.5).</summary>
    public float Toughness = 1;
    /// <summary>Against the player.</summary>
    public float Grudge;

    public float this[Axis a] { get => Axes[(int)a]; set => Axes[(int)a] = value; }

    /// <summary>
    /// What the faction's war host respects: battles, kills, scars, places taken, wins over the player.
    /// </summary>
    public float Renown => Battles + 0.25f * Kills + 2.0f * Scars + 3.0f * Captures + 5.0f * PlayerWins;
}

/// <summary>What a band is doing.</summary>
public enum BandState
{
    Settled,
    Moving,
    Siege,
    Retreat,
    Dead,
}

/// <summary>What a band is for.</summary>
public enum BandRole
{
    Garrison,
    Invader,
    Patrol,
    Hunt,
    Fauna,
    Traveller,
}

/// <summary>A band's quality (Warfare's novice / advanced / veteran).</summary>
public enum BandTier
{
    Novice,
    Advanced,
    Veteran,
}

/// <summary>
/// A warband: a persistent group with a leader, moving and fighting off screen.
/// </summary>
public sealed class Band
{
    /// <summary>Never reused.</summary>
    public int Id = -1;
    public int Faction = -1;
    public int Size, SizeMax;
    public BandTier Quality;
    /// <summary>0.5..3.</summary>
    public float Skill = 1;
    /// <summary>0..1.</summary>
    public float Morale = 1;
    public float Xp;
    public Leader Leader = new();
    public Vector2 Pos;
    public BandState State = BandState.Settled;
    public BandRole Role = BandRole.Garrison;
    /// <summary>A place id.</summary>
    public int Home = -1;
    /// <summary>A place id, -1 = a free position.</summary>
    public int Target = -1;
    public Vector2 TargetPos;
    public double StayUntil, Born;
    /// <summary>The module that drives it ("warfare", "fauna" ...).</summary>
    public string Owner = "";
    /// <summary>1 = a real squad near the player, 2 = near, 3 = far (thinks one tick in four).</summary>
    public int Tier = 3;
    /// <summary>
    /// The game has the band as a real squad (port step 5c): its position comes from the squad, the core neither moves it nor fights it
    /// off screen, and it arrives when its squad is within <see cref="CoreConfig.ExternalArrival"/> of its goal.
    /// </summary>
    public bool External;

    public bool Alive => State != BandState.Dead && Size > 0;

    public float Power => Alive ? Size * Skill * Leader.Toughness * (0.5f + 0.5f * Morale) : 0;
}

/// <summary>Kinds of world events.</summary>
public enum EventType
{
    Info,
    Spawn,
    Capture,
    Lost,
    Invasion,
    Battle,
    Rout,
    Wiped,
    LeaderDied,
    Scar,
    Slaves,
    Vendetta,
    Hunt,
    Resurgence,
    Escape,
    Rival,
    Warlord,
    /// <summary>The first band of an attacker began a siege of a place (added for the war story, F0; appended so saves keep their numbers).</summary>
    SiegeBegan,
}

/// <summary>
/// Something that happened. <see cref="Key"/> names its sentence (the reference's keys, so its translations carry over) and
/// <see cref="Args"/> fills <c>{1}</c>..<c>{7}</c>; the text is made in the current language when shown.
/// </summary>
public sealed record WorldEvent(long Seq, double Time, EventType Type, int A, int B, int Place, Vector2 Pos, int Importance, string Key,
    IReadOnlyList<string> Args);

/// <summary>
/// The core's numbers. Times are game hours, distances world units (about 10 per metre).
/// </summary>
public sealed class CoreConfig
{
    public float TickHours = 0.25f;
    public int MaxBands = 300;
    public int SizeMin = 3, SizeMax = 10;
    /// <summary>Units per game hour.</summary>
    public float Speed = 3000;
    /// <summary>The "settled at a place" distance.</summary>
    public float PlaceRadius = 2500;
    public float CombatRadius = 1500;
    public int RoundsPerFight = 4;
    public float Lethality = 0.16f;
    public float BreakMorale = 0.35f;
    public float Tier1Radius = 6000, Tier2Radius = 40000;
    /// <summary>Hostile spawns never happen this close to the player.</summary>
    public float NoSpawnRadius = 9000;
    /// <summary>An external band arrives when its squad is this close to its goal.</summary>
    public float ExternalArrival = 1500;
    /// <summary>Every faction hostile to every other.</summary>
    public bool AllOutWar;
    /// <summary>
    /// F1: a war host's leader leads from behind the line (a quarter of the risk of falling in a fight) and, when his band is wiped, rides
    /// out three times in four and takes the strongest band left. Off = the reference's rules.
    /// </summary>
    public bool WarlordsSurvive;
}

/// <summary>
/// Who is hostile to whom, from the game.
/// </summary>
public interface IRelations
{
    bool Hostile(int a, int b);

    bool HostileToPlayer(int faction);
}
