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
    /// Every .cs under AlRunner.Tests that CALLS AlEnumMetadataRegistry.Clear() — comment lines
    /// are stripped first, because this guard's own collection file and this file both describe
    /// the call in prose, and a guard that matches its own documentation reports offenders that
    /// do not exist. Measured: without the strip it named EnumMetadataRegistrySerialCollection.cs,
    /// which contains no code at all.
    /// </summary>
    private static IEnumerable<string> MutatingSources()
    {
        foreach (var path in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = string.Join('\n',
                File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            if (code.Contains("AlEnumMetadataRegistry.Clear()", StringComparison.Ordinal))
                yield return path;
        }
    }

    [Fact]
    public void TheGuardCanSeeTheTestSources_SoAnEmptyResultIsNotAFalsePass()
    {
        // The third state: a guard that measures nothing must not report its success state.
        // If the directory probe ever breaks, MutatingSources() returns empty and the
        // membership test below would pass vacuously — so assert the population is non-trivial
        // first, with the count that was true when this was written as the floor.
        Assert.True(Directory.Exists(TestsDir), $"cannot see the test sources at '{TestsDir}'");
        Assert.True(MutatingSources().Count() >= 7,
            $"expected at least the 7 known AlEnumMetadataRegistry.Clear() callers under '{TestsDir}', "
            + $"found {MutatingSources().Count()} — the probe is broken, not the tree.");
    }

    [Fact]
    public void EveryClassThatClearsTheRegistry_JoinsTheSerialCollection()
    {
        var offenders = new List<string>();
        foreach (var path in MutatingSources())
        {
            var text = File.ReadAllText(path);
            // Already serialised by a DIFFERENT collection is fine: two DisableParallelization
            // collections never run concurrently with each other either.
            if (Regex.IsMatch(text, @"\[Collection\("))
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
