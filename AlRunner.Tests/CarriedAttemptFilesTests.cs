// CarriedAttemptFilesTests — a promised attempt that is not here must not pass quietly (#2747).
//
// The distinction this class exists to draw, and every test below is about which side of it a
// given file falls on:
//
//   * a file NEVER NAMED — nothing was promised, nothing is missing. Every run that did not
//     resume is in this case, and must be completely unaffected.
//   * a file NAMED and usable — the ordinary resume. Also unaffected.
//   * a file NAMED and not usable — a promised attempt vanished. Whatever the run reports omits
//     it, so the run may not present itself as complete.
//
// The third case used to be silent: JUnitReport.LoadCarriedSuites skipped it and JUnitCounts.Read
// returned zeros, so the child finished, found nothing, and reported a SMALLER run with exit 0.
// The carry directory is owned by the PARENT attempt, which waits while the child runs, so
// killing the parent alone loses the file for the child's entire run — either because SIGTERM
// ran its ProcessExit and deleted the directory, or because SIGKILL left an owner that is dead
// and the next runner start swept it correctly.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class CarriedAttemptFilesTests : IDisposable
{
    private readonly string _dir = TestScratch.Dir("al-runner-carried-attempt-tests");

    public CarriedAttemptFilesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => ScratchDirs.Release(_dir);

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private string GoodJUnit(string name = "good.xml") => Write(name,
        """<testsuites><testsuite name="s" tests="1"><testcase classname="C" name="T"/></testsuite></testsuites>""");

    private string GoodResults(string name = "good.json")
    {
        var p = Path.Combine(_dir, name);
        ResumeCarry.Write(p, new[]
        {
            new BucketResult("/b", BucketStage.Ran, Array.Empty<string>(), null,
                new[] { new TestResult("C", "T", TestOutcome.Pass, null, null, TimeSpan.Zero) },
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
        });
        return p;
    }

    [Fact]
    public void NoFilesNamed_IsClean_SoANonResumedRunIsUntouched()
    {
        // The case every ordinary run is in. If this ever reported a loss, every run on the
        // machine would start failing.
        Assert.Empty(CarriedAttemptFiles.Audit(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void UsableFiles_AreClean()
    {
        Assert.Empty(CarriedAttemptFiles.Audit(new[] { GoodJUnit() }, new[] { GoodResults() }));
    }

    [Fact]
    public void AMissingJUnit_IsReported_NotSkipped()
    {
        // The defect verbatim: this is what the child sees after the parent's scratch directory
        // went away, and it used to contribute nothing in silence.
        var missing = Path.Combine(_dir, "gone.xml");

        var bad = CarriedAttemptFiles.Audit(new[] { missing }, Array.Empty<string>());

        var one = Assert.Single(bad);
        Assert.Equal(missing, one.Path);
        Assert.Contains("gone", one.Reason);
    }

    [Fact]
    public void AMissingResultsSidecar_IsReported_Too()
    {
        // Both channels or neither: a report is complete or it is not, and #2719's sidecar can
        // vanish exactly the same way the JUnit can — same directory, same owner, same kill.
        var missing = Path.Combine(_dir, "gone.json");

        var one = Assert.Single(CarriedAttemptFiles.Audit(Array.Empty<string>(), new[] { missing }));

        Assert.Equal(missing, one.Path);
        Assert.Contains("gone", one.Reason);
    }

    [Fact]
    public void ATruncatedJUnit_IsReported()
    {
        // What a kill mid-write leaves: well-formed enough to load, no suites in it. Reported,
        // because an attempt only ever writes a carry file when it HAS results — "no suites"
        // cannot be an honest attempt.
        var truncated = Write("truncated.xml", "<testsuites></testsuites>");

        var one = Assert.Single(CarriedAttemptFiles.Audit(new[] { truncated }, Array.Empty<string>()));

        Assert.Contains("no testsuite", one.Reason);
    }

    [Fact]
    public void ACorruptJUnit_IsReported()
    {
        var corrupt = Write("corrupt.xml", "<testsuites><not closed");

        var one = Assert.Single(CarriedAttemptFiles.Audit(new[] { corrupt }, Array.Empty<string>()));

        Assert.Contains("will not parse", one.Reason);
    }

    [Fact]
    public void ACorruptResultsSidecar_IsReported()
    {
        var corrupt = Write("corrupt.json", "{ not json");

        var one = Assert.Single(CarriedAttemptFiles.Audit(Array.Empty<string>(), new[] { corrupt }));

        Assert.Contains("will not parse", one.Reason);
    }

    [Fact]
    public void EveryLostFileIsNamed_NotJustCounted()
    {
        // Which attempt was lost is the first thing anyone asks, so the message lists each path
        // with its own reason rather than reporting a count.
        var missingXml = Path.Combine(_dir, "a.xml");
        var corruptJson = Write("b.json", "nope");

        var bad = CarriedAttemptFiles.Audit(new[] { missingXml, GoodJUnit() }, new[] { corruptJson });
        var text = CarriedAttemptFiles.Describe(bad);

        Assert.Equal(2, bad.Count);
        Assert.Contains(missingXml, text);
        Assert.Contains(corruptJson, text);
        Assert.DoesNotContain(GoodJUnit("good.xml"), text.Replace(missingXml, "").Replace(corruptJson, ""));
        Assert.Contains("#2747", text);
        // The run's own results are still valid; it is the TOTAL that cannot be trusted. Saying
        // so stops the next reader discarding a run that mostly worked.
        Assert.Contains("still printed above", text);
    }

    [Fact]
    public void AnEmptyPathEntry_IsNotAPromise()
    {
        // Program.cs can carry an empty string when writing a carry file failed outright; that
        // is not a vanished attempt, it is an attempt that never wrote one.
        Assert.Empty(CarriedAttemptFiles.Audit(new[] { "" }, new[] { "" }));
    }
}
