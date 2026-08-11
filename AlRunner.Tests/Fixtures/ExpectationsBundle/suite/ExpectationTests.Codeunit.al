/// <summary>
/// One method per ExpectationManifest classification path (#1734). Method names
/// are the selection mechanism: the integration test runs `--test GreenPath` to
/// prove the reclassifying paths exit 0, then the whole codeunit to prove both
/// drift directions fail the run loudly.
/// </summary>
codeunit 60810 "Expct Fixture Tests"
{
    Subtype = Test;

    // ── Green paths: with the paired manifest these four must land the run at exit 0 ──

    [Test]
    procedure GreenPath_PlainPass()
    begin
        // No manifest entry; a normal pass. Concrete assertion so the method
        // cannot pass vacuously.
        if 1 + 2 <> 3 then
            Error('arithmetic broke: 1 + 2 <> 3');
    end;

    [Test]
    procedure GreenPath_OosDeclared()
    begin
        // Manifest declares expect-oos with the matching reason → pass-oos.
        OpenRightOuterJoinQuery();
    end;

    [Test]
    procedure GreenPath_KnownGapDeclared()
    begin
        // Manifest declares expect-fail-known-gap → pass-known-gap.
        Error('simulated known gap: surface not yet implemented in the runner');
    end;

    [Test]
    procedure GreenPath_SkipDeclared()
    begin
        // Manifest declares skip → the runner must never invoke this body.
        // The integration test asserts this marker is ABSENT from the output.
        Error('SKIP-DECLARED TEST BODY RAN - skip must prevent invocation');
    end;

    [Test]
    procedure GreenPath_MessageOosDeclared()
    begin
        // #1743: the OOS throw is Cecil-injected IL, so it arrives as an
        // InvalidOperationException carrying the documented
        // "out-of-scope: HttpClient.Get — external-http — see …" message rather
        // than a typed RunnerOutOfScopeException. Manifest declares expect-oos
        // with Reason external-http → pass-oos.
        GetOverHttp();
    end;

    [Test]
    procedure GreenPath_DivergenceDeclared()
    begin
        // #1741: the runner deliberately answers differently from real BC here.
        // Manifest declares expect-divergence → pass-divergence. NOT an OOS
        // throw and NOT a tracked gap.
        Error('simulated intended divergence: real BC answers, the runner declines by design');
    end;

    // ── Drift paths: each must FAIL the run with the documented diagnostic ──

    [Test]
    procedure Drift_OosEntryButPasses()
    begin
        // Manifest declares expect-oos but the test passes cleanly →
        // "Remove the entry … runner now supports this surface."
        if 2 * 2 <> 4 then
            Error('arithmetic broke: 2 * 2 <> 4');
    end;

    [Test]
    procedure Drift_KnownGapEntryButPasses()
    begin
        // Manifest declares expect-fail-known-gap but the test passes cleanly →
        // "Remove the entry … and close the linked issue."
        if 10 div 2 <> 5 then
            Error('arithmetic broke: 10 div 2 <> 5');
    end;

    [Test]
    procedure Drift_OosThrownButNoEntry()
    begin
        // No manifest entry, but the runner throws RunnerOutOfScopeException →
        // "Add an expect-oos entry … or implement the surface."
        OpenRightOuterJoinQuery();
    end;

    [Test]
    procedure Drift_MessageOosThrownButNoEntry()
    begin
        // Same drift direction for the message-convention shape (#1743): a
        // Cecil-injected OOS throw with no entry must ALSO demand one, otherwise
        // widening expect-oos would open a hole where undeclared OOS surfaces
        // read as ordinary failures.
        GetOverHttp();
    end;

    [Test]
    procedure Drift_OosEntryWrongReason()
    begin
        // Manifest declares expect-oos with Reason email-smtp; the runner throws
        // external-http → the reason mismatch must still FAIL the run. Proves the
        // widened matcher actually compares reasons rather than saying yes to any
        // message that starts with "out-of-scope: ".
        GetOverHttp();
    end;

    [Test]
    procedure Drift_OosEntryNearMissReason()
    begin
        // Manifest declares Reason "external-htt" — one character short of the
        // real anchor. Anchors are compared for EQUALITY, so a prefix must not
        // match.
        GetOverHttp();
    end;

    [Test]
    procedure Drift_OosEntryButPlainFailure()
    begin
        // Manifest declares expect-oos, but this fails for an ordinary reason with
        // no out-of-scope signal anywhere in the exception → must still FAIL.
        Error('plain assertion failure with no scope marker in the message');
    end;

    [Test]
    procedure Drift_DivergenceEntryButPasses()
    begin
        // Manifest declares expect-divergence but the test passes cleanly →
        // "Remove the entry … the runner no longer diverges from BC here."
        if 7 mod 4 <> 3 then
            Error('arithmetic broke: 7 mod 4 <> 3');
    end;

    [Test]
    procedure Drift_DivergenceEntryButOos()
    begin
        // Manifest declares expect-divergence but the runner raises an
        // out-of-scope signal → "Declare it expect-oos instead." Divergence must
        // not become a catch-all that absorbs OOS surfaces.
        OpenRightOuterJoinQuery();
    end;

    local procedure OpenRightOuterJoinQuery()
    var
        RightJoin: Query "Expct Right Join";
    begin
        // Deliberately NOT wrapped in asserterror: the typed
        // RunnerOutOfScopeException must reach the test executor uncaught so
        // the manifest classification sees it.
        RightJoin.Open();
        RightJoin.Read();
        Error('RightOuterJoin query unexpectedly opened - the OOS surface is gone');
    end;

    local procedure GetOverHttp()
    var
        Client: HttpClient;
        Resp: HttpResponseMessage;
    begin
        // HTTP egress is permanently out of scope (docs/scope.md §3.2) and the
        // throw is injected by Cecil into NavHttpClient.ALGet, so it arrives as an
        // InvalidOperationException carrying the message convention — the exact
        // shape #1743 is about. Not wrapped in asserterror: the throw must reach
        // the test executor so the manifest classification sees it.
        Client.Get('http://alrunner.invalid/expectations-fixture', Resp);
        Error('HttpClient.Get unexpectedly succeeded - the OOS surface is gone');
    end;
}
