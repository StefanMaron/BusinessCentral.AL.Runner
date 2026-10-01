// TextSplitVariadicSeparatorsTests — #3712: Text.Split(Sep1, Sep2, ...) failed the dependency
// compile with CS1501 "No overload for method 'ALSplit' takes 3 arguments".
//
// BC's compiler emits Text.Split(Sep1, Sep2, ...) as ALSplit(text, a, b, ...) — one argument per
// separator, never an array — and BC's own NavTextExtensions.ALSplit(string, params string[])
// accepts that. BcAssembler redirects every ALSplit to AlRunnerShim.NavRuntimeHelpersShim.ALSplit
// (so it can add the NavList<char> shapes the emitted C# uses), and the shim's string[] overload
// was not `params`. One separator compiled; two or more did not, and the whole module was dropped.
//
// This class proves the COMPILE PAIRING only: the shim exposes the overload shapes the emitted C#
// calls, for literal and variable receivers and separators. What Split(a, b) ANSWERS is BC
// behaviour and is adjudicated upstream — corpus codeunit 60119, TextSplit_TwoSeparatorLiterals_*
// and siblings (corpus PR linked from the runner PR). Per bc-behavior-tests-go-upstream.md the AL
// here therefore asserts nothing about the parts; the fixture's tests are named *_Compiles.

using Xunit;

namespace AlRunner.Tests;

public sealed class TextSplitVariadicSeparatorsTests : IDisposable
{
    private readonly string _root;

    public TextSplitVariadicSeparatorsTests()
    {
        _root = TestScratch.Dir("al-runner-text-split-variadic");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteBundle(string dir)
    {
        Directory.CreateDirectory(dir);
        // No "application" property — see .claude/rules/no-base-app-in-csharp-tests.md.
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "5a3f0b11-3712-4a01-8001-000000003712",
          "name": "TSVS Split Shapes",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63400, "to": 63419 } ],
          "runtime": "14.0"
        }
        """);
        // The variadic shapes #3712 is about — literal receiver + literal separators, variable
        // receiver + variable separators, literal receiver + variable separators, mixed, two and
        // three separators, multi-character separators — plus the two arities that bound before
        // (one separator) and that a second params overload would make ambiguous (none). Each
        // procedure only has to compile and run to completion.
        File.WriteAllText(Path.Combine(dir, "SplitShapes.Codeunit.al"), """
        codeunit 63400 "TSVS Split Shapes"
        {
            Subtype = Test;

            [Test]
            procedure TwoLiteralSeparators_Compiles()
            var
                Parts: List of [Text];
            begin
                Parts := 'a,b;c'.Split(',', ';');
            end;

            [Test]
            procedure NoSeparator_Compiles()
            var
                Parts: List of [Text];
            begin
                Parts := 'a b c'.Split();
            end;

            [Test]
            procedure LiteralReceiver_VariableSeparators_Compiles()
            var
                Sep1: Text;
                Sep2: Text;
                Parts: List of [Text];
            begin
                Sep1 := ',';
                Sep2 := ';';
                Parts := 'a,b;c'.Split(Sep1, Sep2);
            end;

            [Test]
            procedure ThreeLiteralSeparators_Compiles()
            var
                Parts: List of [Text];
            begin
                Parts := 'a,b;c|d'.Split(',', ';', '|');
            end;

            [Test]
            procedure TwoVariableSeparators_OnAVariable_Compiles()
            var
                Input: Text;
                Sep1: Text;
                Sep2: Text;
                Parts: List of [Text];
            begin
                Input := 'a--b::c';
                Sep1 := '--';
                Sep2 := '::';
                Parts := Input.Split(Sep1, Sep2);
            end;

            [Test]
            procedure MixedLiteralAndVariableSeparators_Compiles()
            var
                Input: Text;
                Sep: Text;
                Parts: List of [Text];
            begin
                Input := 'a,b;c';
                Sep := ';';
                Parts := Input.Split(',', Sep);
            end;

            [Test]
            procedure OneSeparator_StillCompiles()
            var
                Parts: List of [Text];
            begin
                Parts := 'a,b,c'.Split(',');
            end;
        }
        """);
    }

    /// <summary>
    /// RED before the fix: <c>COMPILE-FAIL ... error CS1501: No overload for method 'ALSplit'
    /// takes 3 arguments</c> on every multi-separator call, exit 3, <c>Tests: 0 total</c>. After:
    /// all five procedures compile, run, and the run exits 0.
    /// </summary>
    [SkippableFact]
    public async Task SplitWithSeveralSeparators_CompilesAndRuns()
    {
        TestArtifacts.SkipIfMissing();
        WriteBundle(_root);

        var result = await SuiteServer.RunViaServer(_root);

        result.AssertOutputDoesNotContain("CS1501");
        // CS0121 is what a second params overload beside (string, params string[]) produces for
        // the zero-separator Split(): two expanded-form candidates, nothing to prefer.
        result.AssertOutputDoesNotContain("CS0121");
        result.AssertOutputDoesNotContain("COMPILE-FAIL");
        Assert.Equal(7, result.Total);
        foreach (var name in new[]
        {
            "TwoLiteralSeparators_Compiles", "NoSeparator_Compiles",
            "TwoVariableSeparators_OnAVariable_Compiles", "LiteralReceiver_VariableSeparators_Compiles",
        })
        {
            result.AssertPassed($"Codeunit63400.{name}");
        }
        Assert.Equal(0, result.ExitCode);
    }
}
