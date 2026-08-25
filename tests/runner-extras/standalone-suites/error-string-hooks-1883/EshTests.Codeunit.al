/// #1883 follow-up to #1800/#1947/#1970/#1990 — two more clusters of orphaned `Hook(...)`
/// (JmpHook) registrations in AlRunner/BcRuntime.cs, measured via `AL_RUNNER_HOOK_AUDIT=1`
/// against the al-language corpus. JmpHook is disabled by default (see
/// AlRunner/Infrastructure/JmpHook.cs), so all of these were silent no-ops — BC's real,
/// unpatched bodies ran instead, and the corpus (2128/2128) was already green before this PR
/// touched anything.
///
/// Cluster 1 — ALSystemErrorHandling (4 registrations: get_ALGetLastErrorText,
/// get_ALGetLastErrorCode, get_ALGetLastErrorCallStack, ALClearLastError). The deleted
/// registrations' own comment claimed the real getters chain through
/// NavCurrentThread.Session, which is null on the skeleton thread and NREs — stale:
/// NavCurrentThread.Session is already wired to _skeletonSession (see
/// RecordPatches.WireNavCurrentThreadSession). Proven directly by
/// error-handling/TestGetLastError.al's GetLastErrorText_AfterClearLastError_ReturnsEmpty and
/// GetLastErrorCode_WithoutError_ReturnsEmpty (both assert exact values), plus
/// codeunit/TestCodeunitAlCallStack.al's CallStack_AfterAssertError_ContainsALFrames — all
/// passing today with the registration already inert. Deleted outright (MiscPatches'
/// ALSystemErrorHandling_get_*/_ALClearLastError replacements are now unused too).
///
/// Cluster 2 — ALSystemString (2 registrations: ALLowercase, ALUppercase). Same stale
/// NavCurrentThread.Session claim. Proven by text/TestTextOperations.al's
/// Text_LowerCase_ConvertsToLower / Text_UpperCase_ConvertsToUpper (exact-value asserts),
/// passing today with the registration already inert. Deleted outright (NavRecordRefPatches'
/// ALSystemString_AL{Lowercase,Uppercase} replacements are now unused too).
///
/// Bonus cleanup — 3 further Hook(...) call sites (NavHttpClient.get_Target,
/// NavHttpRequestMessage.get_Target, NavHttpResponseMessageBase.get_Target) were provably
/// REDUNDANT, not orphaned: all three are ALSO Cecil-owned (NclCecilRewrite.cs rewrites each
/// real getter's IL to call the exact same BcRuntime helper this JmpHook registration would
/// have redirected to), so JmpHook.Apply's CecilOwned check auto-skips them in every
/// configuration, not just the default. No behaviour to guard here — deleting a call site that
/// was already provably inert under both JmpHook-enabled and JmpHook-disabled configurations
/// changes nothing observable. (Contrast with the sibling single-site-orphaned-hooks-1883 suite,
/// which covers the one HttpRequestMessage.get_Target case that WAS a genuine bug.)
///
/// The tests below are regression guards with a narrower runner-mechanism claim than the full
/// BC behaviour: "no redirect fires here, and BC's real, unpatched body completes correctly" —
/// not a re-proof of the underlying GetLastError/LowerCase/UpperCase contracts, which are
/// already proven upstream by the corpus tests cited above. Same framing as
/// single-site-orphaned-hooks-1883/SsohTests.Codeunit.al and isolated-storage-1883/
/// IstTests.Codeunit.al (see bc-behavior-tests-go-upstream.md).
codeunit 60705 "ESH Tests"
{
    Subtype = Test;

    // No Codeunit Assert dependency in this app (see the sibling suites in this consolidated
    // app.json) — plain Error() with an explicit condition check plays the same role.

    // ── ALSystemErrorHandling.ALClearLastError + get_ALGetLastErrorText ──────────────────────
    // Ties both deleted hooks together in the exact flow error-handling/TestGetLastError.al's
    // GetLastErrorText_AfterClearLastError_ReturnsEmpty already proves against the corpus.
    [Test]
    procedure GetLastErrorText_AfterClearLastError_RealBcBody_ReturnsEmpty()
    var
        TextBefore: Text;
        TextAfter: Text;
    begin
        asserterror Error('ESH probe error');
        TextBefore := GetLastErrorText();
        if TextBefore = '' then
            Error('GetLastErrorText() must be non-empty immediately after an AL error');

        ClearLastError();
        TextAfter := GetLastErrorText();
        if TextAfter <> '' then
            Error('GetLastErrorText() must be empty after ClearLastError(), got: %1', TextAfter);
    end;

    // ── ALSystemErrorHandling.get_ALGetLastErrorCode ──────────────────────────────────────────
    [Test]
    procedure GetLastErrorCode_WithoutError_RealBcBody_ReturnsEmpty()
    var
        Code: Text;
    begin
        ClearLastError();
        Code := GetLastErrorCode();
        if Code <> '' then
            Error('GetLastErrorCode() must be empty with no prior error, got: %1', Code);
    end;

    // ── ALSystemString.ALLowercase / ALUppercase ──────────────────────────────────────────────
    [Test]
    procedure LowerCaseUpperCase_RealBcBody_RoundTrips()
    var
        Lower: Text;
        Upper: Text;
    begin
        Lower := LowerCase('ESH-Probe-MiXeD');
        if Lower <> 'esh-probe-mixed' then
            Error('LowerCase() must lowercase the whole string, got: %1', Lower);

        Upper := UpperCase('ESH-Probe-MiXeD');
        if Upper <> 'ESH-PROBE-MIXED' then
            Error('UpperCase() must uppercase the whole string, got: %1', Upper);
    end;
}
