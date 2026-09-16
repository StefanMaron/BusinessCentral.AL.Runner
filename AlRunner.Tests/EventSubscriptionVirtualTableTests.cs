// EventSubscriptionVirtualTableTests — issue #4198.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it drives the runner
// over a fixture bundle and proves the Event Subscription virtual table (2000000140) is
// actually POPULATED, and populated discriminatingly, by our own seeding of BC's registry.
// What real BC answers for the table is asserted upstream, where a service tier adjudicates it
// — corpus codeunit 60955, StefanMaron/BusinessCentral.AL.Language.Tests#374.
//
// WHY THIS REPLACED A SOURCE-TEXT TEST, AND WHAT THAT COST
//   The first version of this file was five `File.ReadAllText` + `Assert.Contains("<literal>")`
//   checks over the fix's own source. They passed, and two mutations reddened them — but both
//   mutations were literal-string DELETIONS, which is the one class a grep-based test cannot
//   miss. The test and the mutation were reading the same source text, so they agreed with
//   each other rather than measuring anything.
//
//   Measured, on review: wrapping the seeding call in `if (false)` — the whole fix dead, and
//   2000000140 answering an empty store exactly as before #4198 — left that suite GREEN 5/5.
//   So did disabling the dispatch branch, and so did neutering both halves of the reload reset
//   while leaving their text in place. Every grepped string survives all three.
//
//   The lesson worth keeping: a mutation that reds the right subset still proves nothing when
//   the test and the mutation read the same bytes. These tests read the runner's OUTPUT.
//
// WHAT MAKES A DISABLED FIX RED HERE
//   The fixture asserts row counts and column values that are only obtainable from a populated
//   registry, and pairs each positive arm with a negative one an empty table does NOT satisfy.
//   With the seeding disabled every count arm reads 0 where it expects 1 or 2, and the runner
//   exits non-zero.
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class EventSubscriptionVirtualTableTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "EventSubscriptionVirtualTable");

    private static (int ExitCode, string StdOut, string StdErr) Run(string cacheDir)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        sb.Append(' ').Append($"\"{FixtureDir}\"");
        sb.Append(' ').Append($"--cache \"{cacheDir}\"");

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

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 180s.");
        }
        // WaitForExit(int) does not drain the async output callbacks; only the parameterless
        // overload does. Without this the last stdout lines can still be in flight.
        proc.WaitForExit();
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    /// <summary>
    /// The whole fixture, in one runner invocation.
    ///
    /// <para>Named arms rather than a bare exit-code check, so a change that guts the table
    /// while leaving the bundle runnable cannot pass: the runner exits 0 on a bundle whose
    /// tests all pass, and the specific claims below are what say WHICH tests passed.</para>
    /// </summary>
    [Fact]
    public void EventSubscription_IsPopulatedAndDiscriminates()
    {
        var cacheDir = TestScratch.Dir("al-runner-esv-tests");
        try
        {
            var (exit, stdout, stderr) = Run(cacheDir);

            Assert.True(exit == 0,
                $"expected a clean run (every fixture test must pass). exit={exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The table is populated at all, and per subscriber METHOD rather than per codeunit.
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_SubscriberCodeunit_HasARowPerSubscriberMethod", stdout);
            // ...and its partner: a codeunit in the same bundle that only PUBLISHES has none,
            // so a provider ignoring its filters fails one of the two.
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_PublishingOnlyCodeunit_HasNoRows", stdout);

            // The publisher-side filter, both directions, over two tables of the same shape.
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_WatchedTable_HasARowNamingItAsPublisher", stdout);
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_UnsubscribedTable_HasNoRows", stdout);

            // (Subscriber Codeunit ID, Subscriber Function) really keys the table: two methods
            // of one codeunit fetch different rows.
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_Get_ByPrimaryKey_ReturnsThatMethodsRow", stdout);
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_Get_UndeclaredSubscriberFunction_Fails", stdout);

            // The row describes the subscription — real method names, real publisher type.
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_TableEventRow_CarriesTheRealMethodNames", stdout);

            // The arm that fails if only the convenient registry is seeded: the two rows were
            // registered through different runner registries (bare MethodInfo vs full handle).
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_UnfilteredWalk_ReachesBothPublisherKinds", stdout);

            Assert.DoesNotContain("FAIL", stdout);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The reload path keeps BC's registry and the runner's seeding record in step.
    ///
    /// <para>Two runs against ONE cache directory. The second is served from the AL-output
    /// cache, so it re-enters the injection cycle over an already-seeded process state — which
    /// is where a seeding record that is not reset alongside the registry shows up, as rows
    /// that double or vanish. Both runs assert the SAME exact counts, so either failure mode
    /// reds. This is also `local-test-scope.md`'s warm-cache rule: there is a cache between
    /// this change and its observable, so the proving test runs twice.</para>
    /// </summary>
    [Fact]
    public void EventSubscription_WarmSecondRunAgainstOneCache_AnswersTheSameRows()
    {
        var cacheDir = TestScratch.Dir("al-runner-esv-warm");
        try
        {
            var (coldExit, coldOut, coldErr) = Run(cacheDir);
            Assert.True(coldExit == 0,
                $"cold run must pass. exit={coldExit}\nstdout:\n{coldOut}\nstderr:\n{coldErr}");

            var (warmExit, warmOut, warmErr) = Run(cacheDir);
            Assert.True(warmExit == 0,
                $"warm run must pass too — a stale seeding record doubles or drops rows. "
                + $"exit={warmExit}\nstdout:\n{warmOut}\nstderr:\n{warmErr}");

            // The count arms are the ones a drift in either direction breaks, so name them
            // explicitly on the warm run rather than resting on the exit code.
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_SubscriberCodeunit_HasARowPerSubscriberMethod", warmOut);
            Assert.Contains(
                "PASS  Codeunit70765.EventSubscription_WatchedTable_HasARowNamingItAsPublisher", warmOut);
            Assert.DoesNotContain("FAIL", warmOut);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
