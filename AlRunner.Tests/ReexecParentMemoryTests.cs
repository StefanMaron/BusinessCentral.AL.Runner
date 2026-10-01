using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4947: a re-exec parent that did the cold Cecil rewrite of Ncl.dll must not keep that
/// heap resident while it waits for its child. Measured through the phase log's
/// <c>wait_rss_bytes</c>, the parent's RSS at the moment it starts waiting.
/// </summary>
public sealed class ReexecParentMemoryTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // Without the release the rewrite parent waits at about its peak (537 MB measured on
    // Linux); with it, at about 95 MB. The bound sits between the two.
    private const long WaitRssBound = 250L * 1024 * 1024;

    private readonly string _root = TestScratch.Dir("reexec-parent-memory");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [SkippableFact]
    public void ColdRewriteParent_ReleasesItsHeap_BeforeWaitingForTheChild()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "app.json"), """
        { "id": "4c1e9a7b-2d35-4f60-8b19-4947aa000001", "name": "Reexec Parent Memory", "publisher": "AL Runner",
          "version": "1.0.0.0", "dependencies": [], "idRanges": [ { "from": 62170, "to": 62179 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(bundle, "Tests.Codeunit.al"), """
        codeunit 62170 "Reexec Parent Memory Tests"
        {
            Subtype = Test;
            [Test]
            procedure Sum()
            begin
                if 3 + 4 <> 7 then Error('sum');
            end;
        }
        """);
        var log = Path.Combine(_root, "phases.jsonl");
        // A cache root of its own, so the ncl-cecil cache MISSes and this invocation does the
        // rewrite whose heap the assertion is about.
        var cache = Path.Combine(_root, "cache");

        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        args.Append($" --verbose --cache \"{cache}\" \"{bundle}\"");

        var psi = new ProcessStartInfo("dotnet", args.ToString())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_PHASE_LOG"] = log;
        SharedEngineCaches.Isolate(psi);
        var output = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}. Output:\n{output}");
        Assert.Contains("Cecil cache MISS", output.ToString());

        var parents = File.ReadAllLines(log)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("kind").GetString() == "process-reexec-parent")
            .ToList();
        Assert.True(parents.Count >= 1, $"no re-exec parent row in the phase log. Output:\n{output}");

        // Precondition: some parent really grew past the bound, or the bound proves nothing.
        Assert.True(parents.Max(r => r.GetProperty("peak_rss_bytes").GetInt64()) > WaitRssBound,
            "no re-exec parent peaked above the bound, so this run cannot tell a released heap "
            + $"from one that was never large:\n{string.Join("\n", parents)}");
        Assert.All(parents, r =>
        {
            var waitRss = r.GetProperty("wait_rss_bytes").GetInt64();
            Assert.True(waitRss > 0, $"wait_rss_bytes was never recorded: {r}");
            Assert.True(waitRss < WaitRssBound,
                $"a re-exec parent waited with {waitRss / (1024 * 1024)} MB resident: {r}");
        });
    }
}
