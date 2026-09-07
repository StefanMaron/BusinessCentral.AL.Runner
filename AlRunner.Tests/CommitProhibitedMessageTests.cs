// CommitProhibitedMessageTests — the half of issue #3449's Error branch that AL cannot decide.
//
// ALDatabasePatches.BuildCommitProhibited reads BC's own Lang.CommitProhibited by reflection
// and falls back to a hardcoded copy of BC 28.4's en-US string when that read fails. The two
// are byte-identical, which is the point: an AL assertion on the message text passes either
// way, so CommitBehaviorAttributeTests cannot tell a resolved resource from a degraded
// fallback, and neither can the corpus. Two things here can:
//
//   - Lang.CommitProhibited resolves at all. A BC rename would otherwise leave every caller
//     silently on the fallback, correct today and wrong the day Microsoft edits the string.
//   - the exception is BC's own NavCSideException. The fallback path returns a plain
//     InvalidOperationException, so the TYPE is what distinguishes the two routes.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CommitProhibitedMessageTests
{
    private readonly BcEngineFixture _engine;

    public CommitProhibitedMessageTests(BcEngineFixture engine) => _engine = engine;

    /// <summary>
    /// Same reflection route ALDatabasePatches.LangString uses, duplicated here on purpose:
    /// a test that called the production helper would agree with it by construction, including
    /// when both are wrong. Mirrors CodeunitRunWriteTransactionRefusalTests.LangString.
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
    public void CommitProhibited_ResolvesFromBcsOwnLangResource()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var resource = LangString("CommitProhibited");
        Assert.False(string.IsNullOrEmpty(resource),
            "Lang.CommitProhibited not found — Microsoft.Dynamics.Nav.Language.dll shape changed, "
            + "so ALDatabasePatches.BuildCommitProhibited is silently shipping its fallback string.");

        // Anchor the resource's content too, so a rename that left SOME string under this
        // property cannot pass: the corpus test (codeunit 60881) matches on this substring.
        Assert.Contains("Commit is prohibited in the current scope", resource);
    }

    [SkippableFact]
    public void BuildCommitProhibited_ThrowsBcsOwnExceptionTypeCarryingThatText()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var ex = ALDatabasePatches.BuildCommitProhibited();

        // The TYPE is the discriminator the message cannot be: the fallback route returns
        // InvalidOperationException, and BC throws NavCSideException.
        Assert.Equal("NavCSideException", ex.GetType().Name);
        Assert.Equal(LangString("CommitProhibited"), ex.Message);
    }
}
