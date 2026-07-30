// AlRunnerPathsTests — the cross-platform home resolution that replaced the POSIX-only
// HOME lookup (which was null on Windows, silently disabling cache/artifact discovery).

using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class AlRunnerPathsTests
{
    [Fact]
    public void UserHome_IsNonEmpty_Rooted_And_Exists_OnEveryOS()
    {
        var home = AlRunnerPaths.UserHome;
        Assert.False(string.IsNullOrEmpty(home)); // the exact failure mode of POSIX HOME on Windows
        Assert.True(Path.IsPathRooted(home));
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void UserHome_MatchesUserProfileSpecialFolder()
    {
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AlRunnerPaths.UserHome);
    }
}
