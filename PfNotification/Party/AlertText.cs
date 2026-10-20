using System.Collections.Generic;
using System.Linq;

namespace PfNotification.Party;

/// <summary>Wording of the phone notifications. Jobs and counts only: no names or worlds leave the PC.</summary>
public static class AlertText
{
    public const string FullTitle = "Party full";
    public const string DutyTitle = "Duty ready";

    /// <summary>What a roulette pop is called: the game sends no duty name for those.</summary>
    public const string RouletteName = "Duty Roulette";

    /// <summary>"8/8: 2 tanks, 2 healers, 4 DPS".</summary>
    public static string Summary(int count, int target, RoleCounts roles) => $"{count}/{target}: {roles}";

    /// <summary>The duty's name, or <see cref="RouletteName"/> when the game didn't send one.</summary>
    public static string DutyName(string? raw) => string.IsNullOrWhiteSpace(raw) ? RouletteName : raw.Trim();

    /// <summary>"Sastasha: accept within 45 s".</summary>
    public static string DutyBody(string name) => $"{name}: accept within 45 s";

    /// <summary>"WHM joined", "SAM left", or "SAM left, WHM and DRG joined". Empty abbreviations read as "someone".</summary>
    public static string ChangeTitle(IReadOnlyList<string> joined, IReadOnlyList<string> left)
    {
        var parts = new List<string>(2);
        if (left.Count > 0)
        {
            parts.Add($"{List(left)} left");
        }

        if (joined.Count > 0)
        {
            parts.Add($"{List(joined)} joined");
        }

        return (parts.Count == 0) ? "Party changed" : string.Join(", ", parts);
    }

    private static string List(IReadOnlyList<string> jobs)
    {
        var names = jobs.Select(j => string.IsNullOrWhiteSpace(j) ? "someone" : j).ToList();
        return (names.Count == 1) ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }
}
