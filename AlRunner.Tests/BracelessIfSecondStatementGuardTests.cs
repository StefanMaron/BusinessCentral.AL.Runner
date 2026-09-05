// BracelessIfSecondStatementGuardTests — issue #2843.
//
// The shape this guards against, as it stood in Program.cs at the TEST-TIMEOUT-ABORT append:
//
//     if (executor.AbortReasons.Count > 0)
//         bundleErrors.AddRange(executor.AbortReasons.Select(r => $"{rel}: TEST-TIMEOUT-ABORT: {r}"));
//         allAbortReasons.AddRange(executor.AbortReasons);
//
// The `if` has no braces, so it guards only the first statement. The second one — indented as
// though it were guarded, and added by a later commit (#2698) that clearly meant it to be —
// runs unconditionally.
//
// WHAT THAT SECOND STATEMENT ACTUALLY DID, MEASURED RATHER THAN ASSUMED
// ---------------------------------------------------------------------
// Nothing. This is the part worth writing down, because "brace-less if" reads like a live bug
// and here it is not one:
//
//   * The guard is `AbortReasons.Count > 0`, so the ONLY state in which the second statement
//     runs unguarded is `Count == 0`.
//   * `TestExecutor.AbortReasons` is `Array.Empty<string>()` in that state — it is reset to
//     exactly that at the top of every `Run()` call (TestExecutor.cs, the `#2415: this instance
//     is reused across Run() calls` line) and only ever grows via `RecordAbortedSuite`.
//   * `List<string>.AddRange` special-cases an `ICollection<T>` argument on its count, so
//     `allAbortReasons.AddRange(Array.Empty<string>())` is a no-op — it does not even grow the
//     list's capacity.
//
// So the unguarded call is provably equivalent to not making it, and there is no runtime
// observation that can tell the two apart. A test asserting "allAbortReasons stayed empty"
// would pass identically before and after the fix, which is the noise `.claude/rules/tdd.md`
// forbids. The honest proving test for a fix whose only effect is structural is a structural
// one — this file.
//
// WHY IT IS STILL WORTH FIXING AND GUARDING
// -----------------------------------------
// The indentation promises something the code does not do. The next person to add a third
// statement under that `if` gets it wrong in a way that reads as correct — `goto fail`, and
// there the third statement was not a no-op. Braces remove the trap; this test keeps them.
//
// THE DETECTOR HAS TO BE HELD HONEST TOO
// --------------------------------------
// A scanner that quietly stops matching passes forever and guards nothing — the silent-default
// shape from `.claude/rules/loud-failures.md` wearing test clothing. So the detector is tested
// in BOTH directions: `Detector_FiresOnTheShape_AndOnlyOnIt` feeds it the exact #2843 snippet
// plus every near-miss that must NOT trip it, and only then does
// `AlRunnerSources_HaveNoBracelessIfWhoseNextStatementIsIndentedAsIfGuarded` claim the tree is
// clean.
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class BracelessIfSecondStatementGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>One flagged occurrence: the `if`/loop header and the statement that only LOOKS guarded.</summary>
    internal readonly record struct Occurrence(string File, int HeaderLine, string Header, int OrphanLine, string Orphan);

    /// <summary>
    /// Drop `//` line comments, leaving string and char literals intact, so a trailing comment
    /// cannot change whether a line "ends with a semicolon". Block comments are deliberately not
    /// handled: they do not occur in this position anywhere in the tree, and a half-implemented
    /// block-comment stripper would be a worse lie than none. A `/* */` in this position would
    /// simply fail to match and under-report, never over-report.
    /// </summary>
    internal static string StripLineComment(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool inString = false, inChar = false, inVerbatim = false, escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inVerbatim)
            {
                // In @"..." a doubled quote is an escaped quote; a single one ends the literal.
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append(c).Append(line[++i]); continue; }
                    inVerbatim = false;
                }
                sb.Append(c);
                continue;
            }
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                sb.Append(c);
                continue;
            }
            if (inChar)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '\'') inChar = false;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"') { inVerbatim = true; sb.Append(c).Append(line[++i]); continue; }
            if (c == '$' && i + 2 < line.Length && line[i + 1] == '@' && line[i + 2] == '"')
            { inVerbatim = true; sb.Append(c).Append(line[++i]).Append(line[++i]); continue; }
            if (c == '"') inString = true;
            else if (c == '\'') inChar = true;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    private static bool ParensBalanced(string s)
    {
        int depth = 0;
        foreach (var c in s) { if (c == '(') depth++; else if (c == ')') depth--; }
        return depth == 0;
    }

    private static int Indent(string s)
    {
        int n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    private static readonly System.Text.RegularExpressions.Regex HeaderPattern = new(
        @"^(?<ind>[ ]*)(\}[ ]*)?(else[ ]+)?(if|for|foreach|while)[ ]*\(.*\)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Report every place where a brace-less `if`/loop header is followed by a single-line
    /// statement and then a SECOND statement at the same indentation — the statement that looks
    /// guarded and is not.
    ///
    /// Deliberately conservative in every ambiguous case, so a hit is always real:
    /// the header's condition must close on its own line (balanced parens), the guarded
    /// statement must be one complete line ending in `;`, and the following line must be a
    /// statement at exactly that indentation (not `}`, `{`, `else`, `catch`, `finally`). Under
    /// -reporting a contrived shape is acceptable; a false positive that blocks an unrelated PR
    /// is not.
    /// </summary>
    internal static IReadOnlyList<Occurrence> Scan(string file, string text)
    {
        var raw = text.Replace("\r\n", "\n").Split('\n');
        var code = new string[raw.Length];
        for (int i = 0; i < raw.Length; i++) code[i] = StripLineComment(raw[i]);

        var found = new List<Occurrence>();
        for (int i = 0; i < code.Length; i++)
        {
            var m = HeaderPattern.Match(code[i]);
            if (!m.Success || !ParensBalanced(code[i])) continue;
            int headerIndent = m.Groups["ind"].Value.Length;

            int j = i + 1;
            while (j < code.Length && code[j].Trim().Length == 0) j++;
            if (j >= code.Length) continue;
            var body = code[j];
            if (body.TrimStart().StartsWith("{", StringComparison.Ordinal)) continue; // braced — fine
            int bodyIndent = Indent(body);
            if (bodyIndent <= headerIndent) continue;
            if (!body.EndsWith(";", StringComparison.Ordinal)) continue;               // multi-line statement — skip

            int k = j + 1;
            while (k < code.Length && code[k].Trim().Length == 0) k++;
            if (k >= code.Length) continue;
            if (Indent(code[k]) != bodyIndent) continue;
            var next = code[k].Trim();
            if (next.Length == 0) continue;
            if (next.StartsWith("}", StringComparison.Ordinal) || next.StartsWith("{", StringComparison.Ordinal)
                || next.StartsWith("else", StringComparison.Ordinal) || next.StartsWith("catch", StringComparison.Ordinal)
                || next.StartsWith("finally", StringComparison.Ordinal))
                continue;

            found.Add(new Occurrence(file, i + 1, raw[i].Trim(), k + 1, raw[k].Trim()));
        }
        return found;
    }

    /// <summary>
    /// The detector's own RED→GREEN. The positive sample is the #2843 code verbatim; every
    /// negative is a shape that must NOT be reported, including the two legitimate ways to write
    /// the same intent (braces, or the second statement dedented to where it belongs).
    /// </summary>
    [Fact]
    public void Detector_FiresOnTheShape_AndOnlyOnIt()
    {
        const string theDefect = """
            void M()
            {
                if (executor.AbortReasons.Count > 0)
                    bundleErrors.AddRange(executor.AbortReasons.Select(r => $"x: {r}"));
                    allAbortReasons.AddRange(executor.AbortReasons);
            }
            """;
        var hit = Assert.Single(Scan("sample.cs", theDefect));
        Assert.Equal(3, hit.HeaderLine);
        Assert.Equal(5, hit.OrphanLine);
        Assert.Contains("allAbortReasons.AddRange", hit.Orphan, StringComparison.Ordinal);

        // Fix A — braces. This is what the production fix does.
        Assert.Empty(Scan("a.cs", """
            void M()
            {
                if (executor.AbortReasons.Count > 0)
                {
                    bundleErrors.AddRange(x);
                    allAbortReasons.AddRange(y);
                }
            }
            """));

        // Fix B — the second statement dedented to the level it actually executes at.
        Assert.Empty(Scan("b.cs", """
            void M()
            {
                if (executor.AbortReasons.Count > 0)
                    bundleErrors.AddRange(x);
                allAbortReasons.AddRange(y);
            }
            """));

        // A brace-less if with exactly one statement is idiomatic here and must stay allowed.
        Assert.Empty(Scan("c.cs", """
            void M()
            {
                if (a > 0)
                    Use(a);
                Other();
            }
            """));

        // A multi-line condition: the header does not close on its own line, so the "body" line
        // is really the rest of the condition. Skipped rather than guessed at.
        Assert.Empty(Scan("d.cs", """
            void M()
            {
                if (a > 0
                    && b > 0)
                    Use(a);
            }
            """));

        // if/else — `else` after the guarded statement is not an unguarded sibling.
        Assert.Empty(Scan("e.cs", """
            void M()
            {
                if (a > 0)
                    Use(a);
                else
                    Use(b);
            }
            """));

        // A trailing comment must not stop the body line from counting as a complete statement,
        // and a comment line between the two statements must not hide the defect either.
        var withComments = Scan("f.cs", """
            void M()
            {
                if (a > 0)
                    First(a);  // trailing comment
                    Second(a);
            }
            """);
        Assert.Single(withComments);

        // A `//`-commented-out second statement is not code and must not be reported.
        Assert.Empty(Scan("g.cs", """
            void M()
            {
                if (a > 0)
                    First(a);
                    // Second(a);
            }
            """));
    }

    /// <summary>
    /// #2843's actual claim: no `AlRunner/` source contains a brace-less `if`/loop whose next
    /// statement is indented as though it were guarded.
    ///
    /// <para>RED on `main` before this fix: two occurrences, `Program.cs:3376` (bundled-mode run
    /// loop) and `Program.cs:3504` (the `--isolation`-split per-suite loop). The issue reported
    /// only the second and asked whether the first shared the pattern — it did.</para>
    /// </summary>
    [Fact]
    public void AlRunnerSources_HaveNoBracelessIfWhoseNextStatementIsIndentedAsIfGuarded()
    {
        var runnerDir = Path.Combine(RepoRoot, "AlRunner");
        Assert.True(Directory.Exists(runnerDir), $"AlRunner source directory not found at {runnerDir}");

        var files = Directory.EnumerateFiles(runnerDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // If the walk finds nothing, the guard is vacuous — say so instead of passing.
        Assert.True(files.Count > 50, $"expected the AlRunner tree to hold many .cs files, found {files.Count}");

        var hits = new List<Occurrence>();
        foreach (var f in files)
            hits.AddRange(Scan(Path.GetRelativePath(RepoRoot, f), File.ReadAllText(f)));

        Assert.True(hits.Count == 0,
            "brace-less `if`/loop whose NEXT statement is indented as though it were guarded (#2843) — "
            + "the indentation promises a guard the code does not apply, and the next statement added "
            + "under it will be wrong in a way that reads as correct. Add braces:\n"
            + string.Join("\n", hits.Select(h =>
                $"  {h.File}:{h.HeaderLine}  {h.Header}\n      unguarded -> {h.File}:{h.OrphanLine}  {h.Orphan}")));
    }
}
