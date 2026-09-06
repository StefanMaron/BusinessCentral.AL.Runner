// BcEngineSkipAttributionTests — issue #3078.
//
// The claim under test is NOT "BC does X". Nothing here asserts anything about Business
// Central: BcEngineSkipReason is test-harness plumbing that decides what a skipped row in
// the bc-engine-serial collection tells the developer reading it. There is no service
// tier that could adjudicate the wording of an xUnit skip message, so this test belongs
// here and not in the upstream AL-language corpus
// (.claude/rules/bc-behavior-tests-go-upstream.md).
//
// Every condition below is CONSTRUCTED on purpose. None of it reads whatever this
// particular box happens to have provisioned — a test that asserted over the ambient
// engine state would pass on a machine where the engine is ready and prove nothing about
// the message a machine where it is NOT ready would print. The two source-scanning guards
// at the bottom carry their own fixture guards (assert the file was found and is
// substantial) so they cannot rot into vacuously passing scans, which is the exact defect
// #3017 records and the reason #3078 was filed.

using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcEngineSkipAttributionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // ---- Describe: the real exception must survive, not the wrapper's fixed string ----

    /// <summary>
    /// The measured RED. A [ModuleInitializer] that calls BcRuntime.EnsureApplied() through
    /// reflection surfaces every failure as TargetInvocationException, whose Message is the
    /// constant "Exception has been thrown by the target of an invocation." — carrying no
    /// type, no message, no diagnosis. That is verbatim what a local run printed at
    /// `--logger "console;verbosity=normal"` before this fix.
    /// </summary>
    [Fact]
    public void Describe_UnwrapsTargetInvocationException_ToTheRealCause()
    {
        var inner = new PlatformNotSupportedException("Windows Principal functionality is not supported on this platform.");
        var wrapped = new TargetInvocationException(inner);

        var described = BcEngineSkipReason.Describe(wrapped);

        Assert.Contains("PlatformNotSupportedException", described, StringComparison.Ordinal);
        Assert.Contains("Windows Principal functionality is not supported on this platform.", described, StringComparison.Ordinal);
        // The wrapper's own placeholder message must NOT be what the developer is shown.
        Assert.DoesNotContain("Exception has been thrown by the target of an invocation", described, StringComparison.Ordinal);
        // The wrapper is still named, so the reflection boundary is not hidden either.
        Assert.Contains("TargetInvocationException", described, StringComparison.Ordinal);
    }

    /// <summary>Nested wrappers: reflection over an async/aggregating path stacks two of them.</summary>
    [Fact]
    public void Describe_UnwrapsNestedWrappers_ToTheInnermostCause()
    {
        var innermost = new BadImageFormatException("Index not found. (0x80131124)");
        var wrapped = new TargetInvocationException(new AggregateException("one or more errors", innermost));

        var described = BcEngineSkipReason.Describe(wrapped);

        Assert.Contains("BadImageFormatException", described, StringComparison.Ordinal);
        Assert.Contains("Index not found. (0x80131124)", described, StringComparison.Ordinal);
        Assert.DoesNotContain("one or more errors", described, StringComparison.Ordinal);
    }

    /// <summary>Negative direction: an exception that is NOT a wrapper is reported as itself.</summary>
    [Fact]
    public void Describe_LeavesAnUnwrappedExceptionAlone()
    {
        var described = BcEngineSkipReason.Describe(new InvalidOperationException("artifacts root is empty"));

        Assert.Equal("InvalidOperationException: artifacts root is empty", described);
    }

    /// <summary>
    /// A wrapper with no inner exception must not degrade to the useless placeholder
    /// message either — it is reported as itself, wrapper name included.
    /// </summary>
    [Fact]
    public void Describe_WrapperWithNoInner_ReportsTheWrapperItself()
    {
        var described = BcEngineSkipReason.Describe(new TargetInvocationException("outer detail", null));

        Assert.Contains("TargetInvocationException", described, StringComparison.Ordinal);
        Assert.Contains("outer detail", described, StringComparison.Ordinal);
    }

    // ---- Format: every reason names the collection, the cause and the remedy ----

    /// <summary>
    /// The property that makes a skip attributable, asserted over EVERY declared cause
    /// rather than the two that happen to fire on the author's box. A cause added later
    /// with no remedy fails here instead of shipping another silent skip.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCauses))]
    public void Format_NamesTheCollection_TheDetail_AndAnActionableRemedy(BcEngineSkipCause cause)
    {
        const string detail = "SENTINEL-DETAIL-9f3c";

        var reason = BcEngineSkipReason.Format(cause, detail);

        // Which collection stopped running.
        Assert.Contains(BcEngineCollection.Name, reason, StringComparison.Ordinal);
        // That it is a skip and therefore asserted nothing — the thing a green summary hides.
        Assert.Contains("SKIP", reason, StringComparison.OrdinalIgnoreCase);
        // Why.
        Assert.Contains(detail, reason, StringComparison.Ordinal);
        Assert.Contains(cause.ToString(), reason, StringComparison.Ordinal);
        // What would make it run.
        Assert.Contains(BcEngineSkipReason.BootstrapTool, reason, StringComparison.Ordinal);
        Assert.Contains("Remedy", reason, StringComparison.Ordinal);
    }

    public static TheoryData<BcEngineSkipCause> AllCauses()
    {
        var data = new TheoryData<BcEngineSkipCause>();
        foreach (var cause in Enum.GetValues<BcEngineSkipCause>()) data.Add(cause);
        return data;
    }

    /// <summary>
    /// Fixture guard for the theory above: it is only meaningful while there is more than
    /// one cause to enumerate. If the enum were ever collapsed to a single value the
    /// theory would still pass while covering almost nothing.
    /// </summary>
    [Fact]
    public void AllCauses_CoversEveryBranchThatCanLeaveTheEngineUnready()
    {
        var causes = Enum.GetValues<BcEngineSkipCause>();

        Assert.True(causes.Length >= 7,
            $"BcEngineSkipCause declares only {causes.Length} values; the attribution theory needs one per " +
            "branch in BcEngineBootstrap.Initialize that can leave Ready false.");
        Assert.Contains(BcEngineSkipCause.NclPreloaded, causes);
        Assert.Contains(BcEngineSkipCause.CecilCacheCold, causes);
        Assert.Contains(BcEngineSkipCause.BootstrapThrew, causes);
    }

    /// <summary>
    /// Two different causes must not produce the same remedy text, or "attributable" is a
    /// word rather than a property: a developer whose Cecil cache is cold and one whose
    /// startup hooks are unwired need different next steps.
    /// </summary>
    [Fact]
    public void Format_GivesNclPreloadedAndCecilCacheCold_DistinctDiagnoses()
    {
        var preloaded = BcEngineSkipReason.Format(BcEngineSkipCause.NclPreloaded, "d");
        var cold = BcEngineSkipReason.Format(BcEngineSkipCause.CecilCacheCold, "d");

        Assert.NotEqual(preloaded, cold);
        // The startup-hook diagnosis must name the mechanism that is missing.
        Assert.Contains("DOTNET_STARTUP_HOOKS", preloaded, StringComparison.Ordinal);
        Assert.DoesNotContain("DOTNET_STARTUP_HOOKS", cold, StringComparison.Ordinal);
    }

    /// <summary>
    /// No silent default: an undeclared cause must throw rather than quietly produce a
    /// reason with no remedy in it (.claude/rules/loud-failures.md).
    /// </summary>
    [Fact]
    public void Format_ThrowsOnAnUndeclaredCause_RatherThanEmittingARemedylessReason()
    {
        var ex = Record.Exception(() => BcEngineSkipReason.Format((BcEngineSkipCause)9999, "d"));

        Assert.NotNull(ex);
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    // ---- OrDefault: !Ready implies an attributable reason, with no exceptions ----

    /// <summary>
    /// The sibling instance of the reported defect. 132 call sites across the
    /// bc-engine-serial collection spell
    /// <c>_engine.SkipReason ?? "the in-process BC engine is not ready (see
    /// BcEngineCollection)."</c>. That fallback fires whenever the bootstrap recorded no
    /// reason — the [ModuleInitializer] never having run, which is the #1813 shape itself —
    /// and it is accurate, remedy-less and therefore exactly as silent as the message this
    /// issue is about. BcEngineFixture.SkipReason is total now, so the fallback is
    /// unreachable; this pins the replacement rather than trusting 132 edits nobody made.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OrDefault_TurnsAnAbsentReason_IntoAnAttributableOne(string? absent)
    {
        var reason = BcEngineSkipReason.OrDefault(absent);

        Assert.Contains(BcEngineCollection.Name, reason, StringComparison.Ordinal);
        Assert.Contains(nameof(BcEngineSkipCause.BootstrapDidNotRun), reason, StringComparison.Ordinal);
        Assert.Contains(BcEngineSkipReason.BootstrapTool, reason, StringComparison.Ordinal);
        Assert.Contains("Remedy", reason, StringComparison.Ordinal);
    }

    /// <summary>Negative direction: a real reason is passed through untouched.</summary>
    [Fact]
    public void OrDefault_LeavesARecordedReasonAlone()
    {
        var recorded = BcEngineSkipReason.Format(BcEngineSkipCause.CecilCacheCold, "the cache missed.");

        Assert.Equal(recorded, BcEngineSkipReason.OrDefault(recorded));
    }

    /// <summary>
    /// The invariant itself, stated over the type rather than over this box's state:
    /// BcEngineFixture.SkipReason must be non-nullable, so no caller can reach a null and
    /// substitute a fallback of its own. Reading the ambient fixture instead would assert
    /// nothing on a machine where the engine came up.
    /// </summary>
    [Fact]
    public void FixtureSkipReason_IsNotNullable_SoNoCallerCanSubstituteABareFallback()
    {
        var property = typeof(BcEngineFixture).GetProperty(nameof(BcEngineFixture.SkipReason));
        Assert.NotNull(property);

        var nullability = new NullabilityInfoContext().Create(property!);
        Assert.Equal(NullabilityState.NotNull, nullability.ReadState);
    }

    // ---- Guards: the remedy must exist, and nothing may bypass the formatter ----

    /// <summary>
    /// A remedy naming a path that is not in the repository is silence with extra steps.
    /// Asserts the tool exists, is not a stub, and is executable — the last one because a
    /// non-executable script fails with "Permission denied", which reads like a broken
    /// remedy rather than a missing chmod.
    /// </summary>
    [Fact]
    public void TheRemedyNamedByEveryReason_ExistsAndIsExecutable()
    {
        var tool = Path.Combine(RepoRoot, BcEngineSkipReason.BootstrapTool.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(tool), $"Every bc-engine-serial skip reason points at '{tool}', which does not exist.");
        Assert.True(new FileInfo(tool).Length > 500, $"'{tool}' is too small to be performing the engine bootstrap.");

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(tool);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute), $"'{tool}' is not executable (mode {mode}).");
        }
    }

    /// <summary>
    /// The drift guard. Every SkipReason assignment in BcEngineCollection.cs must go
    /// through BcEngineSkipReason.Format, or a future branch reintroduces exactly the bare,
    /// remedy-less string this issue is about — and every assertion above would still pass
    /// while the real run went quiet again. Same shape as the artifact-gate drift guard in
    /// TestArtifactsGateTests.
    /// </summary>
    [Fact]
    public void EverySkipReasonAssignment_GoesThroughTheFormatter()
    {
        var source = Path.Combine(RepoRoot, "AlRunner.Tests", "BcEngineCollection.cs");

        // Fixture guard first: a scan of a file that is not there passes vacuously.
        Assert.True(File.Exists(source), $"'{source}' not found — this guard would otherwise scan nothing and pass.");
        var text = File.ReadAllText(source);
        Assert.True(text.Length > 4000, $"'{source}' is only {text.Length} chars; the guard is scanning the wrong file.");
        Assert.Contains("BcEngineBootstrap", text, StringComparison.Ordinal);

        // `SkipReason =` but not `SkipReason =>` (the fixture's expression-bodied forward)
        // and not the auto-property declaration (which has no `=` at all).
        var assignments = Regex.Matches(text, @"SkipReason\s*=(?!=|>)\s*(?<rhs>[^\r\n]*)")
            .Select(m => m.Groups["rhs"].Value.Trim())
            .ToList();

        Assert.True(assignments.Count >= 4,
            $"Found only {assignments.Count} SkipReason assignments in BcEngineCollection.cs; " +
            "the regex has drifted away from the source and this guard is measuring nothing.");

        var bare = assignments
            .Where(rhs => !rhs.StartsWith("BcEngineSkipReason.", StringComparison.Ordinal))
            .ToList();

        Assert.True(bare.Count == 0,
            "Every bc-engine-serial skip reason must be built by BcEngineSkipReason.Format so it names the " +
            "collection, the cause and the remedy (issue #3078). These assignments bypass it:\n  " +
            string.Join("\n  ", bare));
    }

    /// <summary>
    /// The bootstrap tool and .github/workflows/bc-tests.yml must agree on the two facts
    /// that make the engine collection run: the startup-hook chain (al-runner.dll first,
    /// then AlRunner.Tests.dll — the order matters, see EngineStartupHook.cs) and the
    /// warm-up bundle. A tool that drifts from what CI does sends a local developer to a
    /// bootstrap that no longer matches the one measurement that is known to work.
    /// </summary>
    [Fact]
    public void BootstrapTool_AndCiWorkflow_AgreeOnTheStartupHookChain()
    {
        var tool = File.ReadAllText(Path.Combine(RepoRoot, "tools", "engine-test-bootstrap.sh"));
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "bc-tests.yml"));

        Assert.True(workflow.Length > 10000, "bc-tests.yml is unexpectedly small — this guard is reading the wrong file.");

        foreach (var (text, what) in new[] { (tool, "tools/engine-test-bootstrap.sh"), (workflow, ".github/workflows/bc-tests.yml") })
        {
            Assert.True(text.Contains("DOTNET_STARTUP_HOOKS", StringComparison.Ordinal),
                $"{what} no longer mentions DOTNET_STARTUP_HOOKS.");
            // Both files also MENTION the variable in prose, so match every occurrence and
            // keep the ones that actually carry a hook chain (a `.dll` on the same line).
            var valueLines = Regex.Matches(text, @"<DOTNET_STARTUP_HOOKS>[^\r\n]*")
                .Select(m => m.Value)
                .Where(v => v.Contains(".dll", StringComparison.Ordinal))
                .ToList();

            Assert.True(valueLines.Count > 0,
                $"{what} has no <DOTNET_STARTUP_HOOKS> element carrying a hook chain — only prose. " +
                "Either the wiring is gone or this guard is reading the wrong file.");

            foreach (var line in valueLines)
            {
                var alRunnerAt = line.IndexOf("al-runner.dll", StringComparison.Ordinal);
                var testsAt = line.IndexOf("AlRunner.Tests.dll", StringComparison.Ordinal);
                Assert.True(alRunnerAt >= 0 && testsAt >= 0,
                    $"{what}'s DOTNET_STARTUP_HOOKS does not name both hook assemblies: '{line}'");
                Assert.True(alRunnerAt < testsAt,
                    $"{what} has the startup-hook chain in the wrong order — al-runner.dll must precede " +
                    $"AlRunner.Tests.dll (see AlRunner/EngineTestBinResolverStartupHook.cs): '{line}'");
            }
        }
    }
}
