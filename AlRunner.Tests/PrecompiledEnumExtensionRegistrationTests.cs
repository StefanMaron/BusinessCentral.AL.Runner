// PrecompiledEnumExtensionRegistrationTests — #4197: an enumextension declared by a
// PRECOMPILED dependency .app contributes its values, and its per-value `Implementation`,
// to the enum it extends.
//
// The gap this pins
// -----------------
// AlEnumMetadataRegistry has had RegisterExtension since #1625, and the sidecar has carried
// base/extension identities separately since #2709. Neither reaches a precompiled .app:
// BcAppSymbolCache parsed only the `EnumTypes` container, so `AppSymbols.Enums` never held an
// enumextension, and RecordPatches.AddBcAppPath — the ONLY live path by which a precompiled
// dependency's enums reach the registry — called Register for each of those and
// RegisterExtension for nothing at all. A precompiled app's enumextension values were
// therefore absent from the registry entirely, so `Impl := SomeEnum::ThatValue` found no
// implementation and fell back to NCLOptionMetadata.Default.
//
// Not the same defect as #2709 or #3579, both of which are about the SOURCE-compiled
// dependency's cache sidecar (whether an extension round-trips through it, and whether the
// loader's before/after id diff selects it). Those paths register correctly in memory at
// compile time; this one never registered at all, warm or cold.
//
// Population: Base Application 28.4.53241.54407's own SymbolReference.json declares 44
// enumextension symbols contributing 172 values over 33 distinct target enums, 49 of those
// values declaring an Implementation. Re-derive with the reader below rather than trusting
// the figure — it moves with the BC build.
//
// Fixture: a synthetic .app, the same technique as RecordPatchesBcAppSymbolReadFailureTests
// (#2712) and RecordPatchesWarmReloadExtensionIndexTests (#2478). No Base Application floor
// (.claude/rules/no-base-app-in-csharp-tests.md).

using System.IO.Compression;
using System.Text;
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: this class drives RecordPatches.AddBcAppPath and the
// process-wide AlEnumMetadataRegistry, both of which ParserStaticsIsolationGuardTests
// requires to be in this collection (#1696).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class PrecompiledEnumExtensionRegistrationTests : IDisposable
{
    private readonly string _root;

    // Inside no app.json's idRanges on purpose: these ids exist only in a synthetic
    // SymbolReference.json this test writes, never in compiled AL.
    private const int BaseEnumId = 94810;
    private const int ExtObjectId = 94811;
    private const int BaseImplCodeunit = 94812;
    private const int ExtImplCodeunit = 94813;
    private const string BaseEnumName = "Repro4197 Product";

    public PrecompiledEnumExtensionRegistrationTests()
    {
        _root = TestScratch.Dir("al-runner-4197-tests");
        Directory.CreateDirectory(_root);
        AlEnumMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
    }

    public void Dispose()
    {
        AlEnumMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void WriteApp(string path, string symbolReferenceJson)
    {
        using var zip = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
    }

    /// <summary>
    /// A SymbolReference.json shaped exactly as `al compile` emits one: the base enum under
    /// `EnumTypes`, the enumextension under `EnumExtensionTypes` with a `TargetObject` naming
    /// the base enum through a `#&lt;appid&gt;#` module qualifier — the spelling measured on
    /// Base Application 28.4, where all 44 enumextension symbols carry `TargetObject` and
    /// every value's `Implementation` is a codeunit id written as a string.
    /// </summary>
    private static string SymbolReference(bool includeExtension) => $$"""
    {
      "AppId": "3f2a1b7c-8d49-4e15-a2b6-7c8d9e0f1a2b",
      "Name": "Repro4197Lib",
      "Publisher": "Repro",
      "EnumTypes": [
        {
          "Id": {{BaseEnumId}},
          "Name": "{{BaseEnumName}}",
          "Properties": [ { "Name": "Extensible", "Value": "1" } ],
          "Values": [
            {
              "Ordinal": 0,
              "Name": "Basic",
              "Properties": [ { "Name": "Implementation", "Value": "{{BaseImplCodeunit}}" } ]
            }
          ]
        }
      ]{{(includeExtension ? $$"""
      ,
      "EnumExtensionTypes": [
        {
          "Id": {{ExtObjectId}},
          "Name": "Repro4197 Product Ext",
          "TargetObject": "#3f2a1b7c8d494e15a2b67c8d9e0f1a2b#{{BaseEnumName}}",
          "Values": [
            {
              "Ordinal": 10,
              "Name": "Extended",
              "Properties": [
                { "Name": "Implementation", "Value": "{{ExtImplCodeunit}}" },
                { "Name": "Caption", "Value": "Extended caption" }
              ]
            }
          ]
        }
      ]
      """ : "")}}
    }
    """;

    /// <summary>
    /// The whole claim of #4197, at the registry: after a precompiled .app declaring an
    /// enumextension is registered, the extended enum answers BOTH values, and the extension's
    /// value carries the extension's OWN implementation codeunit id — not the base's, and not
    /// an empty list, which is what the cast to the interface reads and what fell back to
    /// NCLOptionMetadata.Default before the fix.
    /// </summary>
    [Fact]
    public void PrecompiledEnumExtension_ContributesItsValueAndImplementation()
    {
        var appPath = Path.Combine(_root, "with-ext.app");
        WriteApp(appPath, SymbolReference(includeExtension: true));

        RecordPatches.AddBcAppPath(appPath);

        Assert.True(AlEnumMetadataRegistry.TryGet(BaseEnumId, out var entry),
            $"enum {BaseEnumId} was not registered at all from the precompiled .app");

        // Both values, base first then the extension's — TryGet's documented merge order.
        Assert.Equal(new[] { "Basic", "Extended" }, entry.Options);
        Assert.Equal(new[] { 0, 10 }, entry.Indexes);

        // The value the extension added resolves to the EXTENSION's implementation codeunit.
        // This is the assertion the gap failed: without it the ordinal existed (or did not)
        // and the interface cast found nothing.
        var extendedIndex = Array.IndexOf(entry.Indexes, 10);
        Assert.Equal(new[] { ExtImplCodeunit }, entry.Implementations[extendedIndex]);

        // ...and the base value still resolves to the base's own, so the extension merged
        // rather than clobbered (the #2709 failure mode, in the other direction).
        var basicIndex = Array.IndexOf(entry.Indexes, 0);
        Assert.Equal(new[] { BaseImplCodeunit }, entry.Implementations[basicIndex]);

        // The extension's per-value Caption survives too — the same field #1775 fixed for a
        // precompiled base enum, which the extension path would otherwise drop.
        Assert.NotNull(entry.Captions);
        Assert.Equal("Extended caption", entry.Captions![extendedIndex]);

        // Extensible is the BASE enum's property; an extension declares none, so the merged
        // entry must still state the base's (#3807).
        Assert.True(entry.Extensible);
    }

    /// <summary>
    /// The negative direction: the SAME app with no <c>EnumExtensionTypes</c> container
    /// registers the base enum and exactly one value. Without this, a fix that invented an
    /// extension value from nowhere would pass the positive test.
    /// </summary>
    [Fact]
    public void PrecompiledAppWithoutEnumExtension_RegistersOnlyTheBaseValue()
    {
        var appPath = Path.Combine(_root, "no-ext.app");
        WriteApp(appPath, SymbolReference(includeExtension: false));

        RecordPatches.AddBcAppPath(appPath);

        Assert.True(AlEnumMetadataRegistry.TryGet(BaseEnumId, out var entry));
        Assert.Equal(new[] { "Basic" }, entry.Options);
        Assert.Equal(new[] { 0 }, entry.Indexes);
        Assert.Equal(new[] { BaseImplCodeunit }, entry.Implementations[0]);
    }
}
