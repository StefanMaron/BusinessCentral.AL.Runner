// SessionUserWindowsSidShapeGapTests: the session user's Windows SID is reached through
// NavSession.Authenticator -> navUser -> windowsSID. A member BC moved must REFUSE with a
// BcShapeGapException naming it; the old `?.` chain answered "no user" / "" and left Sid() on a
// value nobody chose (#2352, the rule in guards-need-a-third-state.md, #4147).
//
// RUNNER-MECHANISM tests: fake sessions stand in for BC's layout, because only a layout BC does
// not have can exercise the refusal. The control arms show the same readers still answer.
using System;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class SessionUserWindowsSidShapeGapTests
{
    private sealed class NavUserWithSid { private string windowsSID = "S-1-5-21-1-2-3-4"; public string Peek() => windowsSID; }
    private sealed class NavUserWithoutSid { private string userName = "X"; public string Peek() => userName; }
    private sealed class AuthWith { private object? navUser; public AuthWith(object? u) { navUser = u; } public object? Peek() => navUser; }
    private sealed class AuthWithoutNavUser { }
    private sealed class SessionWith { public SessionWith(object? a) { Authenticator = a; } public object? Authenticator { get; } }
    private sealed class SessionWithoutAuthenticator { }

    private static object? Invoke(string method, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"test setup: RecordPatches.{method} not found");
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
    }

    [Fact]
    public void ReadingTheSid_RefusesWhenAuthenticatorHasMoved()
    {
        var gap = Assert.Throws<BcShapeGapException>(
            () => Invoke("ReadSkeletonUserWindowsSid", new SessionWithoutAuthenticator()));
        Assert.Contains("Authenticator", gap.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingTheSid_RefusesWhenNavUserHasMoved()
    {
        var gap = Assert.Throws<BcShapeGapException>(
            () => Invoke("ReadSkeletonUserWindowsSid", new SessionWith(new AuthWithoutNavUser())));
        Assert.Contains("navUser", gap.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingTheSid_RefusesWhenWindowsSidHasMoved()
    {
        var gap = Assert.Throws<BcShapeGapException>(
            () => Invoke("ReadSkeletonUserWindowsSid", new SessionWith(new AuthWith(new NavUserWithoutSid()))));
        Assert.Contains("windowsSID", gap.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void PokingTheSid_RefusesWhenWindowsSidHasMoved_InsteadOfReportingNoUser()
    {
        var gap = Assert.Throws<BcShapeGapException>(
            () => Invoke("TryPokeSessionUserWindowsSid", new SessionWith(new AuthWith(new NavUserWithoutSid())), "S-1-5-21-9-9-9-9"));
        Assert.Contains("windowsSID", gap.Member, StringComparison.Ordinal);
    }

    // CONTROLS: what a read that SUCCEEDED answers. A refusal on every input would pass the arms above.
    [Fact]
    public void WhenTheLayoutIsBcs_TheReadersStillAnswer()
    {
        var user = new NavUserWithSid();
        var session = new SessionWith(new AuthWith(user));

        Assert.Equal("S-1-5-21-1-2-3-4", Invoke("ReadSkeletonUserWindowsSid", session));
        Assert.Equal(true, Invoke("TryPokeSessionUserWindowsSid", session, "S-1-5-21-9-9-9-9"));
        Assert.Equal("S-1-5-21-9-9-9-9", user.Peek());
        Assert.Equal("S-1-5-21-9-9-9-9", Invoke("ReadSkeletonUserWindowsSid", session));
    }

    [Fact]
    public void WhenTheSessionHoldsNoUser_ThatIsAnAnswerNotARefusal()
    {
        var session = new SessionWith(null);
        Assert.Equal(string.Empty, Invoke("ReadSkeletonUserWindowsSid", session));
        Assert.Equal(false, Invoke("TryPokeSessionUserWindowsSid", session, "S-1-5-21-9-9-9-9"));

        var noNavUser = new SessionWith(new AuthWith(null));
        Assert.Equal(string.Empty, Invoke("ReadSkeletonUserWindowsSid", noNavUser));
    }
}
