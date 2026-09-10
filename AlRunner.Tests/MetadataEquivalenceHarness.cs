// MetadataEquivalenceHarness — drives the total comparison of BC's own metadata emitter
// against the runner's SymbolReference-derived metadata, for one ground-truth bundle.
//
// Issue #3533. The standard: the derivation is acceptable only if it builds the SAME objects
// BC's emitter builds, member for member, with every difference declared.
//
// The two sides, and why they are the same TYPE
// ---------------------------------------------
//   BC's:  MetaTable.CreateMetaTableFromXml(<the emitter's own document>)
//   ours:  RecordPatches.GetOrBuildNCLMetaTable(id).GetMetaTableOriginal()
//
// The runner builds a Types.Metadata.MetaTable and hands it to NCLMetaTable.CreateFromMetaTable;
// GetMetaTableOriginal() hands that same MetaTable back. So both sides are
// Microsoft.Dynamics.Nav.Types.Metadata.MetaTable and the comparison is member-for-member on
// one type, not a translation between two shapes.
//
// Why the bundle is not checked in: see docs/metadata-equivalence.md § "Where the ground
// truth lives".

using System.Reflection;
using System.Text.Json;
using System.Xml;
using AlRunner.Metadata;
using AlRunner.Patches;

namespace AlRunner.Tests;

/// <summary>One object in a ground-truth bundle, as its manifest describes it.</summary>
internal sealed record GroundTruthObject(string Kind, int Id, string Name, string File, string Sha256);

internal sealed record GroundTruthBundle(
    string Directory,
    int Schema,
    Guid AppId,
    string AppName,
    string AppPublisher,
    string AppVersion,
    string BcBuild,
    IReadOnlyDictionary<string, int> Census,
    IReadOnlyList<GroundTruthObject> Objects)
{
    public string Label => $"{AppPublisher}_{AppName} {AppVersion}";
}

/// <summary>What one bundle's comparison found, including what it did NOT cover.</summary>
internal sealed record MetadataEquivalenceReport(
    GroundTruthBundle Bundle,
    IReadOnlyList<string> KindsCompared,
    IReadOnlyList<string> KindsNotCompared,
    int ObjectsCompared,
    IReadOnlyList<string> Unbuildable,
    IReadOnlyList<MetadataDifference> Differences)
{
    public string Summary =>
        $"{Bundle.Label} (BC {Bundle.BcBuild}): compared {ObjectsCompared} object(s) of kind(s) " +
        $"[{string.Join(", ", KindsCompared)}]; NOT compared: [{string.Join(", ", KindsNotCompared)}]; " +
        $"{Differences.Count} difference(s) across {Differences.Select(d => d.Signature).Distinct().Count()} member(s)";
}

internal static class MetadataEquivalencePaths
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public static string ExpectationsDir()
        => Path.Combine(RepoRoot, "tests", "expectations", "metadata-equivalence");

    public static string AllowlistFile() => Path.Combine(ExpectationsDir(), "allowlist.json");

    public static string AppsFile() => Path.Combine(ExpectationsDir(), "apps.json");

    /// <summary>
    /// Where the generator writes bundles. A sibling of the artifacts root, because a bundle
    /// belongs to the BC build it was generated from and is regenerated when that moves.
    /// </summary>
    public static string GroundTruthRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("AL_RUNNER_METADATA_GROUND_TRUTH");
        if (!string.IsNullOrEmpty(explicitRoot)) return explicitRoot;
        var artifacts = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir;
        return Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(artifacts))
                            ?? artifacts, "metadata-ground-truth");
    }

    /// <summary>
    /// Bundles for the BC build this process actually loaded — never the whole root.
    ///
    /// A dev box accumulates one directory per build it has generated, and every one of them
    /// holds a System Application bundle with the same table ids. Registering two of those
    /// packages into RecordPatches' process-global state makes the second table id lose to the
    /// first, and the comparison then measures one app's metadata against another app's
    /// symbols — a wrong answer, arrived at silently, of exactly the kind this harness exists
    /// to catch. CI has one build so it would never have shown up there.
    /// </summary>
    public static string GroundTruthDirForThisBuild()
        => Path.Combine(GroundTruthRoot(),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(
                AlRunner.Infrastructure.BcArtifacts.ServiceTierDir)));
}

internal static class MetadataEquivalenceHarness
{
    /// <summary>Bundle schema this harness understands. See tools/metadata-ground-truth.</summary>
    private const int SupportedSchema = 1;

    /// <summary>
    /// The object kinds this harness compares today. Everything else in a bundle is reported
    /// as NOT compared rather than dropped — covering less has to be visible, or the harness
    /// becomes the silent-partial-answer it was built to prevent.
    ///
    /// #3782 is the programme that empties the NOT-compared list, one kind per pull request.
    /// The order and remaining kinds are on that issue.
    /// </summary>
    internal static readonly string[] ComparedKinds = { "MetaTable", "PageDefinition" };

    public static IReadOnlyList<GroundTruthBundle> LoadBundles(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<GroundTruthBundle>();
        var bundles = new List<GroundTruthBundle>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
            bundles.Add(ReadManifest(manifestPath));
        return bundles;
    }

    private static GroundTruthBundle ReadManifest(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        var schema = root.GetProperty("schema").GetInt32();
        if (schema != SupportedSchema)
            throw new InvalidDataException(
                $"{manifestPath}: bundle schema {schema}, this harness understands {SupportedSchema}. " +
                "Regenerate with tools/metadata-ground-truth rather than reading fields that may have moved.");

        var app = root.GetProperty("app");
        var census = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in root.GetProperty("census").EnumerateObject())
            census[kv.Name] = kv.Value.GetInt32();

        var objects = root.GetProperty("objects").EnumerateArray()
            .Select(o => new GroundTruthObject(
                o.GetProperty("Kind").GetString()!,
                o.GetProperty("Id").GetInt32(),
                o.GetProperty("Name").GetString()!,
                o.GetProperty("File").GetString()!,
                o.GetProperty("Sha256").GetString()!))
            .ToArray();

        return new GroundTruthBundle(
            Path.GetDirectoryName(manifestPath)!, schema,
            app.GetProperty("Id").GetGuid(), app.GetProperty("Name").GetString()!,
            app.GetProperty("Publisher").GetString()!, app.GetProperty("Version").GetString()!,
            root.GetProperty("bcBuild").GetString()!, census, objects);
    }

    /// <summary>
    /// Compare one bundle. <paramref name="appPackagePath"/> is the very .app the bundle was
    /// generated from — the runner reads its SymbolReference.json, so a different build of the
    /// same app would compare two things that were never meant to agree.
    /// </summary>
    public static MetadataEquivalenceReport Compare(GroundTruthBundle bundle, string appPackagePath)
    {
        RecordPatches.AddBcAppPath(appPackagePath);

        var getOriginal = Type.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable, Microsoft.Dynamics.Nav.Ncl")
            ?.GetMethod("GetMetaTableOriginal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "NCLMetaTable.GetMetaTableOriginal is not reachable. Without it there is no way to " +
                "read back the MetaTable the runner built, and the comparison would silently " +
                "become NCLMetaTable-vs-MetaTable — two different types.");

        var fromXml = Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaTable, Microsoft.Dynamics.Nav.Types")
            ?.GetMethod("CreateMetaTableFromXml", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("MetaTable.CreateMetaTableFromXml is not reachable.");

        // BC's own reader for a PageDefinition document: a CONSTRUCTOR, not a factory — pages
        // have no CreatePageDefinitionFromXml the way MetaTable has CreateMetaTableFromXml.
        //
        // PageDefinition, NOT MetaPageDefinition, and the difference is not cosmetic. Both
        // types expose a public (XmlNode) constructor and both accept the document without
        // throwing, but MetaPageDefinition's IGNORES it: measured on BC 28.1.49838.53910
        // against the emitter's own Page 257 "Source Codes", it answers ID=0, Name=null,
        // Properties=null, Content=null, while PageDefinition answers ID=257,
        // Name="Source Codes" with both Properties and Content populated. Passing that empty
        // object in as BOTH sides is a comparison of nothing against nothing: 235 pages
        // compared, 0 differences, every other test in this file still green. See
        // MetadataEquivalencePageOracleTests, which pins exactly that asymmetry so this cannot
        // be silently switched back.
        var pageType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.PageDefinition, Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("PageDefinition is not reachable.");
        var pageFromXml = pageType.GetConstructor(new[] { typeof(XmlNode) })
            ?? throw new InvalidOperationException(
                "PageDefinition has no (XmlNode) constructor. Without it there is no way to " +
                "read BC's emitted PageDefinition document back into BC's own object model, and " +
                "the comparison would become a hand-written XML walk against the runner — two " +
                "derivations, no oracle.");

        var differences = new List<MetadataDifference>();
        var unbuildable = new List<string>();
        int compared = 0;

        foreach (var obj in bundle.Objects.Where(o => ComparedKinds.Contains(o.Kind, StringComparer.Ordinal))
                     .OrderBy(o => o.Kind, StringComparer.Ordinal).ThenBy(o => o.Id))
        {
            var xmlPath = Path.Combine(bundle.Directory, obj.File);
            var document = new XmlDocument();
            document.Load(xmlPath);

            object? expected;
            object? actual;
            string objectKey;

            if (obj.Kind == "PageDefinition")
            {
                objectKey = $"Page {obj.Id}";
                try
                {
                    expected = pageFromXml.Invoke(new object?[] { document.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own PageDefinition " +
                                    $"constructor threw — {Describe(ex)}");
                    continue;
                }
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own PageDefinition " +
                                    "constructor produced null");
                    continue;
                }

                try
                {
                    // The runner's page-metadata producer, and the exact document every runner
                    // consumer of page metadata reads: RunnerXmlMetadataLoader hands this XML to
                    // BC, which deserializes it with the same constructor used for BC's side
                    // above. So the comparison is one type against itself, and it measures the
                    // document the runner actually ships rather than a test-only rendering.
                    var runnerXml = RecordPatches.TryBuildDependencyPageMetadata(obj.Id);
                    if (runnerXml is null)
                    {
                        unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner built no page " +
                                        "metadata at all");
                        continue;
                    }
                    var runnerDoc = new XmlDocument();
                    runnerDoc.LoadXml(runnerXml);
                    actual = pageFromXml.Invoke(new object?[] { runnerDoc.DocumentElement });
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }
            else
            {
                objectKey = $"Table {obj.Id}";
                expected = fromXml.Invoke(null, new object?[] { document.DocumentElement, 0 });
                if (expected is null)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own CreateMetaTableFromXml returned null");
                    continue;
                }

                actual = null;
                try
                {
                    var ncl = RecordPatches.GetOrBuildNCLMetaTable(obj.Id);
                    if (ncl is not null) actual = getOriginal.Invoke(ncl, null);
                }
                catch (Exception ex)
                {
                    unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — {Describe(ex)}");
                    continue;
                }
            }

            if (actual is null)
            {
                unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner built no metadata at all");
                continue;
            }

            compared++;
            differences.AddRange(obj.Kind == "PageDefinition"
                ? MetadataObjectDiff.Compare(expected, actual, objectKey, PageDiffOptions)
                : MetadataObjectDiff.Compare(expected, actual, objectKey));
        }

        var kindsPresent = bundle.Census.Keys.ToArray();
        return new MetadataEquivalenceReport(
            bundle,
            kindsPresent.Where(k => ComparedKinds.Contains(k, StringComparer.Ordinal)).ToArray(),
            kindsPresent.Where(k => !ComparedKinds.Contains(k, StringComparer.Ordinal)).ToArray(),
            compared, unbuildable, differences);
    }

    /// <summary>
    /// Page control collections pair by the control's own ID, not by position.
    ///
    /// Position is the differ's default because order is meaningful in AL, and for a table's
    /// fields and keys that is the right call. It is wrong here for a structural reason: BC's
    /// Controls list holds the page's ORDINARY field controls as well as its part controls,
    /// and the runner reconstructs only the parts (DependencyPageMetadataXml's header says why
    /// — a field control's value binding lives in the .app's IL, not in this XML). So the two
    /// lists legitimately differ in length, and every element after the first divergence shifts.
    ///
    /// Measured on BC 28.1.49838.53910 before this option was passed: System Application
    /// page 4312 reported its part 'InputMessagePart' as differing from 'LogsPart', page 9855
    /// 'Permissions' from 'MetadataPermissions', and so on — 12 fabricated Name/ID/PagePartID
    /// triples across seven pages, none of which is a disagreement about any control. Pairing
    /// by id turns that cascade back into what it is: the runner does not build the ordinary
    /// controls, reported once per control as a presence difference.
    ///
    /// #Ordinal still fires when both sides hold the same id set, so a genuine reordering is
    /// not hidden — see MetadataObjectDiff.TryPairById and
    /// MetadataObjectDiffTests.Id_paired_elements_still_report_a_reordering.
    /// </summary>
    private static readonly MetadataObjectDiffOptions PageDiffOptions = new()
    {
        PairByIdMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "MetaTable.Fields",
            "ContentDefinition.Containers",
            "ControlContainerDefinition.Controls",
            "ControlGroupDefinition.Controls",
            "RepeaterDefinition.Controls",
            "GridLayoutDefinition.Controls",
            "ColumnLayoutDefinition.Controls",
            "PageDefinition.ActionContainers",
            "ActionContainerDefinition.Actions",
            "ActionGroupDefinition.Actions",
        },
    };

    /// <summary>
    /// A reflected call reports the real fault as InnerException; the outer
    /// TargetInvocationException says only "an exception was thrown", which turns every
    /// unbuildable line into the same useless sentence.
    /// </summary>
    private static string Describe(Exception ex)
    {
        var real = ex.InnerException ?? ex;
        return $"{real.GetType().Name}: {real.Message}";
    }

    /// <summary>
    /// The .app a bundle was generated from, located by identity rather than by a path baked
    /// into the manifest. Returns null when this box has no such package — which the caller
    /// must report, never absorb.
    /// </summary>
    public static string? FindAppPackage(GroundTruthBundle bundle)
    {
        // Scoped to the build's own artifact directory rather than the whole root, for the
        // same reason GroundTruthDirForThisBuild is: several builds of one app are on a dev
        // box, they carry the same object ids, and picking the wrong one is not detectable
        // downstream.
        var artifacts = Path.Combine(
            AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir, bundle.BcBuild);
        var wanted = $"{bundle.AppPublisher}_{bundle.AppName}_{bundle.AppVersion}.app";
        if (!Directory.Exists(artifacts)) return null;
        foreach (var candidate in Directory.EnumerateFiles(artifacts, "*.app", SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileName(candidate), wanted, StringComparison.Ordinal))
                return candidate;
        return null;
    }
}
