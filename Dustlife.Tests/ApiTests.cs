// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using System.Reflection;
using Dustlife.Core;
using Dustlife.Modules;

namespace Dustlife.Tests;

/// <summary>
/// The API for other mods, read the way another mod reads it: by reflection, plain types only, nothing throws.
/// </summary>
public sealed class ApiTests
{
    static readonly Type Api = Type.GetType("Dustlife.DustlifeApi, Dustlife")!;

    static T Get<T>(string member, params object?[] args)
    {
        var prop = Api.GetProperty(member, BindingFlags.Public | BindingFlags.Static);
        if (prop != null) return (T)prop.GetValue(null)!;
        var field = Api.GetField(member, BindingFlags.Public | BindingFlags.Static);
        if (field != null) return (T)field.GetValue(null)!;
        return (T)Api.GetMethod(member, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args)!;
    }

    static void Attach(World? w) => Api.GetMethod("Attach", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [w]);

    [Fact]
    public void Another_mod_finds_it_reads_it_and_gets_plain_answers()
    {
        Assert.NotNull(Api);
        Assert.Equal(1, Get<int>("Version"));
        Assert.True(Get<bool>("IsDustlifeSquad", "dustlife:12"));
        Assert.False(Get<bool>("IsDustlifeSquad", (string?)null));

        Attach(null);
        Assert.False(Get<bool>("Active"));
        Assert.Empty(Get<IReadOnlyList<IReadOnlyDictionary<string, object>>>("News", 0L));
        Assert.Equal("", Get<string>("Holder", "Stack"));

        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        w.AddPlace("site-1", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        w.AddModule(new Warfare(new WarfareConfig()));
        w.StartNew();
        w.Emit(EventType.Capture, hn, -1, 0, new Vector2(10, 20), 3, "hold", "Holy Nation", "Stack");
        Attach(w);
        try
        {
            Assert.True(Get<bool>("Active"));
            var news = Get<IReadOnlyList<IReadOnlyDictionary<string, object>>>("News", 0L);
            var row = news.Last();
            Assert.Equal("hold", row["Key"]);
            Assert.Equal("Holy Nation now hold Stack.", row["Text"]);
            Assert.Equal("hn", row["Faction"]);
            Assert.Equal("site-1", row["Town"]);
            Assert.Equal(10.0, row["X"]);
            Assert.All(row.Values, v => Assert.True(v is string or long or int or double or bool));
            Assert.Empty(Get<IReadOnlyList<IReadOnlyDictionary<string, object>>>("News", (long)row["Seq"]));
            Assert.Equal("hn", Get<string>("Holder", "Stack"));
            Assert.Equal("hn", Get<string>("Holder", "site-1"));
            Assert.Equal("base", Get<IReadOnlyList<IReadOnlyDictionary<string, object>>>("Places")[0]["Kind"]);
        }
        finally
        {
            Attach(null);
        }
    }
}
