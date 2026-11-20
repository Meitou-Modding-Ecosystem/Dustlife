// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Text;

namespace Dustlife.Core;

/// <summary>
/// Every sentence the world says, by key. The keys are the reference's (its <c>lang/en.txt</c>), so its Russian, Chinese and Japanese
/// files fit as they are. <see cref="Format"/> fills <c>{1}</c>..<c>{7}</c>; an argument is never read as a format.
/// </summary>
public sealed class Text
{
    /// <summary>
    /// The English sentences the core uses.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["started"] = "Dustlife started: {1} factions, {2} places, {3} warbands",
        ["where_at"] = "at {1}",
        ["where_near"] = "near {1}",
        ["where_wastes"] = "in the wastes",
        ["wiped"] = "{1}'s band of {2} was wiped out {3}.",
        ["wiped_by"] = "{1}'s band of {2} was wiped out by {3} {4}.",
        ["leader_fell"] = "{1} of {2} fell {3}; {4} leads the survivors now.",
        ["scar"] = "{1} lived through a bad fight {2} and is now called {3}.",
        ["ep_scarred"] = "the Scarred",
        ["ep_one_armed"] = "the One-Armed",
        ["ep_iron_hide"] = "Iron-Hide",
        ["ep_limping"] = "the Limping",
        ["ep_half_face"] = "Half-Face",
        ["ep_unbroken"] = "the Unbroken",
        ["ep_grim"] = "the Grim",
        ["battle"] = "{1} and {2} clashed {3} ({4} vs {5}; {6} and {7} fell).",
        ["rout"] = "{1}'s men broke and ran.",
        ["warlord_new"] = "{1} now leads the war host of {2}.",
        ["warlord_succ"] = "With {1} gone, {2} now leads the war host of {3}.",
        ["siege_end"] = "{1} gave up the siege of {2}.",
        ["overrun"] = "The defenders of {1} ({2}) were overrun.",
        ["took"] = "{1} took {2} from {3}.",
        ["hold"] = "{1} now hold {2}.",
        // a taking with its motive (Warfare's motive rules)
        ["took_doctrine"] = "{1} took {2} from {3}, as their creed demands.",
        ["hold_doctrine"] = "{1} now hold {2}, as their creed demands.",
        ["took_counter"] = "{1} struck while {3}'s home was thin and took {2}.",
        ["hold_counter"] = "{1} struck while their enemy's home was thin and now hold {2}.",
        ["took_retaliation"] = "{1} took {2} from {3}, who had marched on them.",
        ["hold_retaliation"] = "{1} now hold {2}, from where they were attacked.",
        ["took_food"] = "Short of food, {1} took {2} from {3} for its harvest.",
        ["hold_food"] = "Short of food, {1} now hold {2} for its harvest.",
        ["took_arms"] = "Short of arms, {1} took {2} from {3} for its forges.",
        ["hold_arms"] = "Short of arms, {1} now hold {2} for its forges.",
        ["took_lifeline"] = "{1} took {2} from {3} and opened the road home.",
        ["hold_lifeline"] = "{1} now hold {2} and the road home is open.",
        ["took_grievance"] = "{1} took back {2} from {3}.",
        ["hold_grievance"] = "{1} are back in {2}.",
        ["took_opportunity"] = "{1} took {2} from {3}, who held it too thinly.",
        ["hold_opportunity"] = "{1} now hold {2}, left too thinly held.",
        ["took_settle"] = "{1} took {2} from {3} to work its land.",
        ["hold_settle"] = "{1} settled {2} to work its land.",
        ["took_patrol"] = "{1}'s patrols took {2} from {3} and stayed.",
        ["hold_patrol"] = "{1}'s patrols rested at {2} and stayed.",
        ["town_changed"] = "{1} has changed: {2} hold it now, not {3}.",
        ["town_emptied"] = "{1} has changed: {2} no longer hold it.",
        ["raid"] = "{1} raid {2} from {3}: {4} bands under {5}.",
        ["march"] = "{1} march on {2} from {3}: {4} bands under {5}.",
        ["claim"] = "{1} send {2} bands from {3} to claim {4}.",
        ["resurgence"] = "{1} are not finished: {2} bands gather to retake {3}.",
        ["escape"] = "{1} slaves escaped from {2}.",
        ["escape_near"] = "{1} slaves escaped from {2} near {3}.",
        ["slaves"] = "{1} dragged {2} {3} off in chains {4}.",
    };

    readonly IReadOnlyDictionary<string, string> _table;

    /// <summary>
    /// A table over <paramref name="language"/> (null = English only); a key it lacks falls back to English.
    /// </summary>
    public Text(IReadOnlyDictionary<string, string>? language = null) => _table = language ?? English;

    /// <summary>
    /// The sentence for <paramref name="key"/>: the language's, else English, else the key itself.
    /// </summary>
    public string Get(string key) => _table.TryGetValue(key, out var s) ? s : English.TryGetValue(key, out var e) ? e : key;

    /// <summary>
    /// The sentence with <c>{1}</c>..<c>{7}</c> filled from <paramref name="args"/> (a missing one is empty).
    /// </summary>
    public string Format(string key, IReadOnlyList<string> args)
    {
        string fmt = Get(key);
        var sb = new StringBuilder(fmt.Length + 32);
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] == '{' && i + 2 < fmt.Length && fmt[i + 2] == '}' && fmt[i + 1] is >= '1' and <= '7')
            {
                int n = fmt[i + 1] - '1';
                if (n < args.Count) sb.Append(args[n]);
                i += 2;
                continue;
            }

            sb.Append(fmt[i]);
        }

        return sb.ToString();
    }
}
