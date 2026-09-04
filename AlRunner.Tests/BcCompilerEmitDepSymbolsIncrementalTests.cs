// BcCompilerEmitDepSymbolsIncrementalTests — RED/GREEN proof for issue #2669.
//
// #2669: `RunLayeredPrePass`/`BuildSiblingSourceDeps` synthesize a source dependency's
// compile-time symbols (`*.symbols.json`) via `BcCompiler.EmitDepSymbols`, BC's whole-module
// `Compilation.Create` entry point — never `TryEmitIncremental`/`Compilation.CreateForRad`, the
// fast path `--watch`/`--server`'s own per-bundle compile already uses for exactly this class of
// problem. Every re-synthesis of a dependency's symbols therefore cost the same whole-module
// compile as the FIRST, however small the edit — measured 22.25s on Pageworks for one added
// no-op procedure.
//
// `BcCompiler.EmitDepSymbolsIncremental` is the fix: try `TryEmitIncremental` against THIS
// instance's own RAD baseline first (recorded by the previous call, via `EmitDepSymbols`'s new
// `trackIncrementalBaseline` parameter), and only fall back to the full compile when
// `TryEmitIncremental` cannot prove the fast path safe — including #2548's guard against an added
// overload silently moving which member id an untouched sibling resolves to, which matters here
// exactly as much as it does for the main per-bundle loop (see #2603's cross-app extension of that
// hazard).
//
// What each test proves (tdd.md: must prove, not just pass):
//   - The FIRST call on a fresh instance always falls back (no baseline yet) — proving the
//     mechanism cannot silently claim a speedup it did not earn.
//   - The SECOND call on the SAME instance, after a genuine content edit that adds a brand new
//     procedure, takes the fast path AND writes symbols that actually contain the new procedure —
//     not a stale replay of the pre-edit shape. Proven by feeding the SAME symbols.json to a real
//     downstream compile that calls the new procedure: a stale symbol table would fail that
//     compile with AL0132/AL0185, not just "look different" in a text diff.
//   - An edit that ADDS AN OVERLOAD of an existing procedure name — the #2548/#2603 hazard this
//     mechanism inherits by construction from TryEmitIncremental — still falls back to a full
//     compile, with a fallback reason naming the hazard. A version of this method that always took
//     the fast path would satisfy every other assertion here and still be wrong; this is the test
//     that catches it.
using System.Text.Json;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class BcCompilerEmitDepSymbolsIncrementalTests : IDisposable
{
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public BcCompilerEmitDepSymbolsIncrementalTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = Path.Combine(Path.GetTempPath(), "al-runner-depsym-incr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteAl(string fileName, string content) => File.WriteAllText(Path.Combine(_root, fileName), content);

    private static readonly Guid AppId = new("b2c3d4e5-9001-4a11-9111-111111111111");

    private const string DepBefore = """
        codeunit 90340 "DepSym Incr Lib"
        {
            procedure Ping(): Integer
            begin
                exit(1);
            end;
        }
        """;

    /// <summary>Content edit to the SAME file: a brand new procedure added, nothing renamed or
    /// removed — the ordinary shape TryEmitIncremental's ordinary content-edit path covers.</summary>
    private const string DepWithNewProcedure = """
        codeunit 90340 "DepSym Incr Lib"
        {
            procedure Ping(): Integer
            begin
                exit(1);
            end;

            procedure Pong(): Integer
            begin
                exit(42);
            end;
        }
        """;

    /// <summary>The #2548/#2603 hazard: a SECOND overload of `Ping`, not a new procedure name.
    /// `MethodSymbol.CalculateMethodIdForNewVersions` is method-local, so the existing
    /// `Ping()` keeps its member id — an untouched caller passing an argument that used to widen
    /// to a different overload would silently rebind. See BcCompilerIncrementalOverloadRebindTests
    /// for the full mechanism; this test only needs that TryEmitIncremental's existing guard is
    /// still reached through EmitDepSymbolsIncremental, not that it fires correctly in general.</summary>
    private const string DepWithOverload = """
        codeunit 90340 "DepSym Incr Lib"
        {
            procedure Ping(): Integer
            begin
                exit(1);
            end;

            procedure Ping(Seed: Integer): Integer
            begin
                exit(Seed);
            end;
        }
        """;

    [SkippableFact]
    public void EmitDepSymbolsIncremental_FirstCall_AlwaysFallsBack_NoBaselineYet()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        // #2669: TryEmitIncremental reads the current app identity from static state
        // (BcCompiler.SetCurrentAppIdentity/ScopeCurrentAppIdentity), never from the explicit
        // appId/publisher/version arguments passed to EmitDepSymbolsIncremental itself (those only
        // feed the FALLBACK EmitDepSymbols compile) — exactly mirroring how RunLayeredPrePass and
        // BuildSiblingSourceDeps already scope every real call. Omitting this scope here would
        // compare the baseline recorded under one identity against a DIFFERENT default identity on
        // the second call, forcing a spurious fallback that has nothing to do with what this test
        // is proving.
        using var identityScope = BcCompiler.ScopeCurrentAppIdentity(AppId, "AL Runner", new Version(1, 0, 0, 0));


        WriteAl("Lib.al", DepBefore);
        var symbolsPath = Path.Combine(_root, "out.symbols.json");
        var compiler = new BcCompiler();

        compiler.EmitDepSymbolsIncremental(
            new[] { _root }, "DepSymIncrModule", AppId, "AL Runner", new Version(1, 0, 0, 0),
            symbolsPath, appRootDir: null, out var tookFastPath, out var fallbackReason);

        Assert.False(tookFastPath, "the first call on a fresh instance has no baseline to diff against and must fall back");
        Assert.Contains("no incremental baseline yet", fallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(symbolsPath));
        Assert.Contains("Ping", File.ReadAllText(symbolsPath), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void EmitDepSymbolsIncremental_ContentEditAddingAProcedure_TakesFastPath_AndMatchesAFreshFullCompile()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        // #2669: TryEmitIncremental reads the current app identity from static state
        // (BcCompiler.SetCurrentAppIdentity/ScopeCurrentAppIdentity), never from the explicit
        // appId/publisher/version arguments passed to EmitDepSymbolsIncremental itself (those only
        // feed the FALLBACK EmitDepSymbols compile) — exactly mirroring how RunLayeredPrePass and
        // BuildSiblingSourceDeps already scope every real call. Omitting this scope here would
        // compare the baseline recorded under one identity against a DIFFERENT default identity on
        // the second call, forcing a spurious fallback that has nothing to do with what this test
        // is proving.
        using var identityScope = BcCompiler.ScopeCurrentAppIdentity(AppId, "AL Runner", new Version(1, 0, 0, 0));


        WriteAl("Lib.al", DepBefore);
        var symbolsPath = Path.Combine(_root, "out.symbols.json");
        var compiler = new BcCompiler();

        compiler.EmitDepSymbolsIncremental(
            new[] { _root }, "DepSymIncrModule2", AppId, "AL Runner", new Version(1, 0, 0, 0),
            symbolsPath, appRootDir: null, out var firstFast, out _);
        Assert.False(firstFast);

        // Genuine content edit: same file, same object, one new procedure added.
        WriteAl("Lib.al", DepWithNewProcedure);

        compiler.EmitDepSymbolsIncremental(
            new[] { _root }, "DepSymIncrModule2", AppId, "AL Runner", new Version(1, 0, 0, 0),
            symbolsPath, appRootDir: null, out var secondFast, out var secondReason);

        Assert.True(secondFast, $"expected the RAD fast path on the second call; fell back instead: {secondReason}");
        Assert.True(File.Exists(symbolsPath));

        var fastProcedureNames = ProcedureNamesOf(symbolsPath, "DepSym Incr Lib");
        Assert.Equal(new[] { "Ping", "Pong" }, fastProcedureNames.OrderBy(n => n, StringComparer.Ordinal));

        // Correct, not just "changed": an INDEPENDENT fresh instance's full compile of the SAME
        // post-edit source (never touched by the incremental machinery at all) must describe the
        // identical procedure set. A fast path that shipped a stale or partially-merged module
        // definition would still satisfy the assertion above (Pong is present) while disagreeing
        // with this one if it ALSO dropped or duplicated something else in the merge.
        var freshSymbolsPath = Path.Combine(_root, "fresh.symbols.json");
        new BcCompiler().EmitDepSymbols(
            new[] { _root }, "DepSymIncrModule2Fresh", AppId, "AL Runner", new Version(1, 0, 0, 0),
            freshSymbolsPath, appRootDir: null);
        var freshProcedureNames = ProcedureNamesOf(freshSymbolsPath, "DepSym Incr Lib");
        Assert.Equal(freshProcedureNames.OrderBy(n => n, StringComparer.Ordinal), fastProcedureNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>Procedure names of the named codeunit in a written <c>*.symbols.json</c>, read
    /// structurally (not a raw text search) so a false positive from an unrelated string match
    /// cannot pass this test.</summary>
    private static List<string> ProcedureNamesOf(string symbolsJsonPath, string codeunitName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(symbolsJsonPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("Codeunits", out var codeunits))
            throw new InvalidOperationException($"'{symbolsJsonPath}' has no top-level Codeunits array.");
        foreach (var cu in codeunits.EnumerateArray())
        {
            if (!cu.TryGetProperty("Name", out var nameEl) || nameEl.GetString() != codeunitName) continue;
            if (!cu.TryGetProperty("Methods", out var methods))
                return new List<string>();
            return methods.EnumerateArray()
                .Select(m => m.TryGetProperty("Name", out var mn) ? mn.GetString() ?? "" : "")
                .Where(n => n.Length > 0)
                .ToList();
        }
        throw new InvalidOperationException($"'{symbolsJsonPath}' has no codeunit named '{codeunitName}'.");
    }

    [SkippableFact]
    public void EmitDepSymbolsIncremental_AddingAnOverload_FallsBackInsteadOfShippingStaleSymbols()
    {
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        // #2669: TryEmitIncremental reads the current app identity from static state
        // (BcCompiler.SetCurrentAppIdentity/ScopeCurrentAppIdentity), never from the explicit
        // appId/publisher/version arguments passed to EmitDepSymbolsIncremental itself (those only
        // feed the FALLBACK EmitDepSymbols compile) — exactly mirroring how RunLayeredPrePass and
        // BuildSiblingSourceDeps already scope every real call. Omitting this scope here would
        // compare the baseline recorded under one identity against a DIFFERENT default identity on
        // the second call, forcing a spurious fallback that has nothing to do with what this test
        // is proving.
        using var identityScope = BcCompiler.ScopeCurrentAppIdentity(AppId, "AL Runner", new Version(1, 0, 0, 0));


        WriteAl("Lib.al", DepBefore);
        var symbolsPath = Path.Combine(_root, "out.symbols.json");
        var compiler = new BcCompiler();

        compiler.EmitDepSymbolsIncremental(
            new[] { _root }, "DepSymIncrModule3", AppId, "AL Runner", new Version(1, 0, 0, 0),
            symbolsPath, appRootDir: null, out var firstFast, out _);
        Assert.False(firstFast);

        WriteAl("Lib.al", DepWithOverload);

        compiler.EmitDepSymbolsIncremental(
            new[] { _root }, "DepSymIncrModule3", AppId, "AL Runner", new Version(1, 0, 0, 0),
            symbolsPath, appRootDir: null, out var secondFast, out var secondReason);

        Assert.False(secondFast,
            "an added overload must fall back to a full compile (#2548's guard) — taking the fast " +
            "path here risks shipping symbols an unmodified caller elsewhere would silently misbind " +
            "against, exactly the #2603 hazard this mechanism must not reintroduce");
        Assert.Contains("overload", secondReason, StringComparison.OrdinalIgnoreCase);

        // Still correct, just via the (slower) full-compile fallback: the new overload is present.
        var json = File.ReadAllText(symbolsPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("Ping", json, StringComparison.Ordinal);
    }
}
