// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using System.Text;

namespace Dustlife.Core;

/// <summary>
/// A module with state of its own in the save. Indices of places and factions go through <see cref="SaveMap"/>, so a save still loads into a
/// world whose places or factions changed (a mod added or removed towns).
/// </summary>
public interface ISavedModule
{
    /// <summary>
    /// Writes the module's state, places and factions as the world's own indices.
    /// </summary>
    void Save(BinaryWriter w);

    /// <summary>
    /// Reads what <see cref="Save"/> wrote and returns the step that puts it in place, run only once the whole save has been read;
    /// <paramref name="map"/> turns a saved index into this world's (-1: gone).
    /// </summary>
    Action Load(BinaryReader r, SaveMap map);
}

/// <summary>
/// Saved place and faction indices to this world's, by key (-1: no longer in the world).
/// </summary>
public sealed class SaveMap(int[] places, int[] factions)
{
    public int Place(int saved) => saved >= 0 && saved < places.Length ? places[saved] : -1;

    public int Faction(int saved) => saved >= 0 && saved < factions.Length ? factions[saved] : -1;

    /// <summary>The saved places and factions this world no longer has.</summary>
    public int MissingPlaces => places.Count(p => p < 0);

    public int MissingFactions => factions.Count(f => f < 0);
}

/// <summary>
/// The world's save (port step 5): the clock, the random streams, who holds each place, the factions' records, every band with its leader,
/// the news, and each module's own state. Places and factions are stored by key. The world it loads into is built first, from the game's
/// data, with the same modules; loading then replaces its moving parts. A bad or foreign stream throws <see cref="InvalidDataException"/>
/// before anything in the world changes.
/// </summary>
public static class WorldSave
{
    const uint Magic = 0x57534C44;                                         // "DLSW"
    const int Version = 1;
    const int MaxCount = 1_000_000;

    public static void Write(World world, Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        world.SaveCore(w);
        var saved = world.Modules.OfType<ISavedModule>().ToList();
        w.Write(saved.Count);
        foreach (var m in saved)
        {
            using var buf = new MemoryStream();
            using (var mw = new BinaryWriter(buf, Encoding.UTF8, leaveOpen: true)) m.Save(mw);
            w.Write(((IModule)m).Name);
            w.Write((int)buf.Length);
            w.Write(buf.GetBuffer(), 0, (int)buf.Length);
        }
    }

    /// <summary>
    /// Loads a save into <paramref name="world"/> (built, not started) and returns how its keys matched.
    /// </summary>
    public static SaveMap Read(World world, Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a Dustlife save");
            int version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException($"Dustlife save version {version}, this build reads {Version}");
            var core = World.ReadCore(r);
            int count = Count(r);
            var modules = new List<(string Name, byte[] Data)>();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                int len = r.ReadInt32();
                if (len < 0 || len > 64 << 20) throw new InvalidDataException("bad module length");
                byte[] data = r.ReadBytes(len);
                if (data.Length != len) throw new EndOfStreamException();
                modules.Add((name, data));
            }

            var map = world.MapOf(core);
            var apply = new List<Action>();
            foreach (var (name, data) in modules)
            {
                if (world.Modules.FirstOrDefault(m => m.Name == name) is not ISavedModule m) continue;   // a module this build lacks
                using var mr = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
                apply.Add(m.Load(mr, map));
            }

            // everything parsed: now the world changes
            world.ApplyCore(core, map);
            foreach (var step in apply) step();
            return map;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("damaged Dustlife save: " + e.Message, e);
        }
    }

    /// <summary>
    /// A length read from the stream, refused when negative or absurd.
    /// </summary>
    public static int Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > MaxCount) throw new InvalidDataException($"bad count {n}");
        return n;
    }

    public static void WriteVec(BinaryWriter w, Vector2 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
    }

    public static Vector2 ReadVec(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());

    /// <summary>
    /// A float that must be a finite number (a damaged save gives NaN or infinity).
    /// </summary>
    public static float Fin(BinaryReader r)
    {
        float v = r.ReadSingle();
        if (!World.Finite(v)) throw new InvalidDataException("not a number");
        return v;
    }

    public static double FinD(BinaryReader r)
    {
        double v = r.ReadDouble();
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new InvalidDataException("not a number");
        return v;
    }
}
