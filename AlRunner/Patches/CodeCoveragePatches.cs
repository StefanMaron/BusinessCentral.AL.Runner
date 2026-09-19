// CodeCoveragePatches — the loud refusal on BC's code-coverage START surface (#3517).
//
// AL's CODECOVERAGELOG(TRUE) asks BC to begin recording statement hits. BC's path is
// ALCodeCoverage.ALCodeCoverageLog -> CodeCoverageManager.Start{Single,Multi}SessionCodeCoverage
// -> new {Single,Multi}SessionCodeCoverageRecorder(session, codeEnvironment) -> the shared
// CodeCoverageRecorder ctor, whose first statement is `if (codeEnvironment == null) throw new
// ArgumentNullException("codeEnvironment")`.
//
// That null is structural here, not incidental. `codeEnvironment` is assigned in exactly one
// place in Ncl.dll — NCLMetadata's own constructor, which needs a NavDatabase, the metadata
// loaders and NavGlobal.NavAppGroupResolver — and the runner builds its skeleton NCLMetadata
// with GetUninitializedObject precisely because that constructor cannot run headless
// (MetadataPatches.InjectSkeletonSystemTenant). So no amount of session wiring produces one.
//
// Before this file, AL saw `Value cannot be null. (Parameter 'codeEnvironment')` — a .NET
// parameter name, naming no AL API and citing no doc. Worse, the failure usually surfaced
// somewhere else entirely: BaseApp codeunit 9990 "Code Coverage Mgt." swallows nothing, but a
// library helper that starts coverage, runs work and stops it reports the *stop* ("Code
// coverage is not running") because the start never took. 30 measured failures in #3517, 18 of
// them in one SCM codeunit that is not about coverage at all.
//
// Reason anchor is `not-yet-implemented`, not a permanent scope.md section, and that choice is
// load-bearing: recording statement hits IS reachable in principle — BC emits a StmtHit per AL
// statement and the runner already consumes that stream for its own `--coverage` Cobertura
// report (AlRunner/Infrastructure/AlCoverageReport.cs). What is missing is BC's AL-visible half:
// a real ALCodeEnvironment to assign scope ids through, plus the three CodeCoverageDataProvider
// virtual tables LoadDataIntoVirtualTable fills. `not-yet-implemented` also tears through an AL
// [TryFunction] rather than being trapped into `false` (RunnerOutOfScopeException's header), so
// this gap can never read as a green test.
//
// NOT touched here, deliberately — all three measured WORKING before this fix and are pinned as
// controls in tests/runner-extras/code-coverage-start-oos:
//
//   CODECOVERAGELOG()        answers false, from IsCodeCoverageEnabledForConnection, which is a
//                            null-check over the registered listeners and never reads an
//                            ALCodeEnvironment
//   CODECOVERAGELOG(FALSE)   no-ops, because StopCodeCoverageRecording looks the recorder up and
//                            returns when it is null — real BC behaviour on a session with
//                            nothing recording
//
// Refusing those too would turn #3517's 30 failures into 30 differently-worded ones instead of
// moving them to the cause.
//
// RED → GREEN: tests/runner-extras/code-coverage-start-oos.

using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// Body replacements for the two <c>CodeCoverageManager</c> start members. Each stands in for
/// the whole method and carries BC's argument list; the Cecil rewrite forwards the arguments.
/// </summary>
public static class CodeCoveragePatches
{
    // A doc link that names its own file is used verbatim (#2894), and this surface needs that:
    // scope.md is the manifest of what is PERMANENTLY out of scope, and pointing an in-scope
    // gap there would send the reader to a file with no matching section and assert a
    // permanence that is not true. Reason and link stay separate arguments so
    // RunnerOutOfScopeException appends the " — see <link>" itself; a reason that also spelled
    // the doc out would render it twice (#2766), which
    // tests/runner-extras/code-coverage-start-oos counts rather than merely looks for.
    private const string DocLink = "docs/limitations.md#code-coverage-virtual-tables";

    private const string StartReason =
        "not-yet-implemented — CodeCoverage.Start (AL CODECOVERAGELOG(TRUE)). BC records statement "
        + "hits through a CodeCoverageRecorder built over session.NCLMetadata.CodeEnvironment, and "
        + "the runner's skeleton NCLMetadata has none: ALCodeEnvironment is assigned only by "
        + "NCLMetadata's real constructor, which needs a NavDatabase and the metadata loaders. "
        + "Stopping and querying still work — CODECOVERAGELOG() answers false and "
        + "CODECOVERAGELOG(FALSE) no-ops, as they do on a BC session with nothing recording. The "
        + "runner's own --coverage flag reports statement coverage by a separate route";

    /// <summary>
    /// Replacement for <c>CodeCoverageManager.StartSingleSessionCodeCoverage(NavSession,
    /// ALCodeEnvironment)</c> — the path AL's <c>CODECOVERAGELOG(TRUE)</c> and
    /// <c>CODECOVERAGELOG(TRUE, FALSE)</c> both take.
    ///
    /// <para>Unconditional: BC's real body has no early answer to preserve. It constructs a
    /// recorder for every call, and the second argument it passes is the null this refusal
    /// exists to report.</para>
    /// </summary>
    public static void StartSingleSessionCodeCoverage_Replacement(object? session, object? codeEnvironment)
        => throw new RunnerOutOfScopeException("CodeCoverage.Start", StartReason, DocLink);

    /// <summary>
    /// Replacement for <c>CodeCoverageManager.StartMultiSessionCodeCoverage(NavSession,
    /// ALCodeEnvironment)</c> — the path AL's <c>CODECOVERAGELOG(TRUE, TRUE)</c> takes.
    ///
    /// <para>A separate member rather than a shared one: it is a distinct BC entry point
    /// constructing a distinct recorder type, and a fix aimed only at the single-session member
    /// leaves this one raising BC's bare ArgumentNullException. Both are pinned separately.</para>
    /// </summary>
    public static void StartMultiSessionCodeCoverage_Replacement(object? session, object? codeEnvironment)
        => throw new RunnerOutOfScopeException("CodeCoverage.Start", StartReason, DocLink);
}
