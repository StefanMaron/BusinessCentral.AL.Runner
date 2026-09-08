// TestExplicitCommitNotAllowedMessageTests — the half of issue #3451's refusal that AL cannot
// decide, mirroring CommitProhibitedMessageTests for the CommitBehavior::Error branch (#3449).
//
// ALDatabasePatches.BuildTestExplicitCommitNotAllowed prefers BC's own parameterless
// NavTestExplicitCommitNotAllowedException constructor, which builds its message from
// Lang.TestExplicitCommitNotAllowed, and falls back to a hardcoded copy of BC 28.4's en-US
// string. The two are byte-identical, so an AL assertion on the text passes either way and
// TransactionModelCommitRefusalTests cannot tell a resolved resource from a degraded fallback.
// Two things here can: whether the resource resolves at all, and whether the exception is BC's
// own type rather than the plain InvalidOperationException the last fallback returns.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class TestExplicitCommitNotAllowedMessageTests
{
    private readonly BcEngineFixture _engine;

    public TestExplicitCommitNotAllowedMessageTests(BcEngineFixture engine) => _engine = engine;

    /// <summary>
    /// Same reflection route ALDatabasePatches.LangString uses, duplicated on purpose — see
    /// CommitProhibitedMessageTests.LangString for why calling the production helper would
    /// agree with it by construction, including when both are wrong.
    /// </summary>
    private static string? LangString(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var lang = asm.GetType("Microsoft.Dynamics.Nav.Common.Language.Lang", throwOnError: false);
            var value = lang?.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            ?.GetValue(null) as string;
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    [SkippableFact]
    public void TestExplicitCommitNotAllowed_ResolvesFromBcsOwnLangResource()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var resource = LangString("TestExplicitCommitNotAllowed");
        Assert.False(string.IsNullOrEmpty(resource),
            "Lang.TestExplicitCommitNotAllowed not found — Microsoft.Dynamics.Nav.Language.dll shape "
            + "changed, so ALDatabasePatches.BuildTestExplicitCommitNotAllowed is silently shipping "
            + "its fallback string.");

        // Anchor the content too, so a rename that left SOME string under this property cannot
        // pass: the corpus test (codeunit 60899, tests 04-08) matches on this exact sentence.
        Assert.Equal(
            "Tests cannot call the Commit function if TransactionModel property is set to AutoRollback.",
            resource);
    }

    [SkippableFact]
    public void BuildTestExplicitCommitNotAllowed_ThrowsBcsOwnExceptionTypeCarryingThatText()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ex = ALDatabasePatches.BuildTestExplicitCommitNotAllowed();

        // The TYPE is the discriminator the message cannot be: the last fallback returns
        // InvalidOperationException, and BC throws NavTestExplicitCommitNotAllowedException.
        Assert.Equal("NavTestExplicitCommitNotAllowedException", ex.GetType().Name);
        Assert.Equal(LangString("TestExplicitCommitNotAllowed"), ex.Message);
    }
}
