using PfNotification.Party;

namespace PfNotification.Tests;

public sealed class RoleCountsTests
{
    [Fact]
    public void FullPartyFormatsAsInTheAlert()
    {
        Assert.Equal("2 tanks, 2 healers, 4 DPS", new RoleCounts(2, 2, 4, 0).ToString());
    }

    [Fact]
    public void SingularsAndUnknownJobs()
    {
        Assert.Equal("1 tank, 1 healer, 1 DPS, 1 other", new RoleCounts(1, 1, 1, 1).ToString());
    }

    [Fact]
    public void EmptyRolesAreOmitted()
    {
        Assert.Equal("4 DPS", new RoleCounts(0, 0, 4, 0).ToString());
        Assert.Equal("no members", new RoleCounts().ToString());
    }

    [Theory]
    [InlineData(1, PartyRole.Tank)]
    [InlineData(2, PartyRole.Dps)]
    [InlineData(3, PartyRole.Dps)]
    [InlineData(4, PartyRole.Healer)]
    [InlineData(0, PartyRole.Unknown)]
    [InlineData(9, PartyRole.Unknown)]
    public void MapsLuminaClassJobRole(byte role, PartyRole expected)
    {
        Assert.Equal(expected, RoleCounts.FromClassJobRole(role));
    }

    [Fact]
    public void WithTalliesEachRole()
    {
        var roles = new RoleCounts()
            .With(PartyRole.Tank)
            .With(PartyRole.Healer)
            .With(PartyRole.Dps)
            .With(PartyRole.Dps)
            .With(PartyRole.Unknown);

        Assert.Equal(new RoleCounts(1, 1, 2, 1), roles);
        Assert.Equal(5, roles.Total);
    }
}
