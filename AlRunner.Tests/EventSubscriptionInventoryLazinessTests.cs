// EventSubscriptionInventoryLazinessTests — issue #5099.
//
// The Event Subscription inventory (BC's NavGlobal.EventSubscriptionMetadata, served as table
// 2000000140) costs one NavEventSubscription ctor per subscriber — several seconds once the Base
// Application's subscribers are in scope. It is built when AL first reads 2000000140, never at
// injection time. The observable is the runner's own `[Subscribers] EventSubscriptionMetadata
// seeded: rows=N` line, visible under --verbose; the `[Subscribers] registered` line every run
// prints is the control that proves the verbose stream was not filtered.
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class EventSubscriptionInventoryLazinessTests
{
    private const int SpawnTimeoutMs = 180_000;
    private const string SeededLine = "[Subscribers] EventSubscriptionMetadata seeded: rows=";
    private const string ControlLine = "[Subscribers] registered ";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "EventSubscriptionVirtualTable");

    private static (int ExitCode, string Output) Run(string cacheDir, string testFilter)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");
        sb.Append(" --verbose --test ").Append(testFilter);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var all = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (all) all.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (all) all.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(SpawnTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within {SpawnTimeoutMs / 1000}s.");
        }
        proc.WaitForExit();
        return (proc.ExitCode, all.ToString());
    }

    private static void AssertNotBuilt(int exit, string output, string label)
    {
        Assert.True(exit == 0, $"{label}: expected a clean run. exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit70766.WatchedInsert_WithoutReadingEventSubscription_Succeeds", output);
        Assert.Contains(ControlLine, output);
        Assert.DoesNotContain(SeededLine, output);
    }

    /// <summary>
    /// A run that never reads 2000000140 never builds the inventory, and a run that does read it
    /// builds it and answers the right rows. Three runs against ONE cache root (cold, warm,
    /// warm), because the AL-output cache sits between this change and its observable
    /// (local-test-scope.md): a warm run re-enters injection over a cached bundle.
    /// </summary>
    [Fact]
    public void Inventory_IsBuiltOnlyWhenEventSubscriptionIsRead()
    {
        var cacheDir = TestScratch.Dir("al-runner-esv-lazy");
        try
        {
            var (noReadExit, noReadOut) = Run(cacheDir, "Codeunit70766");
            AssertNotBuilt(noReadExit, noReadOut, "cold run that never reads 2000000140");

            var (readExit, readOut) = Run(cacheDir, "Codeunit70765");
            Assert.True(readExit == 0, $"reading run must pass. exit={readExit}\n{readOut}");
            Assert.Contains(SeededLine, readOut);
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_SubscriberCodeunit_HasARowPerSubscriberMethod", readOut);
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_PublishingOnlyCodeunit_HasNoRows", readOut);
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_UnfilteredWalk_ReachesBothPublisherKinds", readOut);
            Assert.DoesNotContain("FAIL", readOut);

            var (warmExit, warmOut) = Run(cacheDir, "Codeunit70766");
            AssertNotBuilt(warmExit, warmOut, "warm run that never reads 2000000140");
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
