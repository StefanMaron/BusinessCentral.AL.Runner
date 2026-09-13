// #4071: RecordPatches' source parse (object declarations, captions, pages, tables...) must pick
// the #if branch the compile picked, which includes the owning app's app.json
// `preprocessorSymbols`. The two apps below carry byte-identical .al text and differ only in
// their manifest, so the parse-tree memo and the keyed tree cache must not serve one app's tree
// to the other.
//
// Own file, engine collection: AddSourceDirs is public and the observable is read through an
// internal accessor, so no reflection-by-name marker is needed (see
// RecordPatchesAddSourceDirsParseOnceTests for the same shape).
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

    private string WriteApp(string name, string? preprocessorSymbolsJson)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var symbols = preprocessorSymbolsJson is null ? "" : $", \"preprocessorSymbols\": {preprocessorSymbolsJson}";
        File.WriteAllText(Path.Combine(dir, "app.json"),
            $$"""{ "id": "00000000-0000-0000-0000-000000004072", "name": "{{name}}", "publisher": "P", "version": "1.0.0.0"{{symbols}} }""");
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

    [SkippableFact]
    public void AddSourceDirs_ParsesEachFileUnderItsOwnManifestSymbols()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Defined first, so a cache keyed without the manifest term would replay its tree for
        // the second, identical text.
        RecordPatches.AddSourceDirs(new[] { WriteApp("defined", "[\"PPX4072\"]") });
        Assert.Equal("AllowOutboundFromHandler", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));

        RecordPatches.AddSourceDirs(new[] { WriteApp("undefined", preprocessorSymbolsJson: null) });
        Assert.Equal("BlockOutboundRequests", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));

        // And back: same text, manifest defines it again, under a third directory.
        RecordPatches.AddSourceDirs(new[] { WriteApp("defined-again", "[\"PPX4072\"]") });
        Assert.Equal("AllowOutboundFromHandler", RecordPatches.TryGetParsedTestHttpRequestPolicy(CodeunitId));
    }
}
