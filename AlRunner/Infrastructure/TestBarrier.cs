// TestBarrier — test-only synchronization hook for --server's runTests
// streaming loop. NOT part of the production protocol; see #1845.
//
// #1785/#1798 tried to make ServerCancelTests.RunTests_CancelDuringRun_*
// deterministic by widening (then live-calibrating) a CPU-bound spin-loop
// margin so the cancel side-channel round trip had "enough" wall-clock time
// to land between two [Test] methods. Both attempts regressed under CI
// contention because the margin was still a wall-clock guess: the calibration
// and the destructive run are two separate phases, and a noisy shared runner
// can shift load between them.
//
// This makes the in-flight window a property of the RUN's own state instead
// of the wall clock: HandleServerRunTests' OnTestComplete hook calls
// WaitForRelease() after emitting each `{"type":"test"}` line. With
// AL_RUNNER_TEST_BARRIER_DIR unset (every real deployment, and every OTHER
// test's server process) this is a single null-check, no-op. Only the ONE
// CliServer subprocess ServerCancelTests.RunTests_CancelDuringRun_* itself
// launches has the env var set (via CliServer.StartAsync's extraEnv
// parameter — set on that child process's ProcessStartInfo, never on the
// current process, so it cannot leak into any other test's server), and only
// that subprocess ever blocks here — the harness releases it once, right
// after observing the cancel's ack, by dropping a "release" file. That turns
// "cancel arrives before the next test starts" into a guarantee instead of a
// probability: the server literally cannot proceed to the next test until
// the harness says so.
//
// Deliberately NOT routed through the stdin/stdout JSON protocol under test:
// using the very channel a test asserts things about to also pace that test's
// workload would prove nothing about the channel. Filesystem polling is a
// wholly separate signal path.
using System;
using System.IO;
using System.Threading;

namespace AlRunner.Infrastructure;

public static class TestBarrier
{
    private static readonly string? BarrierDir =
        Environment.GetEnvironmentVariable("AL_RUNNER_TEST_BARRIER_DIR");

    /// <summary>
    /// No-op unless AL_RUNNER_TEST_BARRIER_DIR is set on THIS process (see the
    /// class doc comment for why that's safe). When set, blocks — polling every
    /// 5ms, not spinning — until a file named "release" appears in that
    /// directory, then deletes it so a later call starts from a clean slate.
    /// Bounded at 60s: a harness bug that forgets to drop the release file must
    /// fail loud with a clear message, not hang the test run (and CI) forever.
    /// </summary>
    public static void WaitForRelease()
    {
        if (BarrierDir == null) return;

        var releaseFile = Path.Combine(BarrierDir, "release");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(releaseFile))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"TestBarrier.WaitForRelease: no release file at '{releaseFile}' within 60s " +
                    "— the test harness should have created it right after observing the event " +
                    "it was waiting for (see AlRunner.Tests.ServerCancelTests).");
            Thread.Sleep(5);
        }
        File.Delete(releaseFile);
    }
}
