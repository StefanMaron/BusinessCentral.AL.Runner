// EnumMetadataRegistryCollectionGuardTests — #4195.
//
// The collection that serialises AlEnumMetadataRegistry mutators only works for classes that
// join it, and nothing made joining necessary: PermissionMetadataStaticsSerialCollection and
// RecordPatchesSerialCollection both say so in their own headers ("it only works for classes
// that remember to join"). A new class calling Clear() without the attribute reintroduces the
// race, and the symptom is a test that fails roughly one run in three — which reads as a flake
// and teaches everyone to re-roll CI rather than to look.
//
// This guard closes that. It reads the test SOURCES rather than reflecting over the assembly,
// because the claim is about what a future editor writes, and the file is where they write it.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class EnumMetadataRegistryCollectionGuardTests
{
    private static readonly string TestsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests"));

    /// <summary>
    /// Every .cs under AlRunner.Tests that CALLS AlEnumMetadataRegistry.Clear(), reading
    /// <see cref="CSharpSource.CodeOnly"/> so neither prose nor an embedded AL fixture counts as
    /// a call site.
    /// </summary>
    private static IEnumerable<string> MutatingSources()
    {
        foreach (var path in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (CSharpSource.ReadCodeOnly(path).Contains("AlEnumMetadataRegistry.Clear()", StringComparison.Ordinal))
                yield return path;
        }
    }

    /// <summary>
    /// Whether <paramref name="sourceText"/> declares a <c>[Collection(...)]</c> attribute.
    /// Exposed over TEXT so the exemption can be tested against synthetic inputs, rather than
    /// only against whatever this suite happens to contain.
    /// </summary>
    internal static bool DeclaresCollection(string sourceText) =>
        Regex.IsMatch(CSharpSource.CodeOnly(sourceText), @"\[Collection\(");

    [Fact]
    public void TheGuardCanSeeTheTestSources_SoAnEmptyResultIsNotAFalsePass()
    {
        // The third state: a guard that measures nothing must not report its success state.
        // If the directory probe ever breaks, MutatingSources() returns empty and the
        // membership test below would pass vacuously — so assert the population is non-trivial
        // first, with the count that was true when this was written as the floor.
        Assert.True(Directory.Exists(TestsDir), $"cannot see the test sources at '{TestsDir}'");
        // 8 real call sites once string literals are excluded (#4251). The floor was 7 when the
        // scan still counted this file itself, so the number moved for a reason worth naming:
        // the population did not change, the measurement did.
        Assert.True(MutatingSources().Count() >= 8,
            $"expected at least the 8 known AlEnumMetadataRegistry.Clear() callers under '{TestsDir}', "
            + $"found {MutatingSources().Count()}. Either the probe stopped seeing the sources, or a "
            + "caller was legitimately deleted — check which before lowering this floor, and lower "
            + "it only for the second. The floor exists so an empty result cannot pass the "
            + "membership test below vacuously, not to pin the exact count.");
    }

    [Fact]
    public void EveryClassThatClearsTheRegistry_JoinsTheSerialCollection()
    {
        var offenders = new List<string>();
        foreach (var path in MutatingSources())
        {
            // Already serialised by a DIFFERENT collection is fine: two DisableParallelization
            // collections never run concurrently with each other either.
            if (DeclaresCollection(File.ReadAllText(path)))
                continue;
            offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "these test classes mutate the process-wide AlEnumMetadataRegistry but declare no "
            + "[Collection], so xunit runs them in parallel with the other mutators and each "
            + "sees the others' Clear() (#4195): "
            + string.Join(", ", offenders)
            + $". Add [Collection({nameof(EnumMetadataRegistrySerialCollection)}.Name)].");
    }
}
