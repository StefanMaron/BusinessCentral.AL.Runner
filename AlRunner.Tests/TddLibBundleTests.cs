// --tdd across a test library (#5243, #5161): `al-runner --tdd app lib lib2 test`, where a procedure of
// the library bundles calls a member the app does not declare. The test bundle's own compile never
// sees that call, because the libraries are dependencies of it. Runner-specific (--tdd turning a
// compile error into a generated stub the tests run against), so it lives here, not in the
// al-language corpus: the claim is about the generator, not about BC.
//
// One run per fixture, shared by the tests of the class (TddRunResult).
using System.Security.Cryptography;
using Xunit;

namespace AlRunner.Tests;

/// <summary>app, lib, lib2 and test: a member two libraries reach.</summary>
public sealed class TddLibBundleRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibBundle");

    public static string AppDir => Path.Combine(Root, "app");

    /// <summary>The app folder's files as they were before the run: taken here, in the constructor, so
    /// no test reads it after the run has started.</summary>
    public string AppHashBefore { get; } = TddLibBundleTests.HashDir(AppDir);

    public TddLibBundleRun() : base(
        AppDir, Path.Combine(Root, "lib"), Path.Combine(Root, "lib2"), Path.Combine(Root, "test")) { }
}

/// <summary>
/// The same four bundles listed test first. The libraries are then compiled only as dependencies of the
/// test bundle, and their own bundle iterations reuse those modules.
/// </summary>
public sealed class TddLibBundleReversedRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibBundle");

    public TddLibBundleReversedRun() : base(
        Path.Combine(Root, "test"), Path.Combine(Root, "lib2"), Path.Combine(Root, "lib"), Path.Combine(Root, "app")) { }
}

/// <summary>app, lib and test: the library calls a member --tdd refuses to generate.</summary>
public sealed class TddLibBundleRefusedRun : TddRunResult
{
    private static readonly string Root = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner.Tests", "Fixtures", "TddLibBundleRefused");

    public TddLibBundleRefusedRun() : base(
        Path.Combine(Root, "app"), Path.Combine(Root, "lib"), Path.Combine(Root, "test")) { }
}

public sealed class TddLibBundleTests
    : IClassFixture<TddLibBundleRun>, IClassFixture<TddLibBundleReversedRun>, IClassFixture<TddLibBundleRefusedRun>
{
    private readonly TddLibBundleRun _run;
    private readonly TddLibBundleReversedRun _reversed;
    private readonly TddLibBundleRefusedRun _refused;

    public TddLibBundleTests(TddLibBundleRun run, TddLibBundleReversedRun reversed, TddLibBundleRefusedRun refused)
    {
        _run = run;
        _reversed = reversed;
        _refused = refused;
    }

    private const string Stub = "Lib Bundle Loyalty: procedure \"Missing\"(Arg1: Integer): Integer";

    internal static string HashDir(string dir) => string.Join("\n",
        Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetFileName(f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));

    /// <summary>
    /// #5243: the missing member sits in the app, the call that names it in a library between the app
    /// and its tests. It is generated into the app in memory, the libraries and the tests compile, and
    /// the run neither aborts (the library's only object used to leave nothing to emit: EMIT-ZERO and
    /// FATAL) nor writes anything to the app's folder.
    /// </summary>
    [SkippableFact]
    public void MemberCalledFromALibraryBundle_IsGeneratedIntoTheAppAndTheTestsRun()
    {
        TestArtifacts.SkipIfMissing();
        Assert.True(_run.Exit == 0, $"exit {_run.Exit}\n{_run.StdErr}");
        Assert.DoesNotContain("EMIT-ZERO", _run.StdErr);
        Assert.DoesNotContain("FATAL", _run.StdErr);
        Assert.Equal(3, _run.Tests.Count);
        Assert.All(_run.Tests, t => Assert.Equal("pass", t.GetProperty("status").GetString()));
        Assert.Contains("--tdd: generated 1 member(s) this run:", _run.StdErr);
        Assert.Contains($"  {Stub}", _run.StdErr);
        Assert.Equal(_run.AppHashBefore, HashDir(TddLibBundleRun.AppDir));
        // #5265, the control: a chain the re-run bound covers says nothing about the bound.
        Assert.DoesNotContain("re-run limit", _run.StdErr);
    }

    /// <summary>
    /// #5161, the another-bundle path: a test reaching the stub only through a library names it, whatever
    /// the number of libraries between them, and a test that calls only a procedure of the same library
    /// that reaches nothing missing does not. Without the second half every test would be annotated.
    /// </summary>
    [SkippableFact]
    public void TestReachingTheStubThroughLibraryBundles_IsAnnotatedAndAnotherIsNot()
    {
        TestArtifacts.SkipIfMissing();

        Assert.True(_run.Exit == 0, $"exit {_run.Exit}\n{_run.StdErr}");
        Assert.Equal(new[] { Stub }, _run.StubsOf("ViaLibrary_RunsAgainstTheGeneratedStub"));
        Assert.Equal(new[] { Stub }, _run.StubsOf("ViaTwoLibraries_RunsAgainstTheGeneratedStub"));
        Assert.Empty(_run.StubsOf("LibraryProcedureThatReachesNothingMissing_IsNotAnnotated"));
        Assert.Contains("--tdd: 2 test(s) reach generated stubs this run:", _run.StdErr);
    }

    /// <summary>
    /// Listed test first, the libraries are compiled only as dependencies of the test bundle, where the
    /// member is asked for. It is still generated once, listed once, and the tests still name it: no
    /// bundle iteration compiles those modules, so none of them reports the member.
    /// </summary>
    [SkippableFact]
    public void BundlesListedTestFirst_StillListAndAnnotateTheMember()
    {
        TestArtifacts.SkipIfMissing();

        Assert.True(_reversed.Exit == 0, $"exit {_reversed.Exit}\n{_reversed.StdErr}");
        Assert.Equal(3, _reversed.Tests.Count);
        Assert.Contains("--tdd: generated 1 member(s) this run:", _reversed.StdErr);
        Assert.DoesNotContain("no members were generated", _reversed.StdErr);
        Assert.Equal(new[] { Stub }, _reversed.StubsOf("ViaLibrary_RunsAgainstTheGeneratedStub"));
        Assert.Equal(new[] { Stub }, _reversed.StubsOf("ViaTwoLibraries_RunsAgainstTheGeneratedStub"));
        Assert.Empty(_reversed.StubsOf("LibraryProcedureThatReachesNothingMissing_IsNotAnnotated"));
    }

    /// <summary>
    /// The other side of the rule, and the control for the two tests above: a member --tdd refuses to
    /// generate (a Text argument fixes no length) leaves the library with nothing to emit. That is
    /// reported with its cause and the run goes on: the test that reaches the library fails where it
    /// does, the test that does not still passes, and nothing claims a symbol was reported that was not.
    /// </summary>
    [SkippableFact]
    public void MemberRefusedInAOneObjectLibrary_IsReportedAndTheRunGoesOn()
    {
        TestArtifacts.SkipIfMissing();

        Assert.True(_refused.Exit == 1, $"exit {_refused.Exit}\n{_refused.StdErr}");
        Assert.DoesNotContain("FATAL", _refused.StdErr);
        Assert.DoesNotContain("EMIT-ZERO", _refused.StdErr);
        Assert.Equal(3, _refused.Tests.Count);

        Assert.Equal("fail", _refused.Find("ViaLibrary_FailsBecauseTheLibraryCannotCompile").GetProperty("status").GetString());
        Assert.Contains("Codeunit 65480", _refused.Failure("ViaLibrary_FailsBecauseTheLibraryCannotCompile"));
        // #5266: the failure names the dropped object and its AL error, and does not send the reader
        // to provisioning: the codeunit was compiled and dropped, not missing from a package.
        var failure = _refused.Failure("ViaLibrary_FailsBecauseTheLibraryCannotCompile");
        Assert.Contains("\"Lib Refused Helper\"", failure);
        Assert.Contains("did not compile", failure);
        Assert.Contains("MissingByName", failure);
        Assert.DoesNotContain("provision", failure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--package-cache", failure);
        Assert.Equal("pass", _refused.Find("NotTouchingTheLibrary_StillRuns").GetProperty("status").GetString());

        // The cause is named on the line that reports the dropped library: its object and the AL
        // diagnostic that identified it.
        var report = _refused.StdErr.Split('\n').Single(l => l.Contains(": EMIT-EXCLUDED — "));
        Assert.Contains("LibRefusedHelper", report);
        Assert.Contains("MissingByName", report);

        Assert.Empty(_refused.StubsOf("ViaLibrary_FailsBecauseTheLibraryCannotCompile"));
        Assert.DoesNotContain("--tdd: generated", _refused.StdErr);
        Assert.DoesNotContain("no test referenced a missing symbol", _refused.StdErr);
        Assert.Contains("--tdd: no members were generated this run — 1 object(s) could not be compiled", _refused.StdErr);

        // A test object of the bundle was dropped as well and is reported FAILED: the closing line says so
        // too, instead of mentioning only the library object (it declares no test of its own).
        Assert.Equal("fail", _refused.Find("OwnRefusedCall_IsReportedFailed").GetProperty("status").GetString());
        Assert.Contains("MissingOwn", _refused.Failure("OwnRefusedCall_IsReportedFailed"));
        Assert.Contains("1 [Test] procedure(s) of other dropped objects are reported FAILED above.", _refused.StdErr);
    }
}
