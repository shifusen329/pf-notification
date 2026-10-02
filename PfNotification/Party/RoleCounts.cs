using System.Collections.Generic;

namespace PfNotification.Party;

public enum PartyRole
{
    Unknown,
    Tank,
    Healer,
    Dps,
}

/// <summary>How many members fill each role. Jobs only: no names or worlds, so nothing identifying leaves the PC.</summary>
public readonly record struct RoleCounts(int Tanks, int Healers, int Dps, int Unknown)
{
    public int Total => Tanks + Healers + Dps + Unknown;

    public RoleCounts With(PartyRole role) => role switch
    {
        PartyRole.Tank => this with { Tanks = Tanks + 1 },
        PartyRole.Healer => this with { Healers = Healers + 1 },
        PartyRole.Dps => this with { Dps = Dps + 1 },
        _ => this with { Unknown = Unknown + 1 },
    };

    /// <summary>Lumina <c>ClassJob.Role</c>: 1 tank, 2 melee, 3 ranged (physical and magical), 4 healer, 0 none.</summary>
    public static PartyRole FromClassJobRole(byte role) => role switch
    {
        1 => PartyRole.Tank,
        2 or 3 => PartyRole.Dps,
        4 => PartyRole.Healer,
        _ => PartyRole.Unknown,
    };

    /// <summary>"2 tanks, 2 healers, 4 DPS", omitting empty roles; "no members" when there are none.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Tanks > 0)
        {
            parts.Add(Tanks == 1 ? "1 tank" : $"{Tanks} tanks");
        }

        if (Healers > 0)
        {
            parts.Add(Healers == 1 ? "1 healer" : $"{Healers} healers");
        }

        if (Dps > 0)
        {
            parts.Add($"{Dps} DPS");
        }

        if (Unknown > 0)
        {
            parts.Add($"{Unknown} other");
        }

        return (parts.Count == 0) ? "no members" : string.Join(", ", parts);
    }
}
