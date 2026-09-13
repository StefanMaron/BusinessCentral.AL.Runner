// #4071: RecordPatches' source parse (object declarations, captions, pages, tables...) must pick
// the #if branch the compile picked, so it parses each registered folder under the app.json the
// COMPILE of that folder reads — never one found by walking up (#2542). The apps below carry
// byte-identical .al text and differ only in their manifest, so the parse-tree memo and the keyed
// tree cache must not serve one app's tree to the other.
//
// Own file, engine collection: AddSourceDirs is reachable without reflection and the observable
// is read through an internal accessor (see RecordPatchesAddSourceDirsParseOnceTests).
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class RecordPatchesManifestPreprocessorSymbolsTests : IDisposable
{
    private const int CodeunitId = 94072;
    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public RecordPatchesManifestPreprocessorSymbolsTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-recordpatches-manifest-symbols");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string WriteManifest(string dir, string? preprocessorSymbolsJson)
    {
        Directory.CreateDirectory(dir);
        var symbols = preprocessorSymbolsJson is null ? "" : $", \"preprocessorSymbols\": {preprocessorSymbolsJson}";
        var path = Path.Combine(dir, "app.json");
        File.WriteAllText(path,
            $$"""{ "id": "00000000-0000-0000-0000-000000004072", "name": "T", "publisher": "P", "version": "1.0.0.0"{{symbols}} }""");
        return path;
    }

    private static string WriteCodeunit(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Policy.Codeunit.al"), $$"""
            codeunit {{CodeunitId}} "Manifest Symbol Policy"
            {
                Subtype = Test;
            #if PPX4072
                TestHttpRequestPolicy = AllowOutboundFromHandler;
            #else
                TestHttpRequestPolicy = BlockOutboundRequests;
            #endif
            }
            """);
        return dir;
    }

    private void SkipIfNoEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    [SkippableFact]
    public void AddSourceDirs_ParsesEachFolderUnderItsOwnManifest()
    {
        SkipIfNoEngine();

        // Defined first, so a cache keyed without the manifest term would replay its tree for
        // the second, identical text.
        var defined = Path.Combine(_root, "defined");
        WriteManifest(defined, "[\"PPX4072\"]");
        RecordPatches.AddSourceDirs(new[] { WriteCodeunit(defined) });
        Assert.Equal("AllowOutboundFromHandler", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));

        var undefined = Path.Combine(_root, "undefined");
        WriteManifest(undefined, preprocessorSymbolsJson: null);
        RecordPatches.AddSourceDirs(new[] { WriteCodeunit(undefined) });
        Assert.Equal("BlockOutboundRequests", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));

        var definedAgain = Path.Combine(_root, "defined-again");
        WriteManifest(definedAgain, "[\"PPX4072\"]");
        RecordPatches.AddSourceDirs(new[] { WriteCodeunit(definedAgain) });
        Assert.Equal("AllowOutboundFromHandler", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));
    }

    [SkippableFact]
    public void AddSourceDirs_FolderWithoutManifest_IgnoresAParentFoldersManifest()
    {
        SkipIfNoEngine();

        // The parent defines the symbol; the compile of the child folder reads no app.json.
        var parent = Path.Combine(_root, "parent");
        WriteManifest(parent, "[\"PPX4072\"]");
        RecordPatches.AddSourceDirs(new[] { WriteCodeunit(Path.Combine(parent, "no-manifest-folder")) });

        Assert.Equal("BlockOutboundRequests", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));
    }

    [SkippableFact]
    public void AddSourceDirs_WithCompileManifest_UsesItForAFolderThatHoldsNone()
    {
        SkipIfNoEngine();

        // src/ holds no app.json; the compile reads the app root's through appRootDir, and the
        // registration says so. Registered after an undefined parse of the same text.
        var undefined = Path.Combine(_root, "undefined-first");
        WriteManifest(undefined, preprocessorSymbolsJson: null);
        RecordPatches.AddSourceDirs(new[] { WriteCodeunit(undefined) });
        Assert.Equal("BlockOutboundRequests", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));

        var app = Path.Combine(_root, "app-with-src");
        var manifest = WriteManifest(app, "[\"PPX4072\"]");
        RecordPatches.AddSourceDirs(new[] { (WriteCodeunit(Path.Combine(app, "src")), (string?)manifest) });

        Assert.Equal("AllowOutboundFromHandler", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));
    }
}
