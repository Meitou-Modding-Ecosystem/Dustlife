// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;

namespace Dustlife.Tests;

/// <summary>Stand-ins for The Wasteland Remembers' <c>WastelandApi</c> at its versions (same member shapes as TWR's MODDER_API.md).</summary>
public static class TwrApi4
{
    public const int Version = 4;
    public static readonly List<string> Calls = [];
    public static bool Answer = true;

    public static bool ReportAtNode(string node, string kind, string faction, string text, int weight, string source)
    {
        Calls.Add($"node|{node}|{kind}|{faction}|{text}|{weight}|{source}");
        return Answer;
    }

    public static bool Report(string kind, string place, string faction, string text, int weight, string source)
    {
        Calls.Add($"name|{place}|{kind}|{faction}|{text}|{weight}|{source}");
        return Answer;
    }
}

public static class TwrApi2
{
    public const int Version = 2;
    public static readonly List<string> Calls = [];

    public static bool Report(string kind, string place, string faction, string text, int weight, string source)
    {
        Calls.Add($"name|{place}|{kind}|{faction}|{text}|{weight}|{source}");
        return true;
    }
}

public static class TwrApi1
{
    public const int Version = 1;
    public static int Calls;

    public static bool Report(string kind, string place, string faction, string text, int weight, string source)
    {
        Calls++;
        return true;
    }
}

public static class TwrThrows
{
    public const int Version = 4;

    public static bool ReportAtNode(string node, string kind, string faction, string text, int weight, string source) =>
        throw new InvalidOperationException("TWR broke");
}

/// <summary>Tests share the stand-ins' static call lists: run one at a time.</summary>
[Collection("twr")]
public sealed class WastelandLinkTests
{
    static (World w, Place a, Place b) War()
    {
        var w = new World();
        w.AddFaction("hn", "Holy Nation", false, false);
        w.AddFaction("shek", "Shek Kingdom", false, false);
        var a = w.PlaceById(w.AddPlace("1111-rebirth.mod", "Bad Teeth", new Vector2(0, 0), PlaceKind.Base, 0, 0))!;
        var b = w.PlaceById(w.AddPlace("2222-rebirth.mod", "Holy Farm", new Vector2(5000, 0), PlaceKind.Resource, 50, 1))!;
        return (w, a, b);
    }

    static WorldEvent Took(World w, Place p, string key = "took_food", int imp = 2) =>
        Emit(w, EventType.Capture, 0, 1, p.Id, imp, key, "Holy Nation", p.Name, "Shek Kingdom");

    static WorldEvent Emit(World w, EventType t, int a, int b, int place, int imp, string key, params string[] args)
    {
        w.Emit(t, a, b, place, Vector2.Zero, imp, key, args);
        return w.Events[^1];
    }

    [Fact]
    public void A_taking_goes_to_TWR_by_node_as_town_fell_with_the_taker()
    {
        TwrApi4.Calls.Clear();
        TwrApi4.Answer = true;
        var (w, _, b) = War();
        var link = new WastelandLink(() => typeof(TwrApi4));
        Assert.Equal(1, link.Forward(w, [Took(w, b)]));
        Assert.Equal(["node|town:2222-rebirth.mod|town-fell|Holy Nation||65|dustlife"], TwrApi4.Calls);
        Assert.Equal(1, link.Sent);
    }

    [Fact]
    public void Only_the_kinds_TWR_hears_are_sent()
    {
        TwrApi4.Calls.Clear();
        var (w, a, b) = War();
        var link = new WastelandLink(() => typeof(TwrApi4));
        WorldEvent[] all =
        [
            Emit(w, EventType.Capture, 0, -1, b.Id, 2, "hold_settle", "Holy Nation", b.Name),          // settled while empty: no fall
            Emit(w, EventType.Info, -1, -1, a.Id, 2, "town_changed", a.Name, "Holy Nation", "Shek Kingdom"), // TWR sees the game's own
            Emit(w, EventType.Warlord, 0, -1, -1, 2, "warlord_new", "Bayan", "Holy Nation"),              // no place
            Emit(w, EventType.Rout, 1, 0, a.Id, 1, "siege_end", "Bayan the Grim", a.Name),
            Emit(w, EventType.Invasion, 0, 1, a.Id, 3, "march", "Holy Nation", a.Name, b.Name, "3", "Bayan"),
        ];
        Assert.Equal(2, link.Forward(w, all));
        Assert.Equal(3, link.Skipped);
        Assert.Equal("node|town:1111-rebirth.mod|siege-broken|||55|dustlife", TwrApi4.Calls[0]);
        Assert.StartsWith("node|town:1111-rebirth.mod|war-march||Holy Nation march on Bad Teeth from Holy Farm", TwrApi4.Calls[1]);
        Assert.EndsWith("|80|dustlife", TwrApi4.Calls[1]);
    }

    [Fact]
    public void An_older_TWR_is_told_by_place_name_and_API_1_gets_nothing()
    {
        TwrApi2.Calls.Clear();
        var (w, _, b) = War();
        Assert.Equal(1, new WastelandLink(() => typeof(TwrApi2)).Forward(w, [Took(w, b, "took", 3)]));
        Assert.Equal(["name|Holy Farm|town-fell|Holy Nation||80|dustlife"], TwrApi2.Calls);

        TwrApi1.Calls = 0;
        var old = new WastelandLink(() => typeof(TwrApi1));
        Assert.Equal(0, old.Forward(w, [Took(w, b)]));
        Assert.False(old.Bound);
        Assert.Equal(0, TwrApi1.Calls);
    }

    [Fact]
    public void Without_TWR_or_when_it_throws_nothing_breaks()
    {
        var (w, _, b) = War();
        var none = new WastelandLink(() => null);
        Assert.Equal(0, none.Forward(w, [Took(w, b)]));
        Assert.False(none.Bound);

        var broken = new WastelandLink(() => throw new TypeLoadException());
        Assert.Equal(0, broken.Forward(w, [Took(w, b)]));

        var throws = new WastelandLink(() => typeof(TwrThrows));
        Assert.Equal(0, throws.Forward(w, [Took(w, b), Took(w, b)]));
        Assert.Equal(2, throws.Failed);
    }

    [Fact]
    public void TWR_found_late_is_bound_then_and_refusals_are_counted()
    {
        TwrApi4.Calls.Clear();
        var (w, _, b) = War();
        Type? there = null;
        var link = new WastelandLink(() => there);
        Assert.Equal(0, link.Forward(w, [Took(w, b)]));                    // TWR's assembly not loaded yet
        there = typeof(TwrApi4);
        TwrApi4.Answer = false;                                            // e.g. a repeat within 12 game hours
        Assert.Equal(0, link.Forward(w, [Took(w, b)]));
        Assert.True(link.Bound);
        Assert.Equal(1, link.Refused);
        TwrApi4.Answer = true;
    }

    [Fact]
    public void A_big_day_sends_at_most_the_cap()
    {
        TwrApi4.Calls.Clear();
        TwrApi4.Answer = true;
        var (w, _, b) = War();
        var link = new WastelandLink(() => typeof(TwrApi4));
        var many = Enumerable.Range(0, 10).Select(_ => Took(w, b)).ToList();
        Assert.Equal(WastelandLink.MaxPerStep, link.Forward(w, many));
        Assert.Equal(10 - WastelandLink.MaxPerStep, link.Capped);
        Assert.Equal(WastelandLink.MaxPerStep, TwrApi4.Calls.Count);
    }
}
