// GeneratorFingerprint — which revision of this generator wrote a ground-truth bundle (#4536).
//
// Compiled into the generator AND linked as source into AlRunner.Tests, so the value a bundle
// records and the value the harness expects come from one implementation. A second copy would
// be free to drift and would then refuse every bundle, or none.
//
// Scope: every *.cs under the generator's directory (the SDK default glob compiles **/*.cs),
// minus bin/ and obj/, plus the top-level *.csproj. The linked
// EngineClosure.cs is deliberately outside it: it decides whether generation may start, never
// what a bundle contains. See docs/metadata-equivalence.md#a-bundle-records-the-generator-that-wrote-it.

using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Tools.MetadataGroundTruth;

internal static class GeneratorFingerprint
{
    /// <summary>The manifest property the generator writes and the harness compares.</summary>
    public const string ManifestProperty = "generatorFingerprint";

    /// <summary>The file that identifies a directory as the generator's source.</summary>
    public const string ProjectFileName = "MetadataGroundTruth.csproj";

    public static string Compute(string sourceDir)
    {
        if (!File.Exists(Path.Combine(sourceDir, ProjectFileName)))
            throw new DirectoryNotFoundException(
                $"'{sourceDir}' holds no {ProjectFileName}, so it is not the ground-truth " +
                "generator's source directory and a fingerprint of it would identify nothing.");

        var files = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
            .Select(p => (Path: p, Rel: Path.GetRelativePath(sourceDir, p).Replace('\\', '/')))
            .Where(f => !IsBuildOutput(f.Rel))
            .Concat(Directory.EnumerateFiles(sourceDir, "*.csproj", SearchOption.TopDirectoryOnly)
                .Select(p => (Path: p, Rel: Path.GetFileName(p))))
            .OrderBy(f => f.Rel, StringComparer.Ordinal);

        var listing = new StringBuilder();
        foreach (var (f, rel) in files)
            listing.Append(rel).Append('\0')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant())
                .Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())))
            .ToLowerInvariant();
    }

    // obj/ holds build-generated *.cs (AssemblyInfo, GlobalUsings) that change per build.
    private static bool IsBuildOutput(string rel) =>
        rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal);
}
