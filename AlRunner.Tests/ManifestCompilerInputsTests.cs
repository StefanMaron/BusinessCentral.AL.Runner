// ManifestCompilerInputsTests — app.json properties that feed BC's
// ParseOptions/CompilationOptions on the TOP-LEVEL (BcCompiler.Emit) and source-dependency
// (BcCompiler.EmitDepSymbols) compile paths.
//
// Root cause being guarded
// -------------------------
// Three app.json properties never reached the compiler on at least one of the two compile
// paths:
//   - #1940 contextSensitiveHelpUrl — only EmitDepSymbols read it (#1898); Emit() left the
//     CompilationOptions field at its "" default, so a page/report using
//     ContextSensitiveHelpPage raised a false AL0543 even when the app's own manifest set
//     the URL.
//   - #1941 features — NEITHER path mapped it onto NavCA.CompilerFeatures at all, so
//     "features": ["NoImplicitWith"] was silently ignored and implicit `with` stayed on,
//     producing false AL0129/AL0135 for AL that is valid under the declared feature set.
//   - #1943 preprocessorSymbols — NEITHER path read it, so a manifest-declared symbol was
//     undefined at parse time and the WRONG #if branch compiled — silently, when both
//     branches are otherwise valid AL.
//
// Fix: BcCompiler.ReadManifestCompilerInputs reads all three in one pass from the owning
// app's own app.json, and both Emit() and EmitDepSymbols() now build their
// ParseOptions/CompilationOptions from it, instead of each path hand-rolling (and
// incompletely rolling) its own.
//
// Test strategy
// -------------
// Exercise BcCompiler.Emit/EmitDepSymbols directly (mirrors ControlAddInFileSystemTests /
// BcCompilerEmitRetryTests) and assert on the returned BcEmitOutput.Diagnostics/Sources —
// no subprocess spawn needed for the compile-time behaviour itself. Every property gets
// both directions: manifest sets it -> compiles/no diagnostic; manifest omits it -> the
// diagnostic BC would genuinely raise is STILL raised (never unconditionally silenced).

using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ManifestCompilerInputsTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public ManifestCompilerInputsTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-manifest-compiler-inputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        // Leave no CLI-define residue for tests that run after this one in the same process.
        BcCompiler.SetExtraPreprocessorSymbols(Array.Empty<string>());
    }

    private void SkipIfEngineNotReady() =>
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static void WriteAppJson(string dir, string extraProps)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Manifest Compiler Inputs Test App",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "28.0.0.0",
          "idRanges": [ { "from": 61050, "to": 61079 } ],
          "runtime": "17.0"{{extraProps}}
        }
        """);
    }

    // ── #1940: contextSensitiveHelpUrl on the top-level Emit() path ──────────────────

    private void WriteHelpUrlPage()
    {
        File.WriteAllText(Path.Combine(_root, "HelpUrl.Page.al"), """
            page 61050 "MCI Help Url Page"
            {
                PageType = Card;
                ContextSensitiveHelpPage = 'docs/repro/';

                layout
                {
                    area(Content)
                    {
                        group(General) { }
                    }
                }
            }
            """);
    }

    [SkippableFact]
    public void Emit_ManifestSetsContextSensitiveHelpUrl_NoAL0543_PageEmitted()
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, ",\n  \"contextSensitiveHelpUrl\": \"https://example.com/docs/\"");
        WriteHelpUrlPage();

        var output = new BcCompiler().Emit(new[] { _root }, "MciHelpUrlSetModule", _root);

        Assert.DoesNotContain(output.Diagnostics, d => d.Contains("AL0543"));
        Assert.Contains(output.Sources, s => s.Name == "MCI Help Url Page");
    }

    [SkippableFact]
    public void Emit_ManifestOmitsContextSensitiveHelpUrl_StillRaisesAL0543()
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, ""); // genuinely no contextSensitiveHelpUrl — real manifest error
        WriteHelpUrlPage();

        var output = new BcCompiler().Emit(new[] { _root }, "MciHelpUrlOmitModule", _root);

        Assert.Contains(output.Diagnostics, d => d.Contains("AL0543"));
    }

    // ── #1941: features -> CompilerFeatures on the top-level Emit() path ─────────────

    // NOTE: NoImplicitWith's actual effect (whether the implicit-with binder shadows a
    // page's own local variable/procedure names with the SourceTable record's members) is
    // NOT provable via a bare BcCompiler.Emit() call with no package cache wired — measured:
    // the exact AL0129/AL0135 repro from #1941 produces ZERO diagnostics either way (feature
    // declared or not) when no --package-cache is supplied, because that binder pathway
    // needs the full symbol-resolution context a real run provides. A test that "passes"
    // regardless of the fix is noise per .claude/rules/tdd.md — see
    // ManifestFeaturesSubprocessTests (spawns the real runner with --package-cache) for the
    // proving RED/GREEN pair, both top-level and source-dependency paths.

    [SkippableFact]
    public void Emit_ManifestDeclaresNonCompilerFeatureString_StillCompiles()
    {
        // "TranslationFile" is a legal app.json `features` entry that has no
        // NavCA.CompilerFeatures counterpart (it drives packaging, not parsing/binding).
        // Must be silently ignored, not fatal.
        SkipIfEngineNotReady();
        WriteAppJson(_root, ",\n  \"features\": [ \"TranslationFile\" ]");
        File.WriteAllText(Path.Combine(_root, "Plain.Codeunit.al"), """
            codeunit 61053 "MCI Plain Codeunit"
            {
                procedure Answer(): Integer
                begin
                    exit(42);
                end;
            }
            """);

        var output = new BcCompiler().Emit(new[] { _root }, "MciUnknownFeatureModule", _root);

        Assert.Contains(output.Sources, s => s.Name == "MCI Plain Codeunit");
        Assert.Empty(output.ExcludedObjects);
    }

    // #1941's dependency-path coverage (EmitDepSymbols honouring a dep's own
    // NoImplicitWith) is in ManifestFeaturesSubprocessTests for the same reason as the
    // top-level case above — see the NOTE before Emit_ManifestDeclaresNonCompilerFeatureString_StillCompiles.

    // ── #1943: preprocessorSymbols on the top-level Emit() path ──────────────────────

    private void WritePreprocessorFixture()
    {
        // Only ONE branch is ever parsed — whichever the symbol selects — so the emitted
        // C# for this codeunit contains exactly one of the two markers below. Distinctive
        // string-literal markers (not plain integers) rule out an incidental digit-sequence
        // collision elsewhere in BC's generated C# boilerplate. This is a stronger check
        // than "it compiled": an implementation that defines the symbol unconditionally
        // would make BOTH directions parse the SAME (true) branch, and the negative test
        // below would catch that (it asserts the ELSE-branch marker).
        File.WriteAllText(Path.Combine(_root, "Sym.Codeunit.al"), """
            codeunit 61054 "MCI Sym Repro"
            {
                procedure Answer(): Text
                begin
            #if MCI_MANIFEST_SYM
                    exit('MCI_IF_BRANCH_MARKER');
            #else
                    exit('MCI_ELSE_BRANCH_MARKER');
            #endif
                end;
            }
            """);
    }

    [SkippableFact]
    public void Emit_ManifestDeclaresPreprocessorSymbol_CompilesIfBranch()
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, ",\n  \"preprocessorSymbols\": [ \"MCI_MANIFEST_SYM\" ]");
        WritePreprocessorFixture();

        var output = new BcCompiler().Emit(new[] { _root }, "MciSymOnModule", _root);

        Assert.Empty(output.Diagnostics);
        var src = Assert.Single(output.Sources, s => s.Name == "MCI Sym Repro");
        Assert.Contains("MCI_IF_BRANCH_MARKER", src.Code);
        Assert.DoesNotContain("MCI_ELSE_BRANCH_MARKER", src.Code);
    }

    [SkippableFact]
    public void Emit_ManifestOmitsPreprocessorSymbol_CompilesElseBranch()
    {
        SkipIfEngineNotReady();
        WriteAppJson(_root, ""); // no preprocessorSymbols -> MCI_MANIFEST_SYM undefined
        WritePreprocessorFixture();

        var output = new BcCompiler().Emit(new[] { _root }, "MciSymOffModule", _root);

        Assert.Empty(output.Diagnostics);
        var src = Assert.Single(output.Sources, s => s.Name == "MCI Sym Repro");
        Assert.Contains("MCI_ELSE_BRANCH_MARKER", src.Code);
        Assert.DoesNotContain("MCI_IF_BRANCH_MARKER", src.Code);
    }

    [SkippableFact]
    public void Emit_ManifestSymbolAndCliDefine_Compose_BothHonoured()
    {
        // --define/--preprocessor-symbols (CLI) and app.json's preprocessorSymbols
        // (manifest) must UNION, never override one another. Two independent #if blocks,
        // one gated on each source, both must compile their "true" branch simultaneously.
        SkipIfEngineNotReady();
        WriteAppJson(_root, ",\n  \"preprocessorSymbols\": [ \"MCI_MANIFEST_SYM\" ]");
        File.WriteAllText(Path.Combine(_root, "Compose.Codeunit.al"), """
            codeunit 61055 "MCI Compose Repro"
            {
                procedure FromManifest(): Text
                begin
            #if MCI_MANIFEST_SYM
                    exit('MCI_MANIFEST_TRUE_MARKER');
            #else
                    exit('MCI_MANIFEST_FALSE_MARKER');
            #endif
                end;

                procedure FromCli(): Text
                begin
            #if MCI_CLI_SYM
                    exit('MCI_CLI_TRUE_MARKER');
            #else
                    exit('MCI_CLI_FALSE_MARKER');
            #endif
                end;
            }
            """);
        BcCompiler.SetExtraPreprocessorSymbols(new[] { "MCI_CLI_SYM" });
        try
        {
            var output = new BcCompiler().Emit(new[] { _root }, "MciComposeModule", _root);

            Assert.Empty(output.Diagnostics);
            var src = Assert.Single(output.Sources, s => s.Name == "MCI Compose Repro");
            Assert.Contains("MCI_MANIFEST_TRUE_MARKER", src.Code);
            Assert.Contains("MCI_CLI_TRUE_MARKER", src.Code);
            Assert.DoesNotContain("MCI_MANIFEST_FALSE_MARKER", src.Code);
            Assert.DoesNotContain("MCI_CLI_FALSE_MARKER", src.Code);
        }
        finally
        {
            BcCompiler.SetExtraPreprocessorSymbols(Array.Empty<string>());
        }
    }
}
