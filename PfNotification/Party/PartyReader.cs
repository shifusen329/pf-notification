using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace PfNotification.Party;

public enum PartySource
{
    None,
    PartyList,
    CrossRealm,
}

/// <summary>Party size including the local player. <see cref="SelfAdded"/> means the cross-realm group didn't list them.</summary>
public readonly record struct PartySnapshot(int Count, PartySource Source, bool SelfAdded);

/// <summary>
/// Reads the local player's party from client memory, after PartyVet's <c>PartySnapshot</c>. <c>IPartyList</c> covers
/// same-world parties and any party inside a duty; Party Finder and cross-world parties before the duty only exist in
/// <see cref="InfoProxyCrossRealm"/>. Every member MUST run on the framework thread.
/// <para>
/// Passive-only rule: this class reads fields and nothing else. Never call <c>InfoProxyCrossRealm.RequestData</c>,
/// <c>EndRequest</c>, <c>AddData</c>, <c>ClearListData</c> or <c>HandleZoneInitPacket</c>; they talk to the server or
/// change client state.
/// </para>
/// </summary>
public static unsafe class PartyReader
{
    public static PartySnapshot Read(IPartyList partyList, ulong selfContentId)
    {
        if (partyList.Length > 0)
        {
            return new PartySnapshot(partyList.Length, PartySource.PartyList, false);
        }

        if (!TryGetLocalGroup(out var proxy, out var index))
        {
            return new PartySnapshot(0, PartySource.None, false);
        }

        ref var group = ref proxy->CrossRealmGroups[index];
        var count = (int)group.GroupMemberCount;
        var selfListed = false;
        for (var i = 0; i < count; i++)
        {
            if (group.GroupMembers[i].ContentId == selfContentId)
            {
                selfListed = true;
                break;
            }
        }

        var addSelf = !selfListed && (selfContentId != 0);
        return new PartySnapshot(addSelf ? (count + 1) : count, PartySource.CrossRealm, addSelf);
    }

    /// <summary>Role tally of the current party. Only called when an alert fires.</summary>
    public static RoleCounts ReadRoles(IPartyList partyList, ExcelSheet<ClassJob> classJobs, ulong selfContentId, uint selfClassJobId)
    {
        var roles = new RoleCounts();
        if (partyList.Length > 0)
        {
            foreach (var member in partyList)
            {
                if (member is not null)
                {
                    roles = roles.With(RoleOf(classJobs, member.ClassJob.RowId));
                }
            }

            return roles;
        }

        if (!TryGetLocalGroup(out var proxy, out var index))
        {
            return roles;
        }

        ref var group = ref proxy->CrossRealmGroups[index];
        var selfListed = false;
        for (var i = 0; i < group.GroupMemberCount; i++)
        {
            ref var member = ref group.GroupMembers[i];
            selfListed |= member.ContentId == selfContentId;
            roles = roles.With(RoleOf(classJobs, member.ClassJobId));
        }

        if (!selfListed && (selfContentId != 0))
        {
            roles = roles.With(RoleOf(classJobs, selfClassJobId));
        }

        return roles;
    }

    private static bool TryGetLocalGroup(out InfoProxyCrossRealm* proxy, out int index)
    {
        proxy = InfoProxyCrossRealm.Instance();
        index = 0;
        if ((proxy == null) || !proxy->IsCrossRealm || (proxy->GroupCount == 0))
        {
            return false;
        }

        // Alliance listings have several groups; the player's own is not necessarily the first.
        index = proxy->LocalPlayerGroupIndex;
        if ((index < 0) || (index >= proxy->GroupCount))
        {
            index = 0;
        }

        return true;
    }

    private static PartyRole RoleOf(ExcelSheet<ClassJob> classJobs, uint classJobId) =>
        classJobs.TryGetRow(classJobId, out var row) ? RoleCounts.FromClassJobRole(row.Role) : PartyRole.Unknown;
}
