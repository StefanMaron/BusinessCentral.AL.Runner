// CSharpSourceTests — #3527.
//
// Detector tests for the shared C# source reader, over SYNTHETIC inputs rather than over the
// suite's own contents. As inline scanning inside each guard, a hole stays invisible for exactly
// as long as nobody happens to write the shape it misses — which is how #4251 and #4249 shipped.
//
// Every literal-form test comes in a discriminating PAIR: the same tracked spelling as real code
// must survive, and inside the literal must not. A stripper that blanked everything would pass
// half of these, which is why the control arms are here.
using Xunit;

namespace AlRunner.Tests;

public sealed class CSharpSourceTests
{
    private const string Tracked = "Probe.Reset()";

    private static readonly string TestsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner.Tests"));
    private static readonly string RunnerDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner"));

    private static bool Sees(string source) => CSharpSource.CodeOnly(source).Contains(Tracked, StringComparison.Ordinal);

    // ── The control: the tracked spelling as real code must always be seen ──────────────

    [Fact]
    public void RealCode_IsSeen()
    {
        Assert.True(Sees("""
            class C { void M() { Probe.Reset(); } }
            """),
            "the control arm: a stripper that blanks everything passes every negative test below, "
            + "so a suite with no firing control proves nothing.");
    }

    // ── The three literal forms ────────────────────────────────────────────────────────

    [Fact]
    public void RegularStringLiteral_IsNotCode()
    {
        Assert.False(Sees("""
            class C { void M() { var s = "Probe.Reset();"; } }
            """));
    }

    [Fact]
    public void VerbatimStringLiteral_IsNotCode()
    {
        Assert.False(Sees("""
            class C { void M() { var s = @"Probe.Reset();"; } }
            """));
    }

    [Fact]
    public void RawStringLiteral_IsNotCode()
    {
        Assert.False(Sees(""""
            class C { void M() { var s = """Probe.Reset();"""; } }
            """"));
    }

    [Fact]
    public void MultiLineVerbatimLiteral_IsNotCode()
    {
        // The multiline arm of the same claim: an embedded AL or C# fixture spanning lines is
        // where this actually bites in this assembly, not the one-line form above.
        Assert.False(Sees("""
            class C { void M() { var s = @"
                Probe.Reset();
            "; } }
            """));
    }

    [Fact]
    public void AVerbatimLiteralEndingInABackslash_DoesNotSwallowTheCodeAfterIt()
    {
        // @"a\" ends at the quote: the backslash is NOT an escape in a verbatim string. A
        // regular-string rule applied here consumes the closing quote and everything after it,
        // which silently exonerates the rest of the file (the ordering trap #4254 records).
        Assert.True(Sees("""
            class C { void M() { var dir = @"C:\tmp\"; Probe.Reset(); } }
            """));
    }

    [Fact]
    public void ACharLiteralHoldingAQuote_DoesNotSwallowTheCodeAfterIt()
    {
        // ' " ' is a char literal, not the start of a string. A scanner with no char-literal
        // branch enters string mode at that quote and blanks the rest of the line — a SILENT
        // exoneration, the direction nobody notices.
        Assert.True(Sees("""
            class C { void M() { var q = '"'; Probe.Reset(); } }
            """));
    }

    [Fact]
    public void ASlashSlashInsideAStringLiteral_IsNotAComment()
    {
        Assert.True(Sees("""
            class C { void M() { var u = "https://example.invalid/x"; Probe.Reset(); } }
            """));
    }

    // ── Comments, in all four spellings, and regions the compiler does not build ────────

    [Fact]
    public void WholeLineComment_IsNotCode()
    {
        Assert.False(Sees("""
            class C { void M() {
                // Probe.Reset(); described in prose
            } }
            """));
    }

    [Fact]
    public void TrailingComment_IsNotCode()
    {
        // The hole in the whole-line-only stripper EnumMetadataRegistryCollectionGuardTests
        // carried: it dropped lines that START with //, so a trailing one read as a call site.
        Assert.False(Sees("""
            class C { void M() { Other(); // Probe.Reset() would be wrong here
            } }
            """));
    }

    [Fact]
    public void BlockComment_IsNotCode()
    {
        Assert.False(Sees("""
            class C { void M() { /* Probe.Reset(); */ } }
            """));
    }

    [Fact]
    public void DocComment_IsNotCode()
    {
        Assert.False(Sees("""
            class C {
                /// <summary>Do not call <see cref="Probe.Reset()"/> here.</summary>
                void M() { }
            }
            """));
    }

    [Fact]
    public void ADisabledIfRegion_IsNotCode()
    {
        // The compiler does not build it, so a call spelled there is not a call this assembly
        // makes. A character-level scanner has no notion of #if at all.
        Assert.False(Sees("""
            class C { void M() {
            #if AL_RUNNER_NO_SUCH_SYMBOL
                Probe.Reset();
            #endif
            } }
            """));
    }

    [Fact]
    public void AnEnabledIfRegion_IsStillCode()
    {
        // The discriminating half: "#if means skip" would pass the test above and be wrong.
        Assert.True(Sees("""
            class C { void M() {
            #if true
                Probe.Reset();
            #endif
            } }
            """));
    }

    // ── Interpolation: the hole this issue names ───────────────────────────────────────

    [Fact]
    public void AnInterpolationHole_IsCode()
    {
        // Acceptance criterion 1 of #3527. A hole really can call something, and a scanner that
        // treats an interpolated string wholly as text hides it.
        Assert.True(Sees("""
            class C { void M() { var s = $"value={Probe.Reset()}"; } }
            """));
    }

    [Fact]
    public void AnInterpolatedRawStringHole_IsCode()
    {
        Assert.True(Sees(""""
            class C { void M() { var s = $"""value={Probe.Reset()}"""; } }
            """"));
    }

    [Fact]
    public void TheTEXTPartOfAnInterpolatedString_IsNotCode()
    {
        // The discriminating half: "keep interpolated strings whole" would pass the two above
        // and re-open the literal hole for every $"..." in the assembly.
        Assert.False(Sees("""
            class C { void M() { var s = $"Probe.Reset() {x}"; } }
            """));
    }

    // ── Properties the callers depend on ───────────────────────────────────────────────

    [Fact]
    public void LineNumbersArePreserved()
    {
        // TestArtifactsGateTests attributes offenders by line, so blanking must not move them.
        var src = "class C {\n// Probe.Reset();\nvoid M() { Probe.Reset(); }\n}\n";
        var lines = CSharpSource.CodeOnly(src).Split('\n');
        Assert.Equal(src.Split('\n').Length, lines.Length);
        Assert.DoesNotContain(Tracked, lines[1], StringComparison.Ordinal);
        Assert.Contains(Tracked, lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsBlanked_KeepsLiterals()
    {
        // The opposite answer, and both are right: the artifact-path scan's whole subject is a
        // path spelled as a literal.
        var stripped = CSharpSource.CommentsBlanked("""
            class C { void M() { var p = "Probe.Reset();"; /* Other.Thing(); */ } }
            """);
        Assert.Contains(Tracked, stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Other.Thing()", stripped, StringComparison.Ordinal);
    }

    // ── The third state ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnterminatedStringLiteral_Refuses()
    {
        // A guard that could not read its input has measured nothing. Returning the partial
        // blanking it managed would let every scan over that file report zero violations --
        // its success state (guards-need-a-third-state.md). An unterminated literal is the
        // shape that makes the answer WRONG rather than merely incomplete: the boundary between
        // content and code is guesswork from there on.
        const string src = "class C { void M() { var s = \"Probe.Reset(); } }\n";
        var ex = Record.Exception(() => CSharpSource.CodeOnly(src));
        Assert.True(ex is CSharpSourceRefusedException,
            "expected a refusal, got " + (ex?.GetType().Name ?? "no exception") + ". Roslyn's "
            + "diagnostics for this source were: " + string.Join(", ",
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(src).GetDiagnostics()
                    .Select(d => d.Id + "@" + d.Location.SourceSpan)));
    }

    [Fact]
    public void AnUnterminatedBlockComment_Refuses()
    {
        var ex = Record.Exception(() => CSharpSource.CodeOnly("class C { /* Probe.Reset();\n"));
        Assert.IsType<CSharpSourceRefusedException>(ex);
    }

    [Fact]
    public void AFragmentThatIsNotAWholeCompilationUnit_IsStillRead()
    {
        // The constraint that stops the third state trading one defect for another: a genuinely
        // readable thing must stay a pass. Every detector test in this assembly feeds synthetic
        // fragments -- a member without its enclosing type, a bare statement -- for which Roslyn
        // reports CS0106/CS1513 and lexes perfectly. This pass depends on the lexing only, so a
        // whole-tree error check here would be a false RED on 25 tests that are measuring
        // something real.
        Assert.False(Sees("public void M() { var s = \"Probe.Reset();\"; }"));
        Assert.True(Sees("public void M() { Probe.Reset(); }"));
    }

    [Fact]
    public void AMissingDirectory_Refuses()
    {
        Assert.Throws<CSharpSourceRefusedException>(
            () => CSharpSource.CsFilesUnder(Path.Combine(Path.GetTempPath(), "al-runner-no-such-dir-3527")));
    }

    [Fact]
    public void ADirectoryWithNoSources_Refuses()
    {
        var dir = Directory.CreateTempSubdirectory("csharpsource3527").FullName;
        try
        {
            Assert.Throws<CSharpSourceRefusedException>(() => CSharpSource.CsFilesUnder(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EverySourceFileThisAssemblyScans_Parses()
    {
        // Coverage for the refusal above, in the direction that matters: it must not fire on the
        // real tree. Without this, a refusal nobody has exercised is indistinguishable from one
        // that would reject every file the moment a guard called it.
        var roots = new[] { TestsDir, RunnerDir };
        var scanned = 0;
        var refused = new List<string>();
        foreach (var root in roots)
        {
            foreach (var path in CSharpSource.CsFilesUnder(root))
            {
                scanned++;
                try { CSharpSource.CodeOnly(File.ReadAllText(path)); }
                catch (CSharpSourceRefusedException e) { refused.Add($"{Path.GetFileName(path)}: {e.Message}"); }
            }
        }

        Assert.True(scanned > 500,
            $"expected the two source roots to hold well over 500 .cs files; saw {scanned}. "
            + "A small number means the probe is looking at the wrong tree, and every assertion "
            + "here is then about nothing.");
        Assert.True(refused.Count == 0,
            $"{refused.Count} of {scanned} source files could not be parsed as C#: "
            + string.Join("; ", refused.Take(10)));
    }

    // ── The exemption scan of EnumMetadataRegistryCollectionGuardTests ─────────────────
    //
    // #4254 made that guard's POPULATION scan literal-aware and left its EXEMPTION scan reading
    // raw text. The two directions fail differently and only one is loud: a false offender stops
    // a PR, a false EXONERATION is silent and lets the race it guards against back in.

    [Fact]
    public void DeclaresCollection_SeesARealAttribute()
    {
        Assert.True(EnumMetadataRegistryCollectionGuardTests.DeclaresCollection("""
            [Collection(EnumMetadataRegistrySerialCollection.Name)]
            public sealed class C { }
            """));
    }

    [Fact]
    public void DeclaresCollection_IsNotExemptedByTheSpellingInAStringLiteral()
    {
        // Exactly the shape that exempted the guard from itself in #4251: the text sat in its
        // own failure message. Reading raw text, a class can exonerate itself by MENTIONING the
        // attribute it never applies.
        Assert.False(EnumMetadataRegistryCollectionGuardTests.DeclaresCollection("""
            public sealed class C {
                void M() { Fail("add [Collection(SomeSerialCollection.Name)] to this class"); }
            }
            """));
    }

    [Fact]
    public void DeclaresCollection_IsNotExemptedByTheSpellingInAComment()
    {
        Assert.False(EnumMetadataRegistryCollectionGuardTests.DeclaresCollection("""
            // TODO: give this a [Collection(...)] one day
            public sealed class C { }
            """));
    }
}
