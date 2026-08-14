// ControlAddInFileSystemTests — BcCompiler.Emit must resolve ControlAddIn resource files.
//
// Root cause being guarded
// -------------------------
// BC's compiler reads non-AL files (Scripts/StartupScript/StyleSheets/Images) through an
// injected IFileSystem. BcCompiler.Emit never supplied one, so the compiler could not
// resolve ANY control-add-in resource path — AL0327 "Missing file" fired for every such
// declaration EVEN WHEN THE FILE EXISTS at the declared path, case-exact. This is latent
// on a healthy project (Emit succeeds anyway, so it goes unnoticed), but it turns fatal
// the moment any unrelated object fails to bind: the emit-retry loop treats the add-in's
// AL0327 as a real compile error and excludes its whole source tree along with the
// genuinely broken one, so the add-in's tests silently vanish from the run. See issue
// #1899 for the full reproduction (COMPILE FAIL, 0 tests run, exit 3, for a project where
// only ONE object was actually broken).
//
// Fix: BcCompiler.Emit / EmitDepSymbols now accept an `appRootDir` (the directory holding
// the app's own app.json — NOT the src/ subfolder `alFolders` carries) and attach
// `compilation.WithFileSystem(new NavCA.RelativeFileSystem(appRootDir))` at every
// Compilation.Create site (primary compile, emit-retry compile, dependency-symbol
// compile), so resource paths declared relative to the app root resolve correctly.
//
// Test strategy
// -------------
// Exercise BcCompiler.Emit directly (mirrors BcCompilerEmitRetryTests) with a minimal
// ControlAddIn declaring both `StartupScript` and `Scripts`, and assert on the returned
// BcEmitOutput.Diagnostics — which carries every AL0327 the compile produced (BcCompiler.cs
// folds compilation.GetDeclarationDiagnostics() into it unconditionally, not just under a
// diagnostic env var). Both directions:
//   - Positive: files present at the declared paths, app root supplied -> no AL0327 at all.
//   - Negative: a declaration pointing at a file that genuinely does not exist must still
//     raise AL0327 naming that file — a fix that unconditionally silences AL0327 (instead
//     of resolving real paths) would pass the positive case AND hide real typos, which is
//     explicitly worse than the original bug.
//   - Regression guard: the SAME valid-resource fixture with appRootDir omitted (null)
//     must still raise AL0327 — proving the fix is the file system wiring itself, not
//     some incidental change that would make the positive test pass on its own.

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ControlAddInFileSystemTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public ControlAddInFileSystemTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-controladdin-fs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteControlAddIn(string startupScriptRelPath, string scriptsRelPath)
    {
        File.WriteAllText(Path.Combine(_root, "Widget.al"), $$"""
            controladdin "CAS Widget"
            {
                StartupScript = '{{startupScriptRelPath}}';
                Scripts = '{{scriptsRelPath}}';

                RequestedHeight = 100;
                RequestedWidth = 200;

                procedure Refresh(Payload: Text);
                event OnWidgetReady();
            }
            """);
    }

    [SkippableFact]
    public void Emit_ValidControlAddInResources_ProducesNoAL0327()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Directory.CreateDirectory(Path.Combine(_root, "addin"));
        File.WriteAllText(Path.Combine(_root, "addin", "startup.js"), "function StartupScriptMarker() { return 'cas'; }");
        File.WriteAllText(Path.Combine(_root, "addin", "widget.js"), "function WidgetMarker() { return 'cas'; }");
        WriteControlAddIn("addin/startup.js", "addin/widget.js");

        var output = new BcCompiler().Emit(new[] { _root }, "ControlAddInFsTestModule", _root);

        Assert.DoesNotContain(output.Diagnostics, d => d.Contains("AL0327"));
    }

    [SkippableFact]
    public void Emit_MissingControlAddInResource_StillRaisesAL0327()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Directory.CreateDirectory(Path.Combine(_root, "addin"));
        // Only widget.js exists; startup.js is a genuine typo/missing file.
        File.WriteAllText(Path.Combine(_root, "addin", "widget.js"), "function WidgetMarker() { return 'cas'; }");
        WriteControlAddIn("addin/does-not-exist.js", "addin/widget.js");

        var output = new BcCompiler().Emit(new[] { _root }, "ControlAddInFsTestModule", _root);

        var al0327 = output.Diagnostics.Where(d => d.Contains("AL0327")).ToList();
        Assert.True(al0327.Count > 0,
            $"Expected AL0327 for a genuinely missing resource file, got none. All diagnostics: " +
            $"{string.Join(" | ", output.Diagnostics)}");
        Assert.Contains(al0327, d => d.Contains("does-not-exist.js"));
    }

    [SkippableFact]
    public void Emit_ValidControlAddInResources_WithoutAppRootDir_StillRaisesAL0327()
    {
        // Regression guard: proves the fix is the file-system wiring (anchored at the app
        // root), not something that happens to make AL0327 disappear unconditionally. With
        // NO app root supplied (the pre-fix call shape), the SAME valid-resource fixture
        // that passes above must still fail — otherwise the fix could be a no-op stub that
        // always suppresses AL0327, which would hide real missing-file typos.
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        Directory.CreateDirectory(Path.Combine(_root, "addin"));
        File.WriteAllText(Path.Combine(_root, "addin", "startup.js"), "function StartupScriptMarker() { return 'cas'; }");
        File.WriteAllText(Path.Combine(_root, "addin", "widget.js"), "function WidgetMarker() { return 'cas'; }");
        WriteControlAddIn("addin/startup.js", "addin/widget.js");

        var output = new BcCompiler().Emit(new[] { _root }, "ControlAddInFsTestModule"); // appRootDir omitted

        Assert.Contains(output.Diagnostics, d => d.Contains("AL0327"));
    }
}
