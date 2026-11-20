// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;

namespace Dustlife.Modules;

/// <summary>
/// A border war beside a faction's main war: Bast between the UC and the Holy Nation, the Hub between the Shek and the Holy Nation. Inside
/// the circle the places of <see cref="Enemies"/> are targets even where the doctrine's main limits would forbid them (<c>Never</c> still
/// holds). A border attack waits while the defenders' power is at least <see cref="WeakBelow"/> (&gt; 0), and at most one goes out per
/// <see cref="EveryHours"/> (&gt; 0) on this front, so the war smoulders instead of sweeping.
/// </summary>
public sealed class Front
{
    public Vector2 Center;
    public float Radius;
    public List<string> Enemies = [];
    public float WeakBelow;
    public float EveryHours;
    /// <summary>Only territory (ruins, hamlets, outposts): never towns, farms or mines.</summary>
    public bool TerritoryOnly;
    /// <summary>Never bases; resources and territory are fair game.</summary>
    public bool NoBases;
    /// <summary>Every target on this front is a border target, even one the main war allows: paced, weak-gated raids.</summary>
    public bool Raids;
    /// <summary>
    /// &gt; 0: raid parties of this many bands (1 or 2), sent every <see cref="EveryHours"/> from the faction's nearest place on or by the
    /// front that keeps one band home, at a weak enemy place on the front: not a base's full invasion, which is sized for a town (the
    /// Hub theatre's falsified first try, RivalryCore#90).
    /// </summary>
    public int PartySize;
}

/// <summary>
/// A faction's war doctrine: lore rules from the game's own dialogue. Places are named as the game names them, factions by key. Every list
/// empty and every number 0 means no rule.
/// </summary>
public sealed class Doctrine
{
    /// <summary>Fronts past this many are ignored (each has its own pace timer).</summary>
    public const int MaxFronts = 4;

    public string Faction = "";
    /// <summary>Only places held (or first held) by these factions are targets.</summary>
    public List<string> Enemies = [];
    /// <summary>Places taken in this order: the first one not yet ours is target 1.</summary>
    public List<string> Order = [];
    /// <summary>Invades nothing but these places.</summary>
    public List<string> OnlyPlaces = [];
    /// <summary>Places never targeted, by invasion or patrol.</summary>
    public List<string> Never = [];
    /// <summary>Places whose garrison never goes below <see cref="HomeKeep"/>.</summary>
    public List<string> Home = [];
    public int HomeKeep;
    /// <summary>Holds and defends; never sends an invasion.</summary>
    public bool NoInvade;
    /// <summary>&gt; 0: invades only while the defenders' settled power is below this.</summary>
    public float WeakBelow;
    /// <summary>A place it goes for when <see cref="CounterFaction"/>'s home is thin.</summary>
    public string CounterTarget = "";
    public string CounterFaction = "";
    /// <summary>The counter faction's bands settled at its bases below this = thin.</summary>
    public int CounterHomeBelow;
    /// <summary>
    /// Stop the main war's invasions once <see cref="EaseFaction"/> holds more than <see cref="EaseAbove"/> places that
    /// <see cref="EaseVictim"/> held at the start. Border wars go on (RivalryCore#33: the reference stopped them too, wider than the lore).
    /// </summary>
    public string EaseFaction = "";
    public string EaseVictim = "";
    public int EaseAbove;
    public List<Front> Fronts = [];
    /// <summary>New bands of this faction fight this much better (1 = as rolled).</summary>
    public float SkillMult = 1;
    /// <summary>&gt; 0: every captain attacks a held place only when the bands he sends have at least this many times the defenders' power.</summary>
    public float OddsAbove;
    /// <summary>&gt; 0: at most one main-war attack on a held place per this many hours, for the whole faction.</summary>
    public float WarEveryHours;
}
