// DependencyAppSymbolWalkSourceGuardTests — the source-level half of issue #3143.
//
// WHY A SOURCE GUARD AT ALL
//   DependencySymbolReadFailureTests pins the ten sites #3143 converted. It cannot pin the
//   ELEVENTH, which does not exist yet: the shape is a two-line idiom, it was copied ten
//   times before anyone noticed, and each copy reads as ordinary defensive code. #3031 fixed
//   two of them and #3117 fixed a third INDEPENDENTLY AND CONCURRENTLY, without either agent
//   seeing the other — which is exactly what a repeated idiom with no mechanical check looks
//   like from the inside.
//
//   So this file holds the property directly: every `BcAppSymbolCache.Get(` call site in the
//   runner is named here, with what it does on failure and why that is right. A new call site
//   fails this test until someone writes that sentence down.
//
// WHAT IS DELIBERATELY NOT ASSERTED
//   Not "no catch anywhere near a Get". Two of the sites below catch legitimately, and a
//   guard that forbade catching outright would be wrong rather than strict. The guard is a
//   census, which is the part a reviewer cannot do by eye.

using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyAppSymbolWalkSourceGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Every file that may call <c>BcAppSymbolCache.Get(</c>, with the number of call sites
    /// it is allowed and why each is correct. Adding a call site anywhere else, or an extra
    /// one here, fails — deliberately, because "one more copy of the walk" is precisely the
    /// change that needs a human to look at it.
    /// </summary>
    private static readonly (string File, int Sites, string Why)[] Allowed =
    {
        ("AlRunner/Patches/RecordPatches.DependencyAppSymbolWalk.cs", 1,
            "#3143: THE walk. Vanished -> [warn] + skip; present-but-unreadable -> "
            + "BcAppSymbolReadException. Every table-facing read goes through this."),

        ("AlRunner/Patches/RecordPatches.BcAppFallback.cs", 3,
            "#2712: AddBcAppPath's eager read (refuses, and the path is never registered); "
            + "EnsureBcSymbolTableIndex (vanished -> [warn], unreadable -> refuses); and "
            + "DescribeParsedSymbolState, which catches broadly ON PURPOSE and returns "
            + "\"unreadable:<Type>\" — a diagnostic string whose job is to DESCRIBE the "
            + "state, and which differs from every healthy key, so it forces a cache miss "
            + "rather than colliding with a good answer."),

        ("AlRunner/Patches/RecordPatches.AllObjVirtualTable.cs", 1,
            "#3117/#3133's BuildObjectOwnerIndex: refuses with AllObjShapeGap rather than "
            + "BcAppSymbolReadException because the owner is a STORED COLUMN VALUE on rows "
            + "AllObj is already writing, so the refusal names the AllObj shape. #3143 added "
            + "the vanished skip in front of it."),

        ("AlRunner/Patches/RecordPatches.AggregatePermissionSetVirtualTable.cs", 1,
            "#3031: BuildKnownAppNameIndex. Vanished -> [warn] + skip; unreadable -> refuses."),

        ("AlRunner/Patches/RecordPatches.MetadataPermissionSetVirtualTable.cs", 1,
            "#3031: EnumerateKnownPermissionSets. Same split."),

        ("AlRunner/Patches/EnumMetadataPatches.cs", 1,
            "#3143: AlEnumMetadataRegistry.RegisterFromAppPath — no live callers, but public, "
            + "so its swallow was converted to a refusal rather than left for a future caller "
            + "to inherit."),
    };

    private static int CountSites(string relativePath)
    {
        var full = Path.Combine(RepoRoot, relativePath);
        Assert.True(File.Exists(full), $"{relativePath} not found — renamed or removed.");
        var count = 0;
        foreach (var raw in File.ReadAllLines(full))
        {
            var line = raw.TrimStart();
            // Comments quote the idiom on purpose (the walk's own header does); a census of
            // CODE must not count those.
            if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("*")) continue;
            var i = 0;
            while ((i = line.IndexOf("BcAppSymbolCache.Get(", i, StringComparison.Ordinal)) >= 0)
            {
                count++;
                i += 1;
            }
        }
        return count;
    }

    [Fact]
    public void EveryBcAppSymbolCacheGetCallSiteIsAccountedFor()
    {
        foreach (var (file, sites, why) in Allowed)
            Assert.True(CountSites(file) == sites,
                $"{file}: expected {sites} BcAppSymbolCache.Get( call site(s), found "
                + $"{CountSites(file)}.\nWhy this file is allowed any: {why}\n"
                + "A NEW call site here must state, in a comment at the site, what it does "
                + "when the read fails — see .claude/rules/loud-failures.md and issue #3143 — "
                + "and then update this census.");
    }

    [Fact]
    public void NoOtherFileCallsBcAppSymbolCacheGet()
    {
        var allowed = new HashSet<string>(
            Allowed.Select(a => a.File.Replace('/', Path.DirectorySeparatorChar)),
            StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();
        foreach (var full in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot, "AlRunner"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot, full);
            if (allowed.Contains(relative)) continue;
            if (CountSites(relative) > 0) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These files call BcAppSymbolCache.Get( without being in the #3143 census:\n  "
            + string.Join("\n  ", offenders)
            + "\nEvery dependency-symbol read must say what it does when the read fails. The "
            + "default is to go through RecordPatches.EnumerateRegisteredBcAppSymbols, which "
            + "already makes the vanished/unreadable split; a site that cannot must justify "
            + "itself in DependencyAppSymbolWalkSourceGuardTests.Allowed.");
    }
}
