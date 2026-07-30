using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The JmpHook layer is OFF by default (Cecil-only). Every <c>Hook(...)</c> call site therefore
/// routes into <see cref="JmpHook.Apply"/> and returns immediately — which is correct ONLY when
/// the target method has actually been migrated to a Cecil IL rewrite (i.e. it is in
/// <see cref="NclCecilRewrite.CecilOwned"/>). A registered patch that is owned by NEITHER
/// mechanism silently disappears: BC's unpatched body runs and typically NREs deep inside Ncl,
/// with nothing in the log pointing back at the missing patch.
///
/// That is exactly how the Pageworks <c>NavTestPageBase.ALGoToRecord</c> cluster presented
/// (14 tests, bare NullReferenceException inside BC's own body, no runner frame on the stack).
/// The migration debt is accepted and tracked — but it must be MEASURABLE, not invisible.
/// <see cref="JmpHook.OrphanedHooks"/> records those call sites so the audit can name them.
/// </summary>
public class JmpHookOrphanAuditTests
{
    // A stand-in "original" and "replacement". Reflection over methods on this test type is
    // enough — Apply() records the orphan before it ever touches native code, and the JmpHook
    // layer is disabled by default in the test process, so nothing is actually patched.
    private static int SampleOriginal(int a) => a;
    private static int SampleReplacement(int a) => a + 1;

    private static MethodInfo Original =>
        typeof(JmpHookOrphanAuditTests).GetMethod(nameof(SampleOriginal),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static MethodInfo Replacement =>
        typeof(JmpHookOrphanAuditTests).GetMethod(nameof(SampleReplacement),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void HookOwnedByNeitherMechanism_IsRecordedAsOrphaned()
    {
        JmpHook.ResetOrphanAudit();

        JmpHook.Apply(Original, Replacement, "TestType.SampleOriginal");

        Assert.Contains(JmpHook.OrphanedHooks, o => o.StartsWith("TestType.SampleOriginal"));
    }

    [Fact]
    public void OrphanAudit_RecordsTheCecilKey_SoTheFixIsGreppable()
    {
        JmpHook.ResetOrphanAudit();

        JmpHook.Apply(Original, Replacement, "TestType.SampleOriginal");

        // The audit must name the exact CecilOwned key the maintainer has to add / implement,
        // otherwise "orphaned" is not actionable.
        var expectedKey = NclCecilRewrite.Key(Original);
        Assert.Equal("AlRunner.Tests.JmpHookOrphanAuditTests::SampleOriginal/1", expectedKey);
        Assert.Contains(JmpHook.OrphanedHooks, o => o.Contains(expectedKey));
    }

    [Fact]
    public void CecilOwnedHook_IsNotReportedAsOrphaned()
    {
        JmpHook.ResetOrphanAudit();

        // Pick any real entry from the live registry — a Cecil-owned method is fully patched,
        // so its skipped JmpHook is correct and must NOT be flagged.
        var ownedKey = NclCecilRewrite.CecilOwned.First();

        Assert.DoesNotContain(JmpHook.OrphanedHooks, o => o.Contains(ownedKey));
    }

    [Fact]
    public void ResetOrphanAudit_ClearsPreviouslyRecordedOrphans()
    {
        JmpHook.ResetOrphanAudit();
        JmpHook.Apply(Original, Replacement, "TestType.SampleOriginal");
        Assert.NotEmpty(JmpHook.OrphanedHooks);

        JmpHook.ResetOrphanAudit();

        Assert.Empty(JmpHook.OrphanedHooks);
    }
}
