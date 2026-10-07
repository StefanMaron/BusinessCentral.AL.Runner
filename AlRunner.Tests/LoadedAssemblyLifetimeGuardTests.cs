using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards the invariant behind #5414: a test class must not remove a directory an assembly was
/// loaded from, and no test may build compile references from the loaded assemblies' paths
/// without going through <see cref="LoadedAssemblyReferences"/>.
///
/// The failure it prevents was cross-class and scheduling-dependent. DependencyR2rChunkModuleIdentityTests
/// loaded R2R chunks from its own scratch cache and deleted it on Dispose; the assemblies stayed in
/// AppDomain.GetAssemblies() with a dead Location, so whichever class next called
/// MetadataReference.CreateFromFile over that list failed (10 rows, passing alone). CI was spared only
/// by the order CollectionCostOrderer happened to give two serial collections.
///
/// Both scans are textual, like ScratchDirOwnershipGuardTests, because the offender and the victim
/// are different classes that may run in either order, or on different lanes. A runtime check can
/// only see the damage if the victim runs after the offender.
///
/// SCOPE: a tripwire, not a proof. It reads one file at a time, so a load made through a helper in
/// another file, or through an AssemblyLoadContext enumeration, is not seen.
/// </summary>
public sealed class LoadedAssemblyLifetimeGuardTests
{
    private static readonly string TestsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests"));

    /// <summary>The ways a test in this project loads an assembly FROM A PATH into the process.
    /// <c>Assembly.Load(byte[])</c> is absent on purpose: it leaves Location empty, so nothing on
    /// disk can go missing.</summary>
    internal static readonly string[] LoadExpressions =
    [
        "LoadFromAssemblyPath(",
        "Assembly.LoadFrom(",
        "Assembly.LoadFile(",
        "Assembly.UnsafeLoadFrom(",
        ".LoadAll(",
    ];

    /// <summary>Every spelling of a delete this project uses or could reach for: the static
    /// <c>Directory</c>/<c>File</c> calls, the <c>DirectoryInfo</c>/<c>FileInfo</c> instance forms, and
    /// <c>FileSystem.Delete*</c>. A file that loads and deletes is allowlisted, never scanned around.</summary>
    internal static readonly string[] DeleteExpressions =
    [
        "Directory.Delete(",
        "File.Delete(",
        ".Delete(true",
        ".Delete(recursive",
        ".Delete()",
        "FileSystem.DeleteDirectory(",
        "FileSystem.DeleteFile(",
    ];

    /// <summary>
    /// Files that load an assembly from a path AND delete something, with why the deletion cannot
    /// remove a loaded assembly's file. No count: the entry states the invariant, and a stale entry
    /// (the file no longer does both) fails the second fact below.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["CorruptSidecarLoaderCallSiteTests.cs"] =
            "the DLLs it feeds DependencyLoader.LoadAll are 5 bogus bytes, so nothing is ever loaded; "
            + "the deletions are its fixture directory and the chunk it minted",
        ["ExtensionRuntimeDeltasTests.cs"] =
            "Assembly.LoadFrom reads the provisioned service-tier DLL, never a scratch path; the "
            + "deletions are the per-test fixture .app directories",
        ["RelativeCacheRootTests.cs"] =
            "the one LoadFromAssemblyPath is asserted to throw BadImageFormatException on a synthetic "
            + "chunk, so nothing is loaded from the scratch directory it deletes",
    };

    /// <summary>The helper and this guard name the scanned strings as literals.</summary>
    private static readonly HashSet<string> ScanExempt = new(StringComparer.Ordinal)
    {
        "LoadedAssemblyReferences.cs",
        "LoadedAssemblyLifetimeGuardTests.cs",
    };

    private static string Key(string path) =>
        Path.GetRelativePath(TestsDir, path).Replace(Path.DirectorySeparatorChar, '/');

    private static IReadOnlyList<string> TestSources()
    {
        var paths = Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Key(p).Split('/').Any(seg => seg is "bin" or "obj"))
            .ToList();
        Assert.True(paths.Count > 0, $"no .cs sources under {TestsDir}: the guard is reading nothing.");
        return paths;
    }

    /// <summary>The non-comment lines of <paramref name="source"/>, numbered from 1.</summary>
    internal static IEnumerable<(int Line, string Text)> CodeLines(string source)
    {
        var n = 0;
        foreach (var raw in source.Split('\n'))
        {
            n++;
            var t = raw.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*')) continue;
            yield return (n, raw);
        }
    }

    internal static bool Contains(string source, IEnumerable<string> expressions)
        => CodeLines(source).Any(l => expressions.Any(e => l.Text.Contains(e, StringComparison.Ordinal)));

    private static Dictionary<string, string> Sources()
        => TestSources().Where(p => !ScanExempt.Contains(Key(p)))
            .ToDictionary(Key, File.ReadAllText, StringComparer.Ordinal);

    [Fact]
    public void ATestClassThatLoadsAnAssemblyFromAPath_DeletesNothing_ExceptTheAllowlisted()
    {
        var offenders = Sources()
            .Where(kv => Contains(kv.Value, LoadExpressions) && Contains(kv.Value, DeleteExpressions))
            .Where(kv => !Allowed.ContainsKey(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These test sources load an assembly from a path and also delete files or directories:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nAn assembly stays in AppDomain.GetAssemblies() after its directory is deleted, with a "
            + "Location naming a file that is gone, and a later class building references from the "
            + "loaded assemblies then fails (#5414). Load from a directory TestScratch owns and leave "
            + "it for ScratchDirs to remove at host exit. If the deletion provably cannot touch a "
            + "loaded file, add the file to Allowed with the reason.");
    }

    [Fact]
    public void EveryAllowlistEntry_StatesItsReason()
    {
        var blank = Allowed.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(blank.Count == 0,
            "These Allowed entries carry no reason, so nothing says why their deletions cannot remove a "
            + "loaded file: " + string.Join(", ", blank));
    }

    [Fact]
    public void EveryAllowlistEntry_StillLoadsAndDeletes_SoTheListCannotGoStale()
    {
        var sources = Sources();
        var stale = Allowed.Keys
            .Where(k => !sources.TryGetValue(k, out var s)
                        || !Contains(s, LoadExpressions) || !Contains(s, DeleteExpressions))
            .ToList();

        Assert.True(stale.Count == 0,
            "These Allowed entries no longer both load an assembly and delete, so they only "
            + "pre-approve the next violation in that file: " + string.Join(", ", stale));
    }

    [Fact]
    public void TheScan_FindsTheClassThatMotivatedIt_AsALoader()
    {
        // Non-vacuity: DependencyR2rChunkModuleIdentityTests is the one class this guard exists
        // for, so the load matcher must see it. A guard that matched nothing would pass for
        // every other file too.
        var sources = Sources();
        Assert.True(sources.TryGetValue("DependencyR2rChunkModuleIdentityTests.cs", out var s));
        Assert.True(Contains(s!, LoadExpressions));
    }

    [Fact]
    public void EveryScannedExpression_FiresOnSyntheticSource_AndNotInAComment()
    {
        foreach (var e in LoadExpressions.Concat(DeleteExpressions))
        {
            Assert.True(Contains($"        var x = y.{e}z);", [e]), $"'{e}' never fires");
            Assert.False(Contains($"        // y.{e}z);", [e]), $"'{e}' fires inside a comment");
            Assert.False(Contains($"     * y.{e}z);", [e]), $"'{e}' fires inside a doc comment");
        }
    }

    /// <summary><c>CreateFromFile(a.Location)</c> over a lambda or loop variable — the shape that
    /// reads a loaded assembly's path. <c>typeof(X).Assembly.Location</c> is a pinned assembly and is
    /// not matched.</summary>
    internal static readonly System.Text.RegularExpressions.Regex ReferenceFromLoopVariable =
        new(@"CreateFromFile\(\s*\w+\.Location!?\s*[,)]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool BuildsReferencesFromLoadedLocations(string source)
        => Contains(source, ["GetAssemblies()"])
           && CodeLines(source).Any(l => ReferenceFromLoopVariable.IsMatch(l.Text));

    [Fact]
    public void NoTestBuildsCompileReferencesFromTheLoadedAssembliesLocations_ExceptTheHelper()
    {
        var offenders = Sources()
            .Where(kv => BuildsReferencesFromLoadedLocations(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These test sources pass an enumerated assembly's Location to MetadataReference.CreateFromFile:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nUse LoadedAssemblyReferences.Build(), which skips an assembly whose file is gone "
            + "(#5414). A bare CreateFromFile(a.Location) over that list throws FileNotFoundException "
            + "in whichever class runs after one that deleted a directory an assembly was loaded from.");
    }

    [Fact]
    public void TheReferenceScan_FiresOnTheOriginalShape_AndNotOnAPinnedAssembly()
    {
        Assert.True(BuildsReferencesFromLoadedLocations(
            "var refs = AppDomain.CurrentDomain.GetAssemblies()\n  .Select(a => MetadataReference.CreateFromFile(a.Location))"));
        Assert.True(BuildsReferencesFromLoadedLocations(
            "var refs = AppDomain.CurrentDomain.GetAssemblies()\n  .Select(a => MetadataReference.CreateFromFile(a.Location!))"));
        Assert.True(BuildsReferencesFromLoadedLocations(
            "var refs = AppDomain.CurrentDomain.GetAssemblies()\n  .Select(a => MetadataReference.CreateFromFile(a.Location, props))"));
        Assert.False(BuildsReferencesFromLoadedLocations(
            "var r = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);"));
        Assert.False(BuildsReferencesFromLoadedLocations(
            "// AppDomain.CurrentDomain.GetAssemblies() CreateFromFile(a.Location)"));
    }
}
