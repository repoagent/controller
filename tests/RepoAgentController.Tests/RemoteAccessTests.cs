using Xunit;

namespace RepoAgentController.Tests;

public sealed class RemoteAccessTests
{
    [Theory]
    [InlineData((int)RemoteOperation.Enable)]
    [InlineData((int)RemoteOperation.Pair)]
    [InlineData((int)RemoteOperation.Status)]
    [InlineData((int)RemoteOperation.Clients)]
    [InlineData((int)RemoteOperation.Revoke)]
    [InlineData((int)RemoteOperation.Disable)]
    public void Every_remote_operation_requires_administration(int operation)
    {
        foreach (var permission in new[] { LocalCallerPermission.Unknown, LocalCallerPermission.Inspection, (LocalCallerPermission)999 })
        {
            var result = RemoteAccess.Execute((RemoteOperation)operation, permission);

            Assert.Equal(RemoteAccessOutcome.Forbidden, result.Outcome);
            Assert.Equal("Remote administration permission is required.", result.Message);
        }
    }

    [Theory]
    [InlineData((int)RemoteOperation.Enable)]
    [InlineData((int)RemoteOperation.Pair)]
    [InlineData((int)RemoteOperation.Status)]
    [InlineData((int)RemoteOperation.Clients)]
    [InlineData((int)RemoteOperation.Revoke)]
    [InlineData((int)RemoteOperation.Disable)]
    public void Every_administrator_operation_reports_unqualified_remote_access(int operation)
    {
        var result = RemoteAccess.Execute((RemoteOperation)operation, LocalCallerPermission.Administration);

        Assert.Equal(RemoteAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal("Native remote access is unavailable: agent-tool isolation and controller admission of native remote commands are not qualified. Current access and revocation are unconfirmed.", result.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void An_unknown_operation_is_denied_even_to_an_administrator(int operation)
    {
        var result = RemoteAccess.Execute((RemoteOperation)operation, LocalCallerPermission.Administration);

        Assert.Equal(RemoteAccessOutcome.Forbidden, result.Outcome);
        Assert.Equal("Remote operation is not authorized.", result.Message);
    }
}
