// RethrowPreservesOriginFrameTests — a rethrown inner exception must arrive with the
// frames below it intact.
//
// Issue #2948. `throw someException;` RESETS the exception's stack trace to the rethrow
// site: every frame between the real origin and that line is erased, and the failure then
// reads as though it started in the runner's own patch code. This is not a cosmetic
// complaint. On #2925 the identical shape at RecordPatches.QueryJoin.cs:204 split one
// defect into what looked like two: twenty-one Tests-SMB tests reported a
// NullReferenceException inside BC's FlowFieldsHelper.GetFilterFromMetaFilterCollection,
// while four more reported the same NRE with no BC frame at all — just the runner's own
// rethrow line. The issue body described those four as a possibly-unrelated second
// cluster. They were the same bug; the rethrow had erased the evidence.
//
// The correct form is ExceptionDispatchInfo, which rethrows the same exception object with
// the same type and message and leaves the captured trace alone. NavReportSync.cs already
// used it at line 607, and FlowFieldPatches.cs already used it forty lines above a site
// that did not — which is exactly why a guard is here and not just a fix.
//
// Two tests, two different jobs:
//   1. a BEHAVIOUR test driving a real runner code path and asserting the origin frames
//      survive the rethrow;
//   2. a SHAPE guard over AlRunner/ so the pattern cannot come back at the sites the
//      behaviour test cannot reach (see its comment for why it cannot).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class RethrowPreservesOriginFrameTests
{
    // Two frames below the reflection Invoke boundary, both NoInlining so their presence in
    // the stack trace is evidence about the rethrow and not about the JIT's inlining mood.
    private sealed class FakeReportInstance
    {
        public bool Finalized;
        public bool ShouldThrow = true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void FinalizeDataItemLoading()
        {
            Finalized = true;
            if (ShouldThrow) DeepestReportFrame();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeepestReportFrame() =>
            throw new InvalidOperationException("report finalize blew up");
    }

    /// <summary>
    /// NavReportSync.CompleteReportConstruction reaches FinalizeDataItemLoading through
    /// MethodInfo.Invoke, so a failure inside it arrives as a TargetInvocationException and
    /// the catch unwraps it. The unwrapping used to be `throw tie.InnerException`.
    ///
    /// This is a real call into the shipping method, not a re-enactment: the fake instance
    /// only has to expose a `FinalizeDataItemLoading` for the reflection lookup to find. It
    /// deliberately has no `DataItems` property and is passed reportId 0, so every earlier
    /// step in that method (EnsureDataItemTreeBuilt, SeedObjectId, SeedSessionSystemTenant)
    /// returns at its own guard without caching any MemberInfo off this fake type — which
    /// would otherwise poison NavReportSync's process-wide reflection caches for every real
    /// report in the same test process.
    /// </summary>
    [Fact]
    public void CompleteReportConstruction_KeepsTheFramesBelowTheRethrow()
    {
        var instance = new FakeReportInstance();

        var ex = Assert.Throws<InvalidOperationException>(
            () => NavReportSync.CompleteReportConstruction(instance, parent: null, reportId: 0));

        // The code path really ran — otherwise this test would prove nothing about it.
        Assert.True(instance.Finalized, "CompleteReportConstruction never reached FinalizeDataItemLoading");

        // Type and message are unchanged by the fix; only the trace differs.
        Assert.Equal("report finalize blew up", ex.Message);

        var trace = ex.StackTrace ?? "";
        Assert.Contains(nameof(FakeReportInstance.FinalizeDataItemLoading), trace, StringComparison.Ordinal);
        Assert.Contains("DeepestReportFrame", trace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction. Without this, the test above would still pass if
    /// CompleteReportConstruction threw for some reason of its own before ever invoking the
    /// method — it pins that the normal path completes and returns.
    /// </summary>
    [Fact]
    public void CompleteReportConstruction_ReturnsNormallyWhenTheInvokedMethodSucceeds()
    {
        var instance = new FakeReportInstance { ShouldThrow = false };

        NavReportSync.CompleteReportConstruction(instance, parent: null, reportId: 0);

        Assert.True(instance.Finalized);
    }

    // `throw <identifier-or-member-access>;` — deliberately not matching `throw new ...`
    // (a fresh exception has no trace to erase) or a bare `throw;` (which preserves).
    private static readonly Regex BareRethrow = new(
        @"^\s*throw\s+(?!new\b)([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*;",
        RegexOptions.Compiled);

    /// <summary>
    /// Shape guard over the whole of AlRunner/.
    ///
    /// The behaviour test above covers one of the six sites #2948 and its siblings span.
    /// The other five are not reachable from a unit test at acceptable cost: NavReportSync's
    /// ReportAdd and CreateReportInstance both cache MethodInfo/PropertyInfo handles off
    /// whatever object they are handed, into process-wide statics that never re-resolve, so
    /// driving them with a fake would leave every subsequent real report in the same process
    /// invoking members declared on a test type. InvokeLayoutForReport is private and reached
    /// only from a layout report's SyncRun. RecordPatches.DateVirtualTable and
    /// FlowFieldPatches both sit behind BC's own record machinery.
    ///
    /// So this guard asserts the property structurally instead of behaviourally: no rethrow
    /// in AlRunner/ discards a stack trace. It is a genuine test — it went red on six real
    /// sites when written, and it is the only thing standing between the five it cannot
    /// drive and a silent reintroduction.
    ///
    /// The one permitted form is a `throw x;` immediately after an
    /// ExceptionDispatchInfo.Throw call, which the compiler still wants as a terminating
    /// statement even though it is unreachable (see BcRuntime.RethrowPreservingStack).
    /// </summary>
    [Fact]
    public void NoRethrowInAlRunnerDiscardsItsStackTrace()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var runnerDir = Path.Combine(repoRoot, "AlRunner");
        Assert.True(Directory.Exists(runnerDir), $"AlRunner source directory not found at {runnerDir}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(runnerDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!BareRethrow.IsMatch(lines[i])) continue;

                // Permitted: the unreachable terminator after an ExceptionDispatchInfo throw.
                var precededByDispatch = false;
                for (var back = i - 1; back >= 0 && back >= i - 3; back--)
                {
                    if (lines[back].Contains("ExceptionDispatchInfo", StringComparison.Ordinal))
                    {
                        precededByDispatch = true;
                        break;
                    }
                }
                if (precededByDispatch) continue;

                offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "`throw <expression>;` resets the exception's stack trace to the rethrow site, erasing "
            + "every frame below it — the defect in #1955, #2925 and #2948. Use "
            + "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw() (or "
            + "BcRuntime.RethrowPreservingStack) instead; for a brand-new exception, throw it "
            + "directly with `throw new ...`.\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The guard above is only worth anything if its regex actually matches the shape it
    /// claims to. Pin both directions on literal strings, so a regex edit that quietly
    /// stops matching cannot leave the guard passing over a real offender.
    /// </summary>
    [Fact]
    public void TheGuardRegexMatchesTheDefectAndNotTheCorrectForms()
    {
        Assert.Matches(BareRethrow, "            throw tie.InnerException;");
        Assert.Matches(BareRethrow, "        throw inner;   // trailing comment");
        Assert.Matches(BareRethrow, "throw ex;");

        Assert.DoesNotMatch(BareRethrow, "            throw;");
        Assert.DoesNotMatch(BareRethrow, "            throw new InvalidOperationException(\"x\");");
        Assert.DoesNotMatch(BareRethrow, "            throw new AlRunner.Infrastructure.RunnerOutOfScopeException(a, b);");
    }
}
