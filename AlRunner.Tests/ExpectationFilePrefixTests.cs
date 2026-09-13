// The file prefix under tests/expectations/ and each entry's Mode must agree (#3114).
// Enforced here, over the shipped directory, and deliberately NOT in LoadFromDirectory: the
// runner also loads a user's own manifest (--expectations <dir>, or ./tests/expectations by
// default), whose file names are that project's business. See docs/expectations.md#layout.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class ExpectationFilePrefixTests : IDisposable
{
    private readonly string _dir = TestScratch.FlatDir("al-runner-prefix-");

    public ExpectationFilePrefixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // One loadable entry per mode; each carries exactly the fields that mode requires.
    private static string Entry(string mode, string codeunit = "Cu", string method = "M") => mode switch
    {
        "expect-oos" =>
            $@"{{""codeunitId"":1,""CodeunitName"":""{codeunit}"",""Method"":""{method}"",""Mode"":""expect-oos"",""Reason"":""http-egress""}}",
        "expect-fail-known-gap" =>
            $@"{{""codeunitId"":1,""CodeunitName"":""{codeunit}"",""Method"":""{method}"",""Mode"":""expect-fail-known-gap"","
            + @"""Issue"":""https://github.com/StefanMaron/BusinessCentral.AL.Runner/issues/3114""}",
        "expect-divergence" =>
            $@"{{""codeunitId"":1,""CodeunitName"":""{codeunit}"",""Method"":""{method}"",""Mode"":""expect-divergence"","
            + @"""Reason"":""task-scheduler"",""Doc"":""docs/scope.md#jobs""}",
        "skip" =>
            $@"{{""codeunitId"":1,""CodeunitName"":""{codeunit}"",""Method"":""{method}"",""Mode"":""skip""}}",
        "accept-partial-company-init" =>
            $@"{{""codeunitId"":2,""CodeunitName"":""{codeunit}"",""Method"":""*"",""Mode"":""accept-partial-company-init"","
            + @"""Reason"":""ships without the dependency codeunit 2 needs""}",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private void Write(string fileName, params string[] entries)
        => File.WriteAllText(Path.Combine(_dir, fileName), "[" + string.Join(",", entries) + "]");

    [Theory]
    [InlineData("oos-http.json", "expect-oos")]
    [InlineData("known-gaps-pages.json", "expect-fail-known-gap")]
    [InlineData("divergence-session.json", "expect-divergence")]
    [InlineData("disabled-compile.json", "skip")]
    [InlineData("accept-company-init.json", "accept-partial-company-init")]
    public void PrefixAndModeAgree_NoViolation(string fileName, string mode)
    {
        Write(fileName, Entry(mode));
        Assert.Empty(ExpectationManifest.FilePrefixViolations(_dir));
    }

    /// <summary>Each prefix holding an entry of a different mode is reported, naming the file, the
    /// entry, the mode it has and the mode its file name promises.</summary>
    [Theory]
    [InlineData("oos-http.json", "expect-fail-known-gap", "expect-oos")]
    [InlineData("known-gaps-pages.json", "expect-oos", "expect-fail-known-gap")]
    [InlineData("divergence-session.json", "expect-oos", "expect-divergence")]
    [InlineData("disabled-compile.json", "expect-fail-known-gap", "skip")]
    [InlineData("accept-company-init.json", "skip", "accept-partial-company-init")]
    public void PrefixAndModeDisagree_IsReported(string fileName, string actualMode, string promisedMode)
    {
        Write(fileName, Entry(actualMode));
        var v = Assert.Single(ExpectationManifest.FilePrefixViolations(_dir));
        Assert.Contains(fileName, v);
        Assert.Contains($"Mode={actualMode}", v);
        Assert.Contains(promisedMode, v);
    }

    /// <summary>Per entry, not per file: the pr-gate python check for known-gaps files fires only
    /// when NO entry in the file matches, so one stray entry among correct ones passed it.</summary>
    [Fact]
    public void OneStrayEntryAmongAgreeingOnes_IsReportedAlone()
    {
        Write("known-gaps-pages.json",
            Entry("expect-fail-known-gap", "Good", "A"),
            Entry("expect-oos", "Stray", "B"));
        var v = Assert.Single(ExpectationManifest.FilePrefixViolations(_dir));
        Assert.Contains("Stray.B", v);
    }

    /// <summary>The third state: a name matching no known prefix is not a pass, because nothing can
    /// say which mode it should hold. With a loadable entry in it...</summary>
    [Fact]
    public void UnrecognisedPrefix_IsReported()
    {
        Write("my-gaps.json", Entry("expect-fail-known-gap"));
        var v = Assert.Single(ExpectationManifest.FilePrefixViolations(_dir));
        Assert.Contains("my-gaps.json", v);
        Assert.Contains("no recognised prefix", v);
        Assert.Contains("known-gaps-", v);
    }

    /// <summary>...and without one: an empty file under an unrecognised name still misleads.</summary>
    [Fact]
    public void UnrecognisedPrefix_OnAnEmptyFile_IsReported()
    {
        Write("gaps.json");
        Assert.Contains("gaps.json", Assert.Single(ExpectationManifest.FilePrefixViolations(_dir)));
    }

    /// <summary>A known prefix with no entries left stays legal: deleting the last entry and leaving
    /// <c>[]</c> behind must not fail a PR (#3114).</summary>
    [Fact]
    public void RecognisedPrefix_OnAnEmptyFile_IsNotAViolation()
    {
        Write("known-gaps-pages.json");
        Assert.Empty(ExpectationManifest.FilePrefixViolations(_dir));
    }

    [Fact]
    public void MissingDirectory_IsNotAViolation()
    {
        Assert.Empty(ExpectationManifest.FilePrefixViolations(Path.Combine(_dir, "absent")));
    }

    /// <summary>A mode added without a prefix would make every file of that mode unrecognised, and
    /// two prefixes for one mode would let the directory disagree with itself.</summary>
    [Fact]
    public void EveryMode_HasExactlyOnePrefix()
    {
        foreach (var mode in Enum.GetValues<ExpectationMode>())
            Assert.Single(ExpectationManifest.FilePrefixes, p => p.Mode == mode);
    }

    [Theory]
    [InlineData("oos-http.json", ExpectationMode.ExpectOos)]
    [InlineData("known-gaps-pages.json", ExpectationMode.ExpectFailKnownGap)]
    [InlineData("divergence-session.json", ExpectationMode.ExpectDivergence)]
    [InlineData("disabled-compile.json", ExpectationMode.Skip)]
    [InlineData("accept-company-init.json", ExpectationMode.AcceptPartialCompanyInit)]
    public void ModeForFileName_ReadsThePrefix(string fileName, ExpectationMode mode)
        => Assert.Equal(mode, ExpectationManifest.ModeForFileName(fileName));

    [Theory]
    [InlineData("my-gaps.json")]
    [InlineData("oos-.json")]      // a prefix with no area names nothing
    [InlineData("oos.json")]
    [InlineData("known-gaps-pages.txt")]
    public void ModeForFileName_UnrecognisedName_IsNull(string fileName)
        => Assert.Null(ExpectationManifest.ModeForFileName(fileName));

    /// <summary>The shipped directory itself. Vacuity guard: it must actually hold entries and files,
    /// or zero violations would be the answer to an empty question.</summary>
    [Fact]
    public void ShippedManifest_EveryFilePrefixAgreesWithItsEntries()
    {
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var dir = Path.Combine(repoRoot, "tests", "expectations");

        Assert.NotEmpty(Directory.GetFiles(dir, "*.json"));
        Assert.NotEmpty(ExpectationManifest.LoadFromDirectory(dir).Entries);

        var violations = ExpectationManifest.FilePrefixViolations(dir);
        Assert.True(violations.Count == 0,
            "tests/expectations file prefixes disagree with their entries:\n  " + string.Join("\n  ", violations));
    }
}
