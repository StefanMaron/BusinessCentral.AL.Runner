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
}

internal static class MetadataEquivalenceHarness
{
    /// <summary>Bundle schema this harness understands. See tools/metadata-ground-truth.</summary>
    private const int SupportedSchema = 1;

    /// <summary>
    /// The object kinds this harness compares today. Everything else in a bundle is reported
    /// as NOT compared rather than dropped — covering less has to be visible, or the harness
    /// becomes the silent-partial-answer it was built to prevent.
    /// </summary>
    private static readonly string[] ComparedKinds = { "MetaTable" };

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

        var differences = new List<MetadataDifference>();
        var unbuildable = new List<string>();
        int compared = 0;

        foreach (var obj in bundle.Objects.Where(o => ComparedKinds.Contains(o.Kind, StringComparer.Ordinal))
                     .OrderBy(o => o.Id))
        {
            var xmlPath = Path.Combine(bundle.Directory, obj.File);
            var document = new XmlDocument();
            document.Load(xmlPath);

            var expected = fromXml.Invoke(null, new object?[] { document.DocumentElement, 0 });
            if (expected is null)
            {
                unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': BC's own CreateMetaTableFromXml returned null");
                continue;
            }

            object? actual = null;
            try
            {
                var ncl = RecordPatches.GetOrBuildNCLMetaTable(obj.Id);
                if (ncl is not null) actual = getOriginal.Invoke(ncl, null);
            }
            catch (Exception ex)
            {
                unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner threw — " +
                                $"{(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}");
                continue;
            }

            if (actual is null)
            {
                unbuildable.Add($"{obj.Kind} {obj.Id} '{obj.Name}': the runner built no metadata at all");
                continue;
            }

            compared++;
            differences.AddRange(MetadataObjectDiff.Compare(expected, actual, $"Table {obj.Id}"));
        }

        var kindsPresent = bundle.Census.Keys.ToArray();
        return new MetadataEquivalenceReport(
            bundle,
            kindsPresent.Where(k => ComparedKinds.Contains(k, StringComparer.Ordinal)).ToArray(),
            kindsPresent.Where(k => !ComparedKinds.Contains(k, StringComparer.Ordinal)).ToArray(),
            compared, unbuildable, differences);
    }

    /// <summary>
    /// The .app a bundle was generated from, located by identity rather than by a path baked
    /// into the manifest. Returns null when this box has no such package — which the caller
    /// must report, never absorb.
    /// </summary>
    public static string? FindAppPackage(GroundTruthBundle bundle)
    {
        var artifacts = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir;
        var wanted = $"{bundle.AppPublisher}_{bundle.AppName}_{bundle.AppVersion}.app";
        if (!Directory.Exists(artifacts)) return null;
        foreach (var candidate in Directory.EnumerateFiles(artifacts, "*.app", SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileName(candidate), wanted, StringComparison.Ordinal))
                return candidate;
        return null;
    }
}
