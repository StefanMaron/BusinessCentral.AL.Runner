// ActionNeededPointerTests — #4600.
//
// Each "Action needed" entry is at most three lines (the app and what is wrong, where, and the
// fix), and a failing test whose cause is one of those apps says "see Action needed: <app>"
// instead of repeating the remedy. --output-json and JUnit keep the full message.
//
// Runner reporting only: no claim about Business Central, so nothing here belongs in the corpus.
using System;
using System.IO;
using System.Linq;
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ActionNeededPointerTests
{
    private const string App = "Contoso/Test Lib";
    private const int DeclaredId = 139461;   // declared by App in the registrations below
    private const int OtherId = 139462;      // declared by nothing registered

    private const string Gap =
        "[dep] Contoso/Test Lib v2.0.0.0 resolved to a package with NO IMPLEMENTATION"
        + "\n      /cache/Contoso_Test Lib.app: no DLL, no AL source, so calls into it fail with \"object with ID 0\""
        + "\n      Fix: put Contoso/Test Lib v2.0.0.0 or later with AL source or an R2R DLL into one of: /cache"
        + "\n      other copies: none in the searched directories";

    private static BucketResult Bucket(TestResult t, params string[] gaps) =>
        new("/bundle", BucketStage.Ran, Array.Empty<string>(), null, new[] { t },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, gaps);

    private static TestResult Failing(Exception ex) =>
        new("Codeunit50100", "CallsTheLibrary", TestOutcome.Error, ex.Message, RunnerFrame,
            TimeSpan.FromMilliseconds(3), Exception: ex);

    private const string RunnerFrame = "   at AlRunner.NoOpCodeunit.OnInvoke(Int32 methodId, Object[] arguments)";

    private static MissingDependencyCodeunitException Attributed()
    {
        ProvisionGapLog.Reset();
        ProvisionGapLog.RegisterUnservableApp(App, () => new[] { DeclaredId });
        try { return MissingDependencyCodeunitException.For(DeclaredId, "AL called method id 7. ", BcRuntime.BuildMissingCodeunitMessageForTests(DeclaredId)); }
        finally { ProvisionGapLog.Reset(); }
    }

    private static string PerTest(BucketResult b)
    {
        var w = new StringWriter();
        Reporter.PrintPerTest(new[] { b }, w, showPass: false);
        return w.ToString();
    }

    [Fact]
    public void AnAttributedFailure_PointsAtItsActionNeededEntry_InsteadOfRepeatingTheRemedy()
    {
        var output = PerTest(Bucket(Failing(Attributed()), Gap));

        Assert.Contains($"Codeunit {DeclaredId} is in {App}, which has no implementation in this run"
            + $" — see Action needed: {App}", output);
        Assert.DoesNotContain("--package-cache", output);
        Assert.DoesNotContain("al-runner provision", output);
        Assert.DoesNotContain("NoOpCodeunit.OnInvoke", output);
    }

    /// <summary>Negative: no entry for that app, so there is nothing to point at — full text.</summary>
    [Fact]
    public void WithoutAnActionNeededEntryForTheApp_TheFullMessagePrints()
    {
        var output = PerTest(Bucket(Failing(Attributed()), Gap.Replace("Contoso/Test Lib", "Contoso/Other Lib")));

        Assert.DoesNotContain("— see Action needed:", output);
        Assert.Contains("--package-cache", output);
        Assert.Contains("NoOpCodeunit.OnInvoke", output);
    }

    [Fact]
    public void JsonAndJUnit_KeepTheFullMessage()
    {
        var b = Bucket(Failing(Attributed()), Gap);

        var json = Reporter.SerializeJsonOutput(new[] { b }, 1);
        Assert.Contains("--package-cache", json);
        Assert.Contains($"declared by {App}", json);

        var junitDir = TestScratch.Dir("action-needed-pointer-junit");
        Directory.CreateDirectory(junitDir);
        var junit = Path.Combine(junitDir, "r.xml");
        JUnitReport.WriteJUnit(junit, new[] { b });
        Assert.Contains("--package-cache", File.ReadAllText(junit));
    }

    [Fact]
    public void For_AttributesOnlyACodeunitARegisteredAppDeclares()
    {
        ProvisionGapLog.Reset();
        ProvisionGapLog.RegisterUnservableApp(App, () => new[] { DeclaredId });
        try
        {
            var hit = MissingDependencyCodeunitException.For(DeclaredId, "", "REMEDY");
            var miss = MissingDependencyCodeunitException.For(OtherId, "", "REMEDY");

            Assert.Equal(App, hit.App);
            Assert.Equal($"Codeunit {DeclaredId} is declared by {App}, which this run resolved to a package "
                + "with no implementation (see Action needed). REMEDY", hit.Message);
            Assert.Null(miss.App);
            Assert.Equal("REMEDY", miss.Message);
        }
        finally { ProvisionGapLog.Reset(); }
    }

    /// <summary>A bundle's registrations do not attribute the next bundle's failures.</summary>
    [Fact]
    public void Reset_ForgetsTheRegisteredApps()
    {
        ProvisionGapLog.Reset();
        ProvisionGapLog.RegisterUnservableApp(App, () => new[] { DeclaredId });
        Assert.Equal(App, ProvisionGapLog.UnservableAppDeclaringCodeunit(DeclaredId));
        ProvisionGapLog.Reset();
        Assert.Null(ProvisionGapLog.UnservableAppDeclaringCodeunit(DeclaredId));
    }

    /// <summary>An app whose symbols cannot be read attributes nothing, and says so.</summary>
    [Fact]
    public void AnUnreadableApp_AttributesNothing_AndSaysSo()
    {
        var original = Console.Error;
        var err = new StringWriter();
        ProvisionGapLog.Reset();
        try
        {
            Console.SetError(err);
            ProvisionGapLog.RegisterUnservableApp(App, () => throw new IOException("truncated"));
            Assert.Null(ProvisionGapLog.UnservableAppDeclaringCodeunit(DeclaredId));
        }
        finally { Console.SetError(original); ProvisionGapLog.Reset(); }
        Assert.Contains($"could not read the codeunits of {App}", err.ToString());
    }

    [Fact]
    public void ActionNeededEntry_IsTheFirstThreeLines_WithOneNoteWhenAnyWasShortened()
    {
        var w = new StringWriter();
        Reporter.PrintActionNeeded(new[] { Bucket(Failing(Attributed()), Gap) }, w);
        var lines = w.ToString().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[]
        {
            "Action needed (1):",
            "  [dep] Contoso/Test Lib v2.0.0.0 resolved to a package with NO IMPLEMENTATION",
            "        /cache/Contoso_Test Lib.app: no DLL, no AL source, so calls into it fail with \"object with ID 0\"",
            "        Fix: put Contoso/Test Lib v2.0.0.0 or later with AL source or an R2R DLL into one of: /cache",
            "  (--verbose prints each entry's full diagnosis where it is found)",
        }, lines);
    }

    /// <summary>Negative: a gap that already fits prints whole, with no note.</summary>
    [Fact]
    public void AShortGap_PrintsWhole_WithoutTheNote()
    {
        var w = new StringWriter();
        Reporter.PrintActionNeeded(new[] { Bucket(Failing(Attributed()), "one line\n  two") }, w);

        Assert.Contains("  one line\n    two", w.ToString().Replace("\r\n", "\n"));
        Assert.DoesNotContain("--verbose", w.ToString());
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("sidecar")]
    public void ProvisioningCheckGaps_PutTheFixInTheFirstThreeLines(string which)
    {
        var gap = which == "platform"
            ? ProvisioningCheck.BuildPlatformAppMissingR2RMessage("Microsoft", "System Application", "28.2.0.0",
                "/pkg/system application.app", "28.2.50931.52786")
            : ProvisioningCheck.BuildPrecompiledSidecarLoadFailedMessage("Contoso", "Widgets", "1.0.0.0",
                "/b/.deps-bin/Widgets.dll", "BadImageFormatException");

        var summary = ProvisionGapLog.SummaryLines(gap, out var shortened);

        Assert.True(shortened);
        Assert.Equal(3, summary.Count);
        Assert.Contains(which == "platform" ? "/pkg/system application.app" : "/b/.deps-bin/Widgets.dll", summary[1]);
        Assert.StartsWith("  Fix: ", summary[2]);
        if (which == "platform")
            Assert.Contains("al-runner provision --platform-apps --bc-version 28.2.0.0", summary[2]);
    }
}
