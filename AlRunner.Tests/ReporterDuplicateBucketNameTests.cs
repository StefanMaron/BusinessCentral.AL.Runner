// ReporterDuplicateBucketNameTests — RED→GREEN guard for #1692.
//
// SerializeJsonOutput used to build a Dictionary keyed on Path.GetFileName(BucketPath)
// for the compile-failed buckets. Two bundles sharing a last path segment
// (`al-runner ./appA/src ./appB/src`, or the same directory passed twice) collided on
// that key and threw an unhandled ArgumentException — "An item with the same key has
// already been added. Key: src" — during report serialisation, i.e. AFTER the tests had
// already run, so a completed run's results were discarded with a raw stack trace.
//
// These assert the two collision shapes serialise, that each bucket keeps its OWN
// errors under a distinguishable full path, and that the field stays absent when
// nothing failed to compile.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ReporterDuplicateBucketNameTests
{
    private static BucketResult CompileFailed(string path, params string[] errors) =>
        new(path, BucketStage.CompileFailed, errors, null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    private static JsonElement Serialize(params BucketResult[] buckets)
    {
        var json = Reporter.SerializeJsonOutput(buckets, exitCode: 3);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static List<(string File, List<string> Errors)> CompilationErrors(JsonElement root) =>
        root.GetProperty("compilationErrors").EnumerateArray()
            .Select(e => (
                e.GetProperty("file").GetString()!,
                e.GetProperty("errors").EnumerateArray().Select(x => x.GetString()!).ToList()))
            .ToList();

    [Fact]
    public void SerializeJsonOutput_DistinctBundlesSharingBasename_KeepsBothDistinguishable()
    {
        var a = Path.Combine(Path.DirectorySeparatorChar + "work", "appA", "src");
        var b = Path.Combine(Path.DirectorySeparatorChar + "work", "appB", "src");

        var root = Serialize(
            CompileFailed(a, "Tests.al: COMPILE-FAIL (1): CS0246 appA"),
            CompileFailed(b, "Tests.al: COMPILE-FAIL (1): CS0103 appB"));

        var groups = CompilationErrors(root);
        Assert.Equal(2, groups.Count);
        // Full paths, not the shared "src" basename — otherwise the two bundles are
        // indistinguishable in the output even once the crash is gone.
        Assert.Equal(new[] { a, b }, groups.Select(g => g.File).ToArray());
        // Each bucket keeps its OWN errors — no merging, no cross-contamination.
        Assert.Equal(new[] { "Tests.al: COMPILE-FAIL (1): CS0246 appA" }, groups[0].Errors);
        Assert.Equal(new[] { "Tests.al: COMPILE-FAIL (1): CS0103 appB" }, groups[1].Errors);
    }

    [Fact]
    public void SerializeJsonOutput_SameBundlePassedTwice_EmitsOneEntryPerBucket()
    {
        var dir = Path.Combine(Path.DirectorySeparatorChar + "work", "broken3");

        var root = Serialize(
            CompileFailed(dir, "CS0246 first pass"),
            CompileFailed(dir, "CS0246 second pass"));

        var groups = CompilationErrors(root);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(dir, g.File));
        Assert.Equal(new[] { "CS0246 first pass" }, groups[0].Errors);
        Assert.Equal(new[] { "CS0246 second pass" }, groups[1].Errors);
    }

    // Negative direction: nothing compile-failed → the field must be ABSENT, not an
    // empty array, and a bucket that ran must never leak into it.
    [Fact]
    public void SerializeJsonOutput_NoCompileFailures_OmitsCompilationErrors()
    {
        var ran = new BucketResult(
            Path.Combine(Path.DirectorySeparatorChar + "work", "appA", "src"),
            BucketStage.Ran, Array.Empty<string>(), null,
            new[] { new TestResult("CU", "T", TestOutcome.Pass, null, null, TimeSpan.Zero) },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        var root = Serialize(ran);

        Assert.False(root.TryGetProperty("compilationErrors", out _));
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
    }
}
