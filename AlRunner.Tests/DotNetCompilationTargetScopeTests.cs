// DotNetCompilationTargetScopeTests — issue #2641.
//
// RUNNER-MECHANISM test, end to end. ManifestCompilationTargetTests (#2725) pins that
// app.json's `target` reaches NavCA.CompilationOptions. It does not pin that anything
// OBSERVABLE follows from it, and that is the half #2641 is about: the runner used to
// compile every bundle as CompilationTarget.OnPrem regardless of its manifest, so a
// Cloud-target app declaring a `dotnet` block compiled and ran here while a real service
// tier refuses it. Runner-green then meant nothing for that file.
//
// The rejection is NOT re-implemented here. The runner hands BC's own AL compiler the
// declared target and the compiler applies its own rule, so these diagnostics are
// Microsoft's, not the runner's — which is why this can be a runner-mechanism test rather
// than a BC-behaviour claim needing a service tier. It also could not be a corpus test even
// if we wanted one: the corpus adjudicates by COMPILING its apps and running them, so an app
// that deliberately fails to compile cannot express a passing test there.
//
// The four cells, measured on BC 28.1 before this file existed. `target` and the presence of
// a `dotnet` declaration block are independent, and each is load-bearing:
//
//   target   dotnet block   result
//   Cloud    present        error AL0296 — 'DotNet' has scope 'OnPrem', not usable for Cloud
//   Cloud    absent         error AL0185 — DotNet '<Type>' is missing
//   OnPrem   absent         error AL0185 — DotNet '<Type>' is missing
//   OnPrem   present        compiles, and the test runs and passes
//
// The last row is why the other three prove something. Without it every assertion here would
// still pass if the runner simply refused all DotNet everywhere, which is a different bug
// with the same green.
//
// Historical note, because it decides what this test is worth: at the commit #2641 reports
// (7744ab12) only the FIRST row was wrong — Cloud + a dotnet block compiled and the test
// passed. The AL0185 rows already behaved correctly there, on both targets. The AL0185
// output quoted in that issue therefore does not demonstrate the defect it is attached to;
// it is the no-declaration diagnostic, which was never broken. #2737 fixed the real cell by
// making the manifest target reach the compiler.

using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DotNetCompilationTargetScopeTests
{
    /// <summary>
    /// A `dotnet` declaration block naming one mscorlib type. Its presence is one of the two
    /// independent variables in the table above.
    /// </summary>
    private const string DotNetDeclaration = """
    dotnet
    {
        assembly("mscorlib")
        {
            type("System.Text.Encoding"; "Encoding")
            {
            }
        }
    }
    """;

    /// <summary>
    /// Declares a DotNet variable and nothing else. Declaring it is enough — the diagnostics
    /// under test are the compiler's, raised before anything runs.
    /// </summary>
    private const string ProbeCodeunit = """
    codeunit 62260 "DotNet Target Probe"
    {
        Subtype = Test;

        [Test]
        procedure DeclaresADotNetVariable()
        var
            Enc: DotNet Encoding;
        begin
            Clear(Enc);
        end;
    }
    """;

    private static string WriteBundle(string label, string target, bool withDeclaration)
    {
        var root = TestScratch.Dir("al-runner-dotnet-target-" + label);
        Directory.CreateDirectory(root);

        // No "application" property: the Base Application floor is not the subject here and
        // costs ~70 s cold per invocation (.claude/rules/no-base-app-in-csharp-tests.md).
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "DotNet Target Scope {{label}}",
          "publisher": "Repro2641",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62260, "to": 62269 } ],
          "runtime": "15.0",
          "target": "{{target}}"
        }
        """);

        if (withDeclaration)
            File.WriteAllText(Path.Combine(root, "Declaration.DotNet.al"), DotNetDeclaration);

        File.WriteAllText(Path.Combine(root, "Probe.Codeunit.al"), ProbeCodeunit);
        return root;
    }

    [Fact]
    public async Task CloudTarget_DeclaringADotNetBlock_IsRefusedByTheCompiler()
    {
        var r = await SuiteServer.RunViaServer(WriteBundle("cloud-decl", "Cloud", withDeclaration: true));

        // The cell #2641 is actually about. Before #2737 this compiled and the test PASSED.
        Assert.Contains(r.CompilationErrors, c => c.Contains("error AL0296", StringComparison.Ordinal));
        Assert.Contains(r.CompilationErrors, c => c.Contains("cannot be used for 'Cloud' development", StringComparison.Ordinal));

        // It must not have run: a compile-refused bundle that still reports a passing test
        // would mean the diagnostic was raised and then ignored.
        AssertNoTestOfTheCodeunitPassed(r);
    }

    [Fact]
    public async Task OnPremTarget_DeclaringADotNetBlock_CompilesAndRuns()
    {
        var r = await SuiteServer.RunViaServer(WriteBundle("onprem-decl", "OnPrem", withDeclaration: true));

        // The positive control. Without it, a runner that refused DotNet unconditionally
        // would satisfy every other assertion in this file.
        r.AssertPassed("Codeunit62260.DeclaresADotNetVariable");
        Assert.DoesNotContain(r.CompilationErrors, c => c.Contains("error AL0296", StringComparison.Ordinal));
        Assert.DoesNotContain(r.CompilationErrors, c => c.Contains("error AL0185", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Cloud")]
    [InlineData("OnPrem")]
    public async Task EitherTarget_WithoutADotNetBlock_IsRefusedAsMissing(string target)
    {
        var r = await SuiteServer.RunViaServer(WriteBundle("no-decl-" + target, target, withDeclaration: false));

        // A DotNet variable with no declaration is missing on BOTH targets, which is why the
        // AL0185 output quoted in #2641 does not demonstrate a target defect. Asserting the
        // type name too, so this cannot pass on some unrelated AL0185.
        Assert.Contains(r.CompilationErrors,
            c => c.Contains("error AL0185: DotNet 'Encoding' is missing", StringComparison.Ordinal));
        AssertNoTestOfTheCodeunitPassed(r);
    }

    /// <summary>The CLI's "no <c>PASS  Codeunit62260</c> line", read from the protocol.</summary>
    private static void AssertNoTestOfTheCodeunitPassed(ServerRunResult r)
        => Assert.DoesNotContain(r.Tests, t => t.Status == "pass" && t.Name.StartsWith("Codeunit62260.", StringComparison.Ordinal));
}
