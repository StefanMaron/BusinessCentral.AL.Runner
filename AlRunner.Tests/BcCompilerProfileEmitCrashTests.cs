// BcCompilerProfileEmitCrashTests — issue #2238.
//
// A `profile` object referencing a RoleCenter page that never resolves crashes BC's
// own ProfileMetadataEmitter: a NullReferenceException deep inside
// Microsoft.Dynamics.Nav.CodeAnalysis.SymbolExtensions.ShouldBeEmitted, reached via
// ObjectMetadataEmitHelper.WriteAttributeProperties / ProfileMetadataEmitter.
// WriteProfileHeader — not a clean AL0185 diagnostic stop. Compilation.Emit is atomic
// per module (see BcCompilerEmitRetryTests.cs's header for the general shape), so
// before this fix the crash took the WHOLE module's Emit down (EMIT-ZERO), including
// a perfectly healthy codeunit declared alongside the broken profile.
//
// Two separate gaps in BcCompiler's existing crash-recovery machinery combined to
// make this crash unrecoverable specifically for `profile` objects:
//
//   1. `_failingObjectRx` required the crashing object's name to be quoted in BC's
//      exception text. A bare identifier with no spaces (e.g. "TestRoleCenter") is
//      rendered WITHOUT quotes by BC's own ToDisplayString, so the regex matched zero
//      objects and the retry loop broke immediately.
//   2. `DeclaresObject` required a numeric object ID between the type keyword and the
//      name (`codeunit 134688 "Connector Mock"`). A `profile` object has NO numeric ID
//      at all (`profile TestRoleCenter { ... }` — profiles are looked up purely by
//      name in AL) — so even when the crashing object COULD be named from the
//      exception text, it could never be mapped back to its own source file to
//      exclude.
//
// This test drives BcCompiler.Emit directly (mirroring BcCompilerEmitRetryTests.cs's
// shape) with a minimal repro: one profile engineered to crash the emitter, and one
// healthy codeunit. RED before the fix: 0 sources (the healthy codeunit is lost too).
// GREEN after: the healthy codeunit's source survives.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerProfileEmitCrashTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerProfileEmitCrashTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-profile-emit-crash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void Emit_RecoversHealthyCodeunit_WhenAProfileReferencesAnUnresolvedRoleCenter()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // A profile whose RoleCenter page is never declared anywhere in this compile —
        // BC's own emitter crashes on this, not a runner substitute.
        File.WriteAllText(Path.Combine(_root, "BadProfile.al"), """
            profile "ProfileCrashTest Bad"
            {
                Caption = 'ProfileCrashTest Bad';
                RoleCenter = "ProfileCrashTest Nonexistent Page";
            }
            """);

        // ...alongside a healthy codeunit whose real body must survive the retry.
        File.WriteAllText(Path.Combine(_root, "Good.al"), """
            codeunit 90102 "ProfileCrashTest Good"
            {
                procedure GetAnswer(): Integer
                begin
                    exit(42);
                end;
            }
            """);

        var output = new BcCompiler().Emit(new[] { _root }, "ProfileCrashTestModule");

        Assert.True(
            output.Sources.Count > 0,
            "Expected at least the healthy codeunit's source to survive the crashing profile — " +
            $"got 0 sources (diagnostics: {string.Join(" | ", output.Diagnostics.Take(10))}). This means " +
            "the profile's emitter crash still took down the WHOLE module — the exact atomic-emit " +
            "gap this test guards against (issue #2238).");

        var good = output.Sources.FirstOrDefault(s => s.Code.Contains("ProfileCrashTest Good"));
        Assert.True(
            good != null,
            "Expected the healthy 'ProfileCrashTest Good' codeunit's C# to be present in the " +
            $"emitted sources; got: [{string.Join(", ", output.Sources.Select(s => s.Name))}]");
        Assert.Contains("GetAnswer", good!.Code);

        // The crashing profile itself must actually have been identified and excluded
        // (not merely "happened to survive some other way") — pins the specific
        // regex/DeclaresObject fix, not just the end-to-end outcome.
        Assert.Contains(
            output.ExcludedObjects,
            o => o.StartsWith("Profile ", StringComparison.Ordinal) && o.Contains("ProfileCrashTest Bad"));
    }
}
