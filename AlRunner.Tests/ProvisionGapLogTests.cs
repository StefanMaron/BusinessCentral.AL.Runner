// ProvisionGapLogTests — the process-global half of #2587's tests.
//
// Split out of ProvisionGapSummaryTests.cs for #2913. These tests swap Console.Error (three of
// them only to silence noise, one to assert on what was written), and the console-swap guard in
// ConsoleSwapIsolationGuardTests scans per SOURCE FILE — so while these lived alongside
// ProvisionGapSummaryTests, which swaps nothing, the guard flagged that class too. Its own file
// makes the file boundary match reality, so the guard's answer is exact for both classes rather
// than being suppressed with an exemption.
//
// ProvisionGapLog is process-global state, so these must not interleave with anything else
// touching it. They are pure in-memory calls, so serialising them costs nothing.
// RecordPatchesSerialCollection also satisfies the console-swap guard, because what that guard
// requires is DisableParallelization = true and not one particular collection — which is how a
// class needing a serial collection for an unrelated reason stays fenced without having to be
// in two collections at once.
//
// This is entirely about the runner's own reporting — there is no claim about Business Central
// anywhere in this file, so nothing here belongs in the al-language corpus.
using System;
using System.IO;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ProvisionGapLogTests
{
    [Fact]
    public void Report_WritesToStderrAndRecords()
    {
        var original = Console.Error;
        var captured = new StringWriter();
        try
        {
            ProvisionGapLog.Reset();
            Console.SetError(captured);
            ProvisionGapLog.Report("a gap");
        }
        finally { Console.SetError(original); }

        // Loud FIRST, recorded SECOND. .claude/rules/loud-failures.md means the summary is an
        // addition; nothing about this may get quieter, so the stderr half is asserted too.
        Assert.Contains("a gap", captured.ToString());
        Assert.Equal(new[] { "a gap" }, ProvisionGapLog.Collected);
    }

    [Fact]
    public void Reset_ForgetsThePreviousBundlesGaps()
    {
        var original = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();
            ProvisionGapLog.Report("bundle one's missing package");
            Assert.Single(ProvisionGapLog.Collected);

            // Without this, bundle one's gap is attributed to every later bundle and every later
            // --watch cycle — the run would keep reporting a problem the current bundle does not
            // have.
            ProvisionGapLog.Reset();
            Assert.Empty(ProvisionGapLog.Collected);
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public void Report_KeepsDiscoveryOrder()
    {
        var original = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();

            // Deliberately NOT in alphabetical order. Written the obvious way — "first",
            // "second", "third" — the three strings sort into the order they were added, so the
            // assertion passes against an implementation that sorts and proves only that three
            // things came back. Measured: with sorted inputs, replacing the getter with
            // OrderBy(...) still passed.
            ProvisionGapLog.Report("zeta app resolved symbol-only");
            ProvisionGapLog.Report("alpha app has neither a DLL nor AL source");
            ProvisionGapLog.Report("mid app resolved symbol-only");

            // The fourth property of this collector, alongside "records", "reset clears" and
            // "Collected is a copy" below. It is observable end to end: PrintSummary dedupes with
            // Enumerable.Distinct, which keeps first-occurrence order, so the summary lists gaps
            // in the same order as the stderr blocks thousands of lines above it — which is what
            // lets a reader match the two up. Every other assertion in this class uses a single
            // entry, so nothing else here would notice a reordering.
            Assert.Equal(
                new[]
                {
                    "zeta app resolved symbol-only",
                    "alpha app has neither a DLL nor AL source",
                    "mid app resolved symbol-only",
                },
                ProvisionGapLog.Collected);
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public void Collected_IsACopy_SoALaterResetDoesNotEmptyWhatACallerAlreadyRead()
    {
        var original = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();
            ProvisionGapLog.Report("a gap");
            var read = ProvisionGapLog.Collected;

            ProvisionGapLog.Reset();

            // Program.cs reads Collected into the bundle's own list and the next bundle resets.
            // Handing out the live list would empty that bundle's record from under it.
            Assert.Equal(new[] { "a gap" }, read);
        }
        finally { Console.SetError(original); }
    }
}
