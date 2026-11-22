// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
namespace Dustlife.Game;

/// <summary>
/// The two link sections a save keeps after the war: the overruns waiting for TWR's town-fell (<see cref="WastelandLink"/>), then the outlaw
/// strike times (<see cref="OutlawLink"/>). Each is read whole or not at all. A damaged overrun section keeps neither, as the outlaw times come
/// after it in the stream; a damaged outlaw section keeps the overruns already read. Either way the log says exactly what was kept.
/// </summary>
public static class LinkSave
{
    /// <summary>What a read kept; <paramref name="Damage"/> is the log line when a section was damaged, else null.</summary>
    public readonly record struct Result(int Overruns, int OutlawTimes, string? Damage);

    /// <summary>The save errors the readers throw on a damaged section (EndOfStreamException is an IOException).</summary>
    public static bool IsDamage(Exception e) => e is InvalidDataException or IOException;

    public static Result Read(Stream stream, Core.World w, WastelandLink twr, OutlawLink outlaws)
    {
        int waiting;
        try
        {
            waiting = twr.Read(stream, w);
        }
        catch (Exception e) when (IsDamage(e))
        {
            twr.Clear();
            outlaws.Clear();
            return new(0, 0, $"the save's waiting overruns could not be read ({e.Message}): none kept, nor the outlaw strike times after them");
        }

        try
        {
            return new(waiting, outlaws.Read(stream), null);
        }
        catch (Exception e) when (IsDamage(e))
        {
            outlaws.Clear();
            return new(waiting, 0, $"the save's outlaw strike times could not be read ({e.Message}): none kept; {waiting} overruns kept");
        }
    }
}
