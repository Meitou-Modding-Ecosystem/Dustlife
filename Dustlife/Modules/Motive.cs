// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
namespace Dustlife.Modules;

/// <summary>
/// Why a faction wants a place. Warfare records it when the place becomes a target and reports it when the place is taken, so every
/// taking can say why it happened (and a balance run can count the takings that had no reason).
/// </summary>
public enum Motive
{
    /// <summary>No reason: the reference's plain score roll.</summary>
    None,
    /// <summary>The faction's doctrine names the place (its order of conquest).</summary>
    Doctrine,
    /// <summary>The doctrine's counter-strike: the enemy's home is thin.</summary>
    Counter,
    /// <summary>The place's holders had marched on this base.</summary>
    Retaliation,
    /// <summary>Short of food: a farm or worked land.</summary>
    Food,
    /// <summary>Short of arms: a base with forges.</summary>
    Arms,
    /// <summary>The place cuts the road between the faction's bases and some of its places (cut roads).</summary>
    Lifeline,
    /// <summary>The faction lost the place lately, or held it from the start.</summary>
    Grievance,
    /// <summary>A hostile neighbour's place held far too thinly.</summary>
    Opportunity,
    /// <summary>Empty land with something to work, close to home.</summary>
    Settle,
    /// <summary>A patrol rested on empty land and stayed.</summary>
    Patrol,
}

/// <summary>
/// The motive rules' numbers. They add to a place's priority in Warfare's target score (a place in the same region gets +100; a base is
/// worth 10, a farm 5 per resource point), so a weight near 100 can outweigh distance within a region.
/// </summary>
public sealed class MotiveConfig
{
    /// <summary>A faction's pool is short of food below this many days of upkeep (full need at 0 days).</summary>
    public float NeedDays = 8;
    /// <summary>Arms are short below this many middle-sized bands' worth (full need at none).</summary>
    public float NeedBands = 4;
    public float NeedWeight = 150;
    /// <summary>A place lost within this many hours is a grievance at full weight; a place held from the start, at the homeland weight.</summary>
    public float GrievanceHours = 24 * 21;
    public float GrievanceWeight = 80, HomelandWeight = 40;
    public float RetaliationWeight = 50;
    /// <summary>A place that cuts the faction's road home (only with the economy's cut roads and the world's roads).</summary>
    public float LifelineWeight = 120;
    /// <summary>An enemy place is an opportunity when this base's settled power is at least this many times what holds it.</summary>
    public float OpportunityRatio = 2;
    public float OpportunityWeight = 30;
    public float SettleWeight = 20;
    /// <summary>Warfare's expansion picks only places it has a motive for (doctrine targets always have one).</summary>
    public bool RequireMotive = true;
}
