// FloorOnlyBundleEnumFieldTests — issue #3594's regression arm.
//
// A bundle whose app.json declares an `application` FLOOR and `dependencies: []` must still
// run. It resolves the stripped platform packages, which carry NO enum symbols at all, so
// AlEnumMetadataRegistry never learns the enums of the app that declares them — measured on
// Fixtures/BcFloorSkip/healthy-suite: 721 enums registered and Base Application's 8889 absent,
// against zero misses on the corpus, which names Base Application as an explicit dependency.
//
// #3594 made the runner state a field's enumTypeId, which is what makes BC resolve it. Stated
// unconditionally, that turned every floor-only bundle into the exact 0-of-N abort #3594 exists
// to remove, on a different manifest shape:
//
//   EXEC-FAIL: ... GetFieldRecordBuffer threw for table 1366 field:
//   NavMetadataNotFoundException: The metadata object Enum 8889 was not found.
//
// It reached all three BC legs before this test existed. The fix withholds the id where it
// cannot be backed (BcRuntime.CanResolveEnumMetadata), which is faithful rather than a fake:
// with no enum id BC builds the plain NCLOptionMetadataWithCaptions from the field's own inline
// option string, exactly as every such bundle saw before #3594.
//
// This is a RUNNER-MECHANISM test. It spawns the runner, because the defect is only observable
// end to end: the registry contents depend on which packages the bundle resolved, which is a
// property of the whole load, not of any one call.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class FloorOnlyBundleEnumFieldTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string Output, int Exit) RunRunner(string relativeFixture)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", relativeFixture)}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The regression itself, on the fixture that carried it. Asserts the DISTINGUISHING
    /// substrings, not merely that the run was green: an abort for some other reason, or a run
    /// that skipped the bundle and covered nothing, must not pass this.
    /// </summary>
    [SkippableTheory]
    [InlineData("BcFloorSkip/healthy-suite")]
    [InlineData("CrossMajorNote")]
    [InlineData("SubscriberScanAudit")]
    public void FloorOnlyBundle_RunsWithoutAnEnumMetadataAbort(string fixture)
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(fixture);

        // The exact failure #3594 introduced here, named so a DIFFERENT abort cannot pass by
        // being green-adjacent.
        Assert.DoesNotContain("was not found", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFieldRecordBuffer threw", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EXEC-FAIL", output, StringComparison.Ordinal);

        // ...and the bundle really ran, rather than being skipped into a vacuous green. Every
        // one of these fixtures declares exactly one test.
        Assert.Contains("1P/0F/0E", output, StringComparison.Ordinal);
        Assert.True(exit == 0, $"a floor-only bundle must run green. exit={exit}\n{output}");
    }

    /// <summary>
    /// The reader half, in both directions. #3594's first attempt withheld an id the registry
    /// did not know, which masked the real defect: <c>TryParseEnumSymbol</c> dropped every enum
    /// declaring no <c>Values</c> array, so an enum an app genuinely ships was simply missing.
    /// Measured on System Application 28.1 — 3 of its 141 enums are written that way, and
    /// 8889 "Email Connector" is one of them.
    ///
    /// <para>Both directions matter and are asserted here: a valueless enum must survive the
    /// parse WITH its id and name, and an enum WITH values must still carry them. A reader that
    /// returned an empty symbol for everything would satisfy the first alone.</para>
    /// </summary>
    [Fact]
    public void EnumDeclaringNoValues_SurvivesTheSymbolParse_AndOneWithValuesKeepsThem()
    {
        // A valueless extensible enum, exactly the shape BC ships for 8889.
        var valueless = System.Text.Json.JsonDocument.Parse("""
            { "Id": 8889, "Name": "Email Connector",
              "Properties": [ { "Name": "Extensible", "Value": "1" } ] }
            """).RootElement;

        var parsedValueless = AlRunner.Patches.BcAppSymbolCache.ParseEnumSymbolForTest(valueless);

        Assert.NotNull(parsedValueless);
        Assert.Equal(8889, parsedValueless!.Id);
        Assert.Equal("Email Connector", parsedValueless.Name);
        Assert.Empty(parsedValueless.Options);
        Assert.Empty(parsedValueless.Indexes);

        // ...and the ordinary shape is unaffected, so "accept valueless" did not become
        // "return an empty symbol for everything".
        var withValues = System.Text.Json.JsonDocument.Parse("""
            { "Id": 8888, "Name": "Email Status", "Values": [
                { "Name": "Draft", "Ordinal": 0 },
                { "Name": "Queued", "Ordinal": 5 } ] }
            """).RootElement;

        var parsedWithValues = AlRunner.Patches.BcAppSymbolCache.ParseEnumSymbolForTest(withValues);

        Assert.NotNull(parsedWithValues);
        Assert.Equal(new[] { "Draft", "Queued" }, parsedWithValues!.Options);
        Assert.Equal(new[] { 0, 5 }, parsedWithValues.Indexes);
    }

    /// <summary>
    /// #3805 — a value stating no <c>Ordinal</c> has ordinal ZERO, not "the previous ordinal
    /// plus one". The symbol file omits the property exactly when the value is 0, the same
    /// absent-means-default convention <c>PermissionSymbol</c> records for <c>PermissionObject</c>.
    ///
    /// <para>The source-order rule agrees with that on 680 of the 681 absent-Ordinal values
    /// shipped in 28.1, which is why it survived; it disagrees on System Application enum 2616
    /// "Printer Paper Kind", whose 68 values BC emits in NAME order with <c>Custom</c> last and
    /// stating no <c>Ordinal</c>. Its 67 declared ordinals mirror
    /// <c>System.Drawing.Printing.PaperKind</c> exactly, in which <c>Custom</c> is 0, and 0 is the
    /// one ordinal no declared value claims. Measured on two independent binaries, 28.1 and 28.4.
    /// Derivation: the PR body for #3805.</para>
    /// </summary>
    [Fact]
    public void AbsentOrdinal_MeansZero_NotThePreviousOrdinalPlusOne()
    {
        // The shape of System Application 2616, reduced to the part that discriminates: values
        // NOT in ordinal order, and the absent Ordinal at the END rather than at index 0.
        var nameOrdered = System.Text.Json.JsonDocument.Parse("""
            { "Id": 2616, "Name": "Printer Paper Kind", "Values": [
                { "Name": "A3", "Ordinal": 8 },
                { "Name": "A4", "Ordinal": 9 },
                { "Name": "GermanStandardFanfold", "Ordinal": 40 },
                { "Name": "Custom" } ] }
            """).RootElement;

        var parsed = AlRunner.Patches.BcAppSymbolCache.ParseEnumSymbolForTest(nameOrdered);

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "A3", "A4", "GermanStandardFanfold", "Custom" }, parsed!.Options);

        // The whole claim: Custom is 0. The source-order rule answers 41 here.
        Assert.Equal(new[] { 8, 9, 40, 0 }, parsed.Indexes);

        // ...and the consequence that makes the wrong answer more than a wrong number: an AL
        // enum's ordinals are distinct, so a rule producing a collision is wrong by construction.
        Assert.Equal(parsed.Indexes.Count, parsed.Indexes.Distinct().Count());

        // The NEGATIVE direction, which is what stops "read 0 for everything" passing: a value
        // that DOES state an ordinal keeps it, including a 0 stated explicitly, and a later
        // absent one does not inherit its neighbour.
        var mixed = System.Text.Json.JsonDocument.Parse("""
            { "Id": 60000, "Name": "Mixed", "Values": [
                { "Name": "Explicit", "Ordinal": 0 },
                { "Name": "Seven", "Ordinal": 7 },
                { "Name": "AlsoZero" } ] }
            """).RootElement;

        var parsedMixed = AlRunner.Patches.BcAppSymbolCache.ParseEnumSymbolForTest(mixed);

        Assert.NotNull(parsedMixed);
        Assert.Equal(new[] { 0, 7, 0 }, parsedMixed!.Indexes);
    }
}
