// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>A stand-in for TWR's outlaw surface (WastelandApi 4: <c>Outlaws</c>, <c>OutlawAt</c>, <c>HideoutStruck</c>).</summary>
public static class TwrOutlaws
{
    public const int Version = 4;
    public static readonly List<string> Struck = [];
    public static readonly Dictionary<string, object> Row = [];
    public static Vector2 Camp;
    public static bool Answer = true;

    public static void Reset()
    {
        Struck.Clear();
        Answer = true;
        Camp = new Vector2(1000, 0);
        Row.Clear();
        foreach (var (k, v) in new (string, object)[] { ("Name", "Dolan Pike"), ("Faction", "Dust Bandits"), ("Status", "alive"), ("Successor", ""), ("Mood", "raiding"), ("Known", true) })
            Row[k] = v;
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object>> Outlaws() => [new Dictionary<string, object>(Row)];

    public static string OutlawAt(float x, float z, float within) => Vector2.Distance(new Vector2(x, z), Camp) <= within ? (string)Row["Name"] : "";

    public static bool HideoutStruck(string name, string by, bool bloodied, string source)
    {
        Struck.Add($"{name}|{by}|{bloodied}|{source}");
        return Answer;
    }
}

/// <summary>
/// The interim bridge (RivalryCore#59, #126): a war band on its own ground scatters a named outlaw's camp it walks onto, within the limits TWR's
/// author set: only outlaws the squad has heard of, never in their first 7 days (or before TWR's own <c>StrikableAfter</c>), at most once per
/// 30 days, never a wounded or downed one, and TWR's "not now" respected.
/// </summary>
[Collection("twr")]
public sealed class OutlawLinkTests
{
    static (World W, Band Band) Patrol()
    {
        TwrOutlaws.Reset();
        s_now = 0;
        var w = new World();
        int hn = w.AddFaction(Lore.HolyNation, "Holy Nation", false, false);
        w.AddFaction(Lore.Shek, "Shek", false, false);
        w.AddPlace("stack", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false }));
        w.StartNew();
        var b = w.FindBand(w.SpawnBand(hn, 0, BandRole.Patrol, new Vector2(900, 0), "warfare"))!;
        return (w, b);
    }

    static OutlawLink Link() => new(() => typeof(TwrOutlaws));

    static double s_now;

    static void Hours(World w, OutlawLink link, double hours, List<string>? said = null)
    {
        for (double h = 0; h < hours; h++)
        {
            s_now += 1;
            if (link.Step(w, s_now) is { } line) said?.Add(line);
        }
    }

    [Fact]
    public void A_known_outlaw_is_struck_only_after_a_week_then_once_a_month()
    {
        var (w, _) = Patrol();
        var link = Link();
        var said = new List<string>();
        Hours(w, link, 7 * 24 - 2, said);
        Assert.Empty(TwrOutlaws.Struck);                                                  // its first week: left alone
        Assert.True(link.TooYoung > 0);

        Hours(w, link, 4, said);
        Assert.Equal(["Dolan Pike|Holy Nation|False|pza.dustlife"], TwrOutlaws.Struck);   // a patrol: not bloodied
        Assert.Contains("struck Dolan Pike's camp", said.Single());

        Hours(w, link, 29 * 24, said);
        Assert.Single(TwrOutlaws.Struck);                                                 // once in 30 days
        Hours(w, link, 2 * 24, said);
        Assert.Equal(2, TwrOutlaws.Struck.Count);
        Assert.Contains("2 struck", link.Summary());
    }

    [Fact]
    public void Never_one_the_squad_has_not_heard_of_nor_a_wounded_one()
    {
        var (w, _) = Patrol();
        var link = Link();
        TwrOutlaws.Row["Known"] = false;
        Hours(w, link, 10 * 24);
        Assert.Empty(TwrOutlaws.Struck);
        Assert.True(link.Unknown > 0);

        TwrOutlaws.Row["Known"] = true;
        TwrOutlaws.Row["Mood"] = "wounded";
        Hours(w, link, 2 * 24);
        Assert.Empty(TwrOutlaws.Struck);
        Assert.True(link.Down > 0);
    }

    [Fact]
    public void A_new_boss_gets_a_new_week()
    {
        var (w, _) = Patrol();
        var link = Link();
        Hours(w, link, 8 * 24);
        Assert.Single(TwrOutlaws.Struck);
        TwrOutlaws.Row["Status"] = "succeeded";
        TwrOutlaws.Row["Successor"] = "Scrag Pike";
        Hours(w, link, 31 * 24);                                                          // the month is over, but Scrag's first week ...
        Assert.Equal(2, TwrOutlaws.Struck.Count);                                         // ... ended on day 15: struck again on day 38
    }

    [Fact]
    public void TWRs_own_clock_wins_when_it_has_one()
    {
        var (w, _) = Patrol();
        var link = Link();
        TwrOutlaws.Row["StrikableAfter"] = 3.0 * 24;                                       // API 5: TWR says day 3
        Hours(w, link, 4 * 24);
        Assert.Single(TwrOutlaws.Struck);
    }

    [Fact]
    public void Not_now_waits_a_day_and_away_from_home_never()
    {
        var (w, band) = Patrol();
        var link = Link();
        TwrOutlaws.Answer = false;
        Hours(w, link, 8 * 24);
        Assert.InRange(TwrOutlaws.Struck.Count, 1, 2);                                    // asked, refused, asked again no sooner than a day
        Assert.True(link.NotNow >= 1);

        TwrOutlaws.Reset();
        var (w2, b2) = Patrol();
        w2.Places[0].Owner = 1;                                                           // Stack is the Shek's now: not the patrol's ground
        var link2 = Link();
        Hours(w2, link2, 10 * 24);
        Assert.Empty(TwrOutlaws.Struck);
        Assert.True(link2.NotOwnGround > 0);
    }

    [Fact]
    public void The_strike_clock_survives_a_save()
    {
        var (w, _) = Patrol();
        var link = Link();
        Hours(w, link, 8 * 24);
        Assert.Single(TwrOutlaws.Struck);
        using var ms = new MemoryStream();
        link.Write(ms);
        ms.Position = 0;
        var loaded = Link();
        Assert.Equal(2, loaded.Read(ms));                                                  // the strike and the first sighting
        Hours(w, loaded, 5 * 24);
        Assert.Single(TwrOutlaws.Struck);                                                 // no second strike early after a load

        Assert.Equal(0, Link().Read(new MemoryStream([1, 2, 3])));                        // a save from before the bridge: nothing kept
    }

    [Fact]
    public void Without_TWR_or_with_an_old_one_nothing_is_asked()
    {
        var (w, _) = Patrol();
        var none = new OutlawLink(() => null);
        Hours(w, none, 10 * 24);
        Assert.False(none.Bound);
        Assert.Contains("not found", none.Summary());
        var old = new OutlawLink(() => typeof(TwrApi2));
        Hours(w, old, 10 * 24);
        Assert.False(old.Bound);
        Assert.Empty(TwrOutlaws.Struck);
    }
}
