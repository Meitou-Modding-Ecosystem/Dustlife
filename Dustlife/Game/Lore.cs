// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Modules;

namespace Dustlife.Game;

/// <summary>
/// The lore set-up (the reference's <c>--rules lore</c> with its tuned defaults), shared by the mod and the balance runner so both play the
/// same war: the five factions of the Holy Nation's border war, their doctrines, and the economy's profiles.
/// </summary>
public static class Lore
{
    public const string HolyNation = "1083-gamedata.base", UnitedCities = "defaultEmpireFactionSID", Shek = "11624-Dialogue (10).mod";
    public const string SlaveTraders = "1084-gamedata.base", TradersGuild = "1088-gamedata.base";

    /// <summary>Factions that take prisoners (Slavery).</summary>
    public static readonly string[] Slavers = [SlaveTraders];

    /// <summary>The Bast front: the Holy Nation and the UC fight over its territory.</summary>
    public static readonly Vector2 BastCenter = new(8000, -88000);

    public const float BastRadius = 25000, UcWeakBelow = 17;

    /// <summary>The Hub theatre: the Border Zone crossroads between Stack (16.8 km) and Squin (17.7 km).</summary>
    public static readonly Vector2 HubCenter = new(-50900, 2900);

    public const float HubRadius = 35000;

    /// <summary>
    /// The Hub theatre's raid fronts for the Shek and the Holy Nation (RivalryCore#90): both raid each other's mines, farms and ruins round
    /// the Hub with parties of two, at most every 96 h, never a town (Stack stays the main war's prize).
    /// </summary>
    public static Front HubFront(string enemy) => new()
    {
        Center = HubCenter, Radius = HubRadius, Enemies = [enemy], WeakBelow = 17, EveryHours = 96, NoBases = true, Raids = true, PartySize = 2,
    };

    /// <summary>
    /// The war squads a band becomes when the game takes it over (base game templates by name: a faction's own <c>squads</c> list holds its
    /// civilians, not its armies).
    /// </summary>
    public static SquadConfig Squads() => new()
    {
        Templates = new()
        {
            [HolyNation] = ("55666-rebirth.mod", "1406-gamedata.base"),       // Holy Army force, Holy Army Patrol
            [UnitedCities] = ("1127-gamedata.base", "64864-rebirth.mod"),     // Empire army, Empire army small patrol
            [Shek] = ("64238-Dialogue.mod", "51321-rebirth.mod"),             // Shek base attack medium, Shek Patrol
        },
    };

    /// <summary>The game's own band speed for the core: walk speed 45 x 109.1 real seconds per game hour.</summary>
    public const float GameSpeed = 4909;

    /// <summary>
    /// The war's settings. <paramref name="motives"/> adds the motive rules (off: the reference's targets).
    /// </summary>
    public static WarfareConfig War(bool motives, bool hub = false)
    {
        var cfg = BaseWar(motives);
        if (!hub) return cfg;
        cfg.Doctrines.First(d => d.Faction == Shek).Fronts.Add(HubFront(HolyNation));
        cfg.Doctrines.First(d => d.Faction == HolyNation).Fronts.Add(HubFront(Shek));
        return cfg;
    }

    static WarfareConfig BaseWar(bool motives) => new()
    {
        // the reference's lore set-up also keeps an unguarded place with its holder until it is besieged undefended (claimsHold)
        FactionKeys = [HolyNation, UnitedCities, Shek, SlaveTraders, TradersGuild], MusterPatienceHours = 72, UseEconomy = true, ClaimsHold = true,
        Doctrines = Doctrines(), Motives = motives ? new MotiveConfig() : null,
    };

    public static EconomyConfig Economy(bool cutRoads) => new()
    {
        CutRoads = cutRoads,
        Profiles =
        [
            new() { Faction = Shek, FoodMult = 1.0f, ArmsMult = 2.0f },
            new() { Faction = HolyNation, FoodMult = 1.3f },
            new() { Faction = SlaveTraders, FoodMult = 1.2f, PoolOf = UnitedCities },
            new() { Faction = TradersGuild, PoolOf = UnitedCities },
        ],
    };

    /// <summary>
    /// The lore rules as doctrines (the Shek odds 1.3 and skill 1.25, the Bast fronts paced at 96 h with a weakness bar of 17, territory
    /// only).
    /// </summary>
    public static List<Doctrine> Doctrines()
    {
        var shek = new Doctrine
        {
            Faction = Shek, Enemies = [HolyNation], Order = ["Stack", "Okran's Shield", "Blister Hill"], Home = ["Admag", "Squin"], HomeKeep = 2,
            OddsAbove = 1.3f, SkillMult = 1.25f,
        };
        var hn = new Doctrine
        {
            Faction = HolyNation, Enemies = [Shek], WeakBelow = UcWeakBelow, CounterTarget = "Admag", CounterFaction = Shek, CounterHomeBelow = 1,
            Fronts = [new Front { Center = BastCenter, Radius = BastRadius, Enemies = [UnitedCities], WeakBelow = 17, EveryHours = 96, TerritoryOnly = true }],
        };
        var uc = new Doctrine
        {
            Faction = UnitedCities, OnlyPlaces = ["Okran's Shield"], Order = ["Okran's Shield"], Never = ["Blister Hill"], WeakBelow = UcWeakBelow,
            EaseFaction = Shek, EaseVictim = HolyNation, EaseAbove = 4,
            Fronts = [new Front { Center = BastCenter, Radius = BastRadius, Enemies = [HolyNation], WeakBelow = 17, EveryHours = 96, TerritoryOnly = true }],
        };
        return [shek, hn, uc, new Doctrine { Faction = SlaveTraders, NoInvade = true }, new Doctrine { Faction = TradersGuild, NoInvade = true }];
    }
}
