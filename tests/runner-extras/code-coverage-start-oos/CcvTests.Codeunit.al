// Issue #3517 — the runner's refusal on the code-coverage START surface, and its scope.
//
// AL's CODECOVERAGELOG(TRUE) asks BC to start recording statement hits. BC's real path is
// ALCodeCoverage.ALCodeCoverageLog -> CodeCoverageManager.Start{Single,Multi}SessionCodeCoverage
// -> new CodeCoverageRecorder(session, session.NCLMetadata.CodeEnvironment). The runner builds
// its skeleton NCLMetadata with GetUninitializedObject (MetadataPatches.InjectSkeletonSystemTenant),
// so no constructor runs and `codeEnvironment` is null by construction — BC's own
// CodeCoverageRecorder ctor then raised a bare ArgumentNullException naming the parameter
// 'codeEnvironment'. That names no AL API and cites no doc, which is what loud-failures.md
// exists to prevent.
//
// The three scoping controls at the bottom are load-bearing, and each was measured working
// BEFORE this fix: the query form CODECOVERAGELOG() answers false, and CODECOVERAGELOG(FALSE)
// stops cleanly because BC's StopCodeCoverageRecording null-checks its recorder lookup and
// no-ops. An over-broad fix that refused the whole surface by type name would pass the two
// refusal tests above and take those working answers away with it — which is how the 30
// failures in #3517 present in the first place, at Codeunit 9990's Stop() rather than at the
// start that actually failed.
codeunit 65651 "Ccv Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Ccv Assert";

    // --- the refusal: both AL start shapes, single-session and multi-session ---

    [Test]
    procedure StartSingleSession_RefusedByNameNotArgumentNull()
    begin
        asserterror CodeCoverageLog(true, false);

        // The API an AL author wrote, spelled as AL spells it — not ALCodeCoverageLog,
        // not CodeCoverageRecorder..ctor, and not a bare "out-of-scope".
        Assert.ExpectedError('out-of-scope: CodeCoverage.Start');
        Assert.ExpectedError('not-yet-implemented');
        // The pointer must land on limitations.md, not scope.md. scope.md is the manifest of
        // what is PERMANENTLY out of scope; this surface is in scope and unbuilt, so pointing
        // there would send the reader to a file with no matching section and assert a
        // permanence that is not true (RunnerOutOfScopeException.BuildMessage, #2894).
        Assert.ExpectedError('docs/limitations.md#code-coverage-virtual-tables');
        Assert.NotExpectedError('docs/scope.md');
        Assert.ErrorContainsExactlyOnce('see docs/limitations.md');
        // The regression this issue is about: BC's recorder ctor must never be reached,
        // so its bare parameter-name message must not be what AL sees.
        Assert.NotExpectedError('codeEnvironment');
        Assert.NotExpectedError('Value cannot be null');
    end;

    [Test]
    procedure StartMultiSession_RefusedByNameToo()
    begin
        // Multi-session takes a different BC entry point (StartMultiSessionCodeCoverage ->
        // MultiSessionCodeCoverageRecorder) and reached the same null through the same base
        // ctor. A fix aimed only at the single-session recorder leaves this one raw.
        asserterror CodeCoverageLog(true, true);

        Assert.ExpectedError('out-of-scope: CodeCoverage.Start');
        Assert.ExpectedError('not-yet-implemented');
        Assert.NotExpectedError('codeEnvironment');
    end;

    [Test]
    procedure StartOneArgOverload_RefusedByNameToo()
    begin
        // CODECOVERAGELOG(TRUE) is a distinct AL overload that forwards to the two-arg form
        // with multisession=false. It is the spelling BaseApp codeunit 9990 itself uses.
        asserterror CodeCoverageLog(true);

        Assert.ExpectedError('out-of-scope: CodeCoverage.Start');
        Assert.NotExpectedError('codeEnvironment');
    end;

    // --- the controls: the honest answers BC gives without a recorder must survive ---

    [Test]
    procedure QueryForm_StillAnswersFalse_AndDoesNotThrow()
    var
        Active: Boolean;
    begin
        // CODECOVERAGELOG() is a query, not a start. BC answers it from
        // CodeCoverageManager.IsCodeCoverageEnabledForConnection, which is a null-check over
        // the registered listeners — no ALCodeEnvironment involved. It answered false before
        // this fix and must still answer false, not refuse.
        Active := CodeCoverageLog();

        Assert.IsFalse(Active, 'CODECOVERAGELOG() with nothing recording must answer false');
    end;

    [Test]
    procedure StopWithoutStart_StillNoOps()
    begin
        // BC's StopCodeCoverageRecording looks the recorder up and no-ops when it is null.
        // That is real BC behaviour on a session with nothing recording, and the runner must
        // not replace it with a refusal: a refusal here would turn the 30 failures in #3517
        // into 30 differently-worded failures instead of moving them to the start.
        CodeCoverageLog(false, false);
        CodeCoverageLog(false, true);
    end;

    [Test]
    procedure QueryForm_StillAnswersFalse_AfterARefusedStart()
    var
        Active: Boolean;
    begin
        // The refusal must leave no half-registered recorder behind. If the refusal fired
        // after BC had already pushed a listener, this would answer true and the surface
        // would be in a state BC never produces.
        asserterror CodeCoverageLog(true, false);

        Active := CodeCoverageLog();

        Assert.IsFalse(Active, 'a refused start must leave nothing recording');
    end;
}
