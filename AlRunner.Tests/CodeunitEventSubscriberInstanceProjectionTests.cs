// CodeunitEventSubscriberInstanceProjectionTests — the codeunit metadata projection states
// EventSubscriberInstance: the declared value, else StaticAutomatic (#4605).
//
// BC's emitter writes the attribute on every <CodeUnit> document; the symbol file states it only
// where AL declares it. Both row sources are pinned — a precompiled dependency (SymbolReference)
// and a source-parsed codeunit — because each builds its CodeunitMetaRow separately. The
// ground-truth comparison lives in the metadata-equivalence harness; see
// docs/metadata-equivalence.md#unobservable-omissions.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reaches the RecordPatches AL parse statics, which are process-wide (#1696, #1712).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CodeunitEventSubscriberInstanceProjectionTests : IDisposable
{
    private const int DeclaresManual = 61091;
    private const int DeclaresStaticAutomatic = 61092;
    private const int DeclaresNothing = 61093;
    private const int SourceManual = 61094;
    private const int SourceDeclaresNothing = 61095;

    private readonly string _root;

    public CodeunitEventSubscriberInstanceProjectionTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-event-subscriber-instance");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "5b0f2d7e-7a41-4c1e-9a53-4605a0e5f001",
          "Name": "Event Subscriber Instance Fixture",
          "Codeunits": [
            {
              "Id": {{DeclaresManual}},
              "Name": "Declares Manual",
              "Properties": [ { "Name": "EventSubscriberInstance", "Value": "Manual" } ]
            },
            {
              "Id": {{DeclaresStaticAutomatic}},
              "Name": "Declares Static Automatic",
              "Properties": [ { "Name": "EventSubscriberInstance", "Value": "StaticAutomatic" } ]
            },
            { "Id": {{DeclaresNothing}}, "Name": "Declares Nothing", "Properties": [] }
          ]
        }
        """;

    private string WriteApp()
    {
        var appPath = Path.Combine(_root, "event-subscriber-instance.app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static string? Attribute(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml!);
        var root = doc.DocumentElement!;
        return root.HasAttribute("EventSubscriberInstance") ? root.GetAttribute("EventSubscriberInstance") : null;
    }

    [Fact]
    public void A_dependency_codeunit_states_its_declared_value_else_StaticAutomatic()
    {
        var appPath = WriteApp();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        Assert.Equal("Manual", Attribute(DeclaresManual));
        Assert.Equal("StaticAutomatic", Attribute(DeclaresStaticAutomatic));
        // BC's emitter writes the default rather than omitting it (every codeunit in the
        // 28.5.54151.55132 ground-truth bundles, #4605), so absence would be a disagreement.
        Assert.Equal("StaticAutomatic", Attribute(DeclaresNothing));
    }

    /// <summary>
    /// The warm half (local-test-scope.md): BcAppSymbolCache sits between the parse and the
    /// projection, so the same answers must come back from a served cache entry.
    /// </summary>
    [Fact]
    public void A_dependency_codeunit_states_the_same_value_from_a_warm_symbol_cache()
    {
        var appPath = WriteApp();
        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("Manual", Attribute(DeclaresManual));

        BcAppSymbolCache.ResetProcessCacheForTests();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        Assert.Equal("Manual", Attribute(DeclaresManual));
        Assert.Equal("StaticAutomatic", Attribute(DeclaresNothing));
    }

    [Fact]
    public void A_source_parsed_codeunit_states_its_declared_value_else_StaticAutomatic()
    {
        var src = $$"""
            codeunit {{SourceManual}} "Source Manual"
            {
                EventSubscriberInstance = Manual;
            }

            codeunit {{SourceDeclaresNothing}} "Source Declares Nothing"
            {
            }
            """;
        RecordPatches.ResetForReload();
        typeof(RecordPatches)
            .GetMethod("TryParseObjectDeclFile", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { src, null });

        Assert.Equal("Manual", Attribute(SourceManual));
        Assert.Equal("StaticAutomatic", Attribute(SourceDeclaresNothing));
    }
}
