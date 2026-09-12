// StartSessionTimeoutValidationTests — the validation arm of StartSession's timeout argument
// (#3291), the follow-up the SCOPE AUDIT note on ALSession_ALStartSessionAsyncImpl records.
//
// BC computes the EFFECTIVE timeout (the argument, else NavSession.DefaultBackgroundTimeout)
// and raises above int.MaxValue milliseconds. Decompiled from ALSession.ALStartSessionAsyncImpl
// in the shipped Ncl.dll:
//
//     if (timeoutTs.TotalMilliseconds > 2147483647.0)
//         throw new NavNCLArgumentOutOfRangeException(PrivacyClassification.SystemMetadata,
//             string.Format(CultureInfo.CurrentCulture, Lang.TooLargeTimeoutALStartSession,
//                           timeoutTs.TotalMilliseconds, int.MaxValue));
//
// WHY THIS TEST IS HERE AND NOT IN THE CORPUS
//
// "BC raises above int.MaxValue ms" is a plain statement about BC, so bc-behavior-tests-go-
// upstream.md would normally send it to the al-language corpus. It cannot go there, and the
// reason is BC's own statement order rather than any corpus fixture. In BC's body the
// TestIsolation guard throws BEFORE this check (and outside the try), so a [Test] body under
// any isolation other than Disabled is refused first and never reaches the timeout at all —
// which is exactly what corpus codeunit 60397 measures on all eight legs. The corpus declares
// no Subtype = TestRunner codeunit and its CI drives the harness by codeunit_range with no
// isolation input, so every corpus test runs under TestIsolation = Codeunit; and 60397's own
// header records that switching it to Disabled would invalidate what four other codeunits
// measure. The install-trigger route is closed too: it runs outside a [Test] body, but BC's
// AppInstallationContext check returns false before the timeout check.
//
// So this is a runner-side MECHANISM test, per .claude/agents/impl-agent.md: it pins the
// runner's own C# behaviour rather than restating the BC claim. The BC claim itself is
// evidenced by the decompiled body above and by the boundary constants below, both read from
// the shipped binaries.

using System;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class StartSessionTimeoutValidationTests
{
    // BC's boundary, from the decompiled comparison: STRICTLY greater than int.MaxValue ms
    // raises. int.MaxValue itself is accepted, which is what makes the two tests below a
    // boundary pair rather than a single direction.
    private const long MaxMs = int.MaxValue;          // 2147483647
    private const long OverMs = (long)int.MaxValue + 1;

    // The substring of BC's own message. The full text lives in Lang.TooLargeTimeoutALStartSession
    // inside Microsoft.Dynamics.Nav.Language.dll, which ships in the artifacts:
    //
    //   "The specified timeout of {0} ms is longer than the maximum supported timeout in
    //    StartSession, which is {1} ms."
    //
    // Measured byte-identical on the two DISTINCT Language.dll binaries in the artifact set
    // (sha256 0de0bc55… covering 27.0/27.5, and 37fd45cc… covering 28.4).
    private const string BcMessageSubstring =
        "is longer than the maximum supported timeout in StartSession";

    [Fact]
    public void ATimeoutAboveIntMaxValue_Raises()
    {
        var ex = Record.Exception(
            () => BcRuntime.ValidateStartSessionTimeoutMs(OverMs));

        Assert.NotNull(ex);
        Assert.Contains(BcMessageSubstring, ex!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRaisedException_IsBcsOwnNavNCLArgumentOutOfRangeException()
    {
        // The TYPE is load-bearing, not decoration: BC's throw sits INSIDE the try whose catch
        // returns false for a NavBaseException under DataError.TrapError. Measured with Cecil on
        // Microsoft.Dynamics.Nav.Types.dll for two distinct binaries (27.5 and 28.4):
        //   NavNCLArgumentOutOfRangeException -> NavNCLException -> NavException
        //     -> NavBaseException -> System.Exception
        // So a wrong exception type would silently change TrapError's answer from false to a
        // propagating throw, which is a different defect from throwing nothing.
        var ex = Record.Exception(
            () => BcRuntime.ValidateStartSessionTimeoutMs(OverMs));

        Assert.NotNull(ex);
        Assert.Equal(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLArgumentOutOfRangeException",
            ex!.GetType().FullName);
    }

    [Fact]
    public void TheMessage_NamesBothTheOfferedValueAndTheCap()
    {
        // BC formats Lang.TooLargeTimeoutALStartSession with {0} = the offered timeout and
        // {1} = int.MaxValue. Pinning both numbers is what stops a "faithful" message that
        // silently reports the cap as the offered value, or omits the offered value entirely —
        // either would read as correct in a log while telling the AL author nothing actionable.
        var ex = Record.Exception(
            () => BcRuntime.ValidateStartSessionTimeoutMs(OverMs));

        Assert.NotNull(ex);
        Assert.Contains(OverMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ex!.Message, StringComparison.Ordinal);
        Assert.Contains(MaxMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimeoutExactlyAtIntMaxValue_IsAccepted()
    {
        // The negative direction, and the half a one-sided test cannot distinguish: BC compares
        // with > rather than >=, so the cap itself is legal. A guard written with >= would pass
        // every "it raises" test above while breaking an ordinary caller sitting on the bound.
        BcRuntime.ValidateStartSessionTimeoutMs(MaxMs);
    }

    [Theory]
    [InlineData(0L)]          // AL's default/blank duration
    [InlineData(1L)]
    [InlineData(60_000L)]     // a minute, an ordinary caller
    public void AnOrdinaryTimeout_IsAccepted(long ms)
    {
        // Guards the direction that matters most in practice: this arm must not convert every
        // StartSession with a sane timeout into a refusal.
        BcRuntime.ValidateStartSessionTimeoutMs(ms);
    }

    [Fact]
    public void ANullTimeout_IsAccepted_BecauseBcFallsBackToItsDefaultBackgroundTimeout()
    {
        // BC's null-coalesce (timeout?.Value ?? NavSession.DefaultBackgroundTimeout) means an
        // absent argument can never trip the check — the default is far below the cap. Passing
        // null here is the runner's spelling of "no argument supplied".
        BcRuntime.ValidateStartSessionTimeoutMs(null);
    }
}
