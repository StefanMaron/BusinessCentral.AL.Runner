// EnumRegistrationSurvivesReloadTests — issue #3052 (#2888 item 4).
//
// The property this pins is TRUE on main today and asserted by nothing, which is exactly why
// #3052 was filed rather than left to close silently with its parent. It is also load-bearing
// by ACCIDENT, and that is the whole point:
//
//   BcRuntime.ResetForNewBundleReload() calls AlEnumMetadataRegistry.Clear() and then
//   RecordPatches.ResetForReload(). AddBcAppPath is the only live path by which a precompiled
//   dependency's enums reach that registry (AlEnumMetadataRegistry.RegisterFromAppPath has no
//   callers), and its first act is:
//
//       if (_bcAppPaths.Contains(appPath, StringComparer.OrdinalIgnoreCase)) return;
//
//   So the enums come back on cycle 2 ONLY because ClearPerBundleBcAppPaths makes the next
//   AddBcAppPath real again. Nothing states that dependency. An "obvious" optimisation that
//   keeps already-registered paths across a reload — cheaper, and every symbol read it skips is
//   genuinely redundant — silently drops the per-value Captions (#1775) and the enum-level
//   DefaultImplementation / UnknownValueImplementation fallbacks (#2306), with the same exit
//   code the run had before. That is #2478's shape: a reset that stops resetting enough, and
//   nothing counts differently afterwards.
//
// ── WHAT THIS ASSERTS, AND WHY NOT "THE REGISTRY IS NON-EMPTY" ───────────────────────────────
//
// #3052 names the bar: "a concrete enum caption and a DefaultImplementation fallback are still
// answered afterwards — not that the registry is non-empty, which would pass against a registry
// holding the wrong thing". The AL-visible consequence of losing the caption is a caption that
// comes back as its member identifier instead of its declared text, so a test asserting only
// presence sees a fully-populated entry and passes. Both assertions below therefore name a
// specific string and a specific codeunit id that only this fixture's symbol file can produce.
//
// The fixture is a REGISTRABLE .app, not a source bundle: source-declared enums land in the
// registry through BcCompiler's emit, not through AddBcAppPath, so a source-only fixture cannot
// express this property at all — the same trap SymbolAppFixture's own header records for
// _bcAppPaths (#2755). Its enum-carrying variant lives here rather than in SymbolAppFixture
// because no other test needs one.
using System.Text;
using System.Text.Json;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class EnumRegistrationSurvivesReloadTests : IDisposable
{
    // Inside no app.json idRange that ships in this repo, and distinct from every id the
    // neighbouring reset tests register, because all of this state is process-global.
    private const int EnumId = 79971;
    private const string DeclaredCaption = "Posted, and then some";
    private const int DefaultImplCodeunitId = 79972;
    private const int UnknownImplCodeunitId = 79973;

    private readonly string _root;

    public EnumRegistrationSurvivesReloadTests()
        => _root = TestScratch.Dir("al-runner-enum-reload");

    public void Dispose()
    {
        try { AlEnumMetadataRegistry.Clear(); } catch { }
        try { RecordPatches.ResetForReload(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// A registrable .app whose SymbolReference declares one enum carrying all three of the
    /// fields that go missing when AddBcAppPath's early-return wins: a per-value Caption
    /// (#1775) and the two enum-level implementation fallbacks (#2306).
    /// </summary>
    private string WriteEnumSymbolApp(string appName)
    {
        var appId = Guid.NewGuid();
        var bundleDir = Path.Combine(_root, appName);
        Directory.CreateDirectory(bundleDir);
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "{{appName}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{EnumId}}, "to": {{EnumId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        // A .app must carry at least one source file to be emitted; the enum's runtime identity
        // for this test comes entirely from the symbol file below, which is the surface
        // AddBcAppPath reads.
        File.WriteAllText(Path.Combine(bundleDir, "Enm.al"), $$"""
        enum {{EnumId}} "ErsState"
        {
            Extensible = true;
            value(0; Open) { Caption = 'Open'; }
            value(1; Posted) { Caption = '{{DeclaredCaption}}'; }
        }
        """);

        var symbolJson = JsonSerializer.Serialize(new
        {
            AppId = appId.ToString(),
            Name = appName,
            Publisher = "AL Runner",
            Version = "1.0.0.0",
            Tables = Array.Empty<object>(),
            Codeunits = Array.Empty<object>(),
            Pages = Array.Empty<object>(),
            Queries = Array.Empty<object>(),
            EnumTypes = new object[]
            {
                new
                {
                    Id = EnumId,
                    Name = "ErsState",
                    // Enum-level fallbacks, read by SymbolCodeunitIdList off the enum's own
                    // Properties bag — NOT parallel to Values.
                    Properties = new object[]
                    {
                        new { Name = "DefaultImplementation", Value = DefaultImplCodeunitId.ToString() },
                        new { Name = "UnknownValueImplementation", Value = UnknownImplCodeunitId.ToString() },
                    },
                    Values = new object[]
                    {
                        new { Name = "Open", Ordinal = 0, Properties = new object[]
                            { new { Name = "Caption", Value = "Open" } } },
                        new { Name = "Posted", Ordinal = 1, Properties = new object[]
                            { new { Name = "Caption", Value = DeclaredCaption } } },
                    },
                },
            },
        });

        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(bundleDir, "app.json"))
            ?? throw new InvalidOperationException("could not read the app.json this test just wrote");
        var appPath = Path.Combine(bundleDir, appName + ".app");
        InProcessAppPackager.EmitAppPackageToFile(
            bundleDir, identity, appPath, Encoding.UTF8.GetBytes(symbolJson));
        return appPath;
    }

    /// <summary>The three fields that a lost re-registration silently blanks, read together so
    /// a failure names which one went.</summary>
    private static (string? PostedCaption, int[]? DefaultImpl, int[]? UnknownImpl) ReadEnum()
    {
        Assert.True(AlEnumMetadataRegistry.TryGet(EnumId, out var entry),
            $"enum {EnumId} is not in AlEnumMetadataRegistry at all");
        var posted = Array.IndexOf(entry.Options, "Posted");
        Assert.True(posted >= 0, "the enum entry does not carry a 'Posted' value");
        return (entry.Captions?[posted], entry.DefaultImplementations, entry.UnknownImplementations);
    }

    [Fact]
    public void ADependencysEnumCaptionAndImplementationFallbacksSurviveABundleReload()
    {
        AlEnumMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
        var appPath = WriteEnumSymbolApp("ErsApp");

        // Cycle 1 — what Program.cs does after DependencyLoader.LoadAll.
        RecordPatches.AddBcAppPath(appPath);
        var first = ReadEnum();
        Assert.Equal(DeclaredCaption, first.PostedCaption);
        Assert.Equal(new[] { DefaultImplCodeunitId }, first.DefaultImpl);
        Assert.Equal(new[] { UnknownImplCodeunitId }, first.UnknownImpl);

        // The reload boundary, spelled the way the runner spells it: the registry is emptied
        // (BcRuntime.ResetForNewBundleReload's own AlEnumMetadataRegistry.Clear()) and the
        // per-bundle reset runs. Asserted, not assumed — if the registry were NOT emptied here,
        // the cycle-2 assertion below would pass without re-registration having happened and
        // this test would be pinning nothing.
        AlEnumMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
        Assert.False(AlEnumMetadataRegistry.TryGet(EnumId, out _),
            "the reload did not empty the enum registry — the rest of this test would be vacuous");

        // Cycle 2 — the same closure is re-registered, every bundle registering its FULL
        // resolved closure rather than a delta. This is the step AddBcAppPath's
        // already-registered early-return would swallow if ClearPerBundleBcAppPaths stopped
        // clearing _bcAppPaths.
        RecordPatches.AddBcAppPath(appPath);
        var second = ReadEnum();
        Assert.Equal(DeclaredCaption, second.PostedCaption);
        Assert.Equal(new[] { DefaultImplCodeunitId }, second.DefaultImpl);
        Assert.Equal(new[] { UnknownImplCodeunitId }, second.UnknownImpl);
    }

    [Fact]
    public void WithoutTheReload_ReRegisteringTheSamePathIsStillANoOp()
    {
        // The control, and the reason the test above is a statement about the RELOAD rather
        // than about AddBcAppPath being idempotent. Inside one bundle, a second AddBcAppPath
        // for a path already registered must do nothing — that early-return is a real
        // optimisation and the fix must not be "re-read the symbols every time". So: blank the
        // registry WITHOUT resetting _bcAppPaths, re-register, and the enum must stay absent.
        // If this ever starts passing enum data back, the early-return is gone and the test
        // above has stopped discriminating.
        AlEnumMetadataRegistry.Clear();
        RecordPatches.ResetForReload();
        var appPath = WriteEnumSymbolApp("ErsControlApp");

        RecordPatches.AddBcAppPath(appPath);
        Assert.True(AlEnumMetadataRegistry.TryGet(EnumId, out _));

        AlEnumMetadataRegistry.Clear();          // registry only — registration list untouched
        RecordPatches.AddBcAppPath(appPath);     // early-returns: the path is still registered

        Assert.False(AlEnumMetadataRegistry.TryGet(EnumId, out _),
            "AddBcAppPath re-read a path it had already registered — the reload test above no "
            + "longer proves that the reload's clear is what brings the enums back");
    }
}
