// OutOfScopePointerCallSiteGuardTests — no throw site may produce a doubled docs/scope.md.
//
// WHAT THIS ADDS OVER RunnerOutOfScopeMessagePointerTests (#3073)
// ---------------------------------------------------------------
// That file pins the NORMALISER: given these inputs, TrimTrailingDocPointer produces these
// outputs. It proves the normaliser works on inputs someone thought to write down. It cannot
// notice a twenty-fifth throw site written in a form nobody anticipated — which is the actual
// failure #2766 was about, and the reason a per-site audit was proposed there in the first
// place. This file pins the CALL SITES: whatever they write, the message that reaches a
// developer names docs/scope.md once.
//
// WHY THIS READS IL AND NOT SOURCE
// ---------------------------------
// Three reasons, in order of how much they cost when ignored:
//
//   1. IL HAS NO COMMENTS. A raw-text scan of AlRunner/ finds 43 mentions of docs/scope.md, of
//      which only 24 are live reason strings; the rest are comments, --guide text, the
//      mechanism itself, and Cecil-injected literals. #3064 is the standing example of what
//      happens when a guard cannot tell those apart — the Base App floor guard matched a
//      COMMENT documenting compliance and failed a PR that contained no violation. A guard
//      that has to special-case comments will drift out of step with them.
//      ScratchDirOwnershipGuardTests carries the same warning in its own header ("Comment
//      lines are not counted"), which is a heuristic this shape does not need.
//
//   2. IL SEES EVERY SPELLING. The same refusal is written as `new RunnerOutOfScopeException`,
//      `new AlRunner.Infrastructure.RunnerOutOfScopeException`, and target-typed `new(...)`
//      inside a factory method — and all three compile to one `newobj`. A source scan written
//      for this exact task during #2766 measurement missed the second and third spellings and
//      reported 7 sites where there were 24. That is not a hypothetical: it is why the count
//      in #2766's title was wrong for weeks. A guard that under-reports is worse than none,
//      because it certifies the thing it did not look at.
//
//   3. IT MEASURES WHAT SHIPS. The literals scanned here are the ones in the assembly a user
//      runs, after the compiler has folded adjacent constants.
//
// WHAT COUNTS AS A VIOLATION
// --------------------------
// A literal is a CANDIDATE when a human reading it would say it ends with a pointer sentence:
// strip trailing punctuation, brackets and quotes, and what remains ends "see docs/scope.md".
// The guard peels more liberally than the normaliser ON PURPOSE — that gap is the whole
// mechanism. A candidate the normaliser does not strip is the defect, because BuildMessage
// then appends its own link and the reader sees the file named twice.
//
// Two shapes are deliberately NOT candidates, and both are load-bearing rather than
// convenient:
//
//   * An ANCHORED pointer ("docs/scope.md#email") carries a section the appended bare link
//     does not, so it survives by design — RunnerOutOfScopeMessagePointerTests requires it.
//     It is excluded here by construction, not by an exception: peeling leaves "…#email",
//     which does not end "see docs/scope.md".
//   * A pointer MID-SENTENCE is prose about the file, not a citation of it.
//
// Separately, a literal that carries the whole `out-of-scope: <api> — <reason> — see …`
// convention by itself — the Cecil-injected throw sites and HelperShims, which cannot
// construct the typed exception (#1743) and so never reach the normaliser at all — is checked
// against the same end state from the other direction: it must name the file exactly once,
// since nothing will be appended to it.
using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class OutOfScopePointerCallSiteGuardTests
{
    private const string Pointer = "docs/scope.md";
    private const string Convention = "out-of-scope: ";

    /// <summary>
    /// Types that DEFINE the convention rather than cite it. Their literals are the mechanism
    /// — the bare file name, the prefix — and are not throw-site reasons.
    /// </summary>
    private static bool IsMechanism(TypeDefinition t)
    {
        var n = t.FullName;
        return n.StartsWith("AlRunner.Infrastructure.RunnerOutOfScopeException", StringComparison.Ordinal)
            || n.StartsWith("AlRunner.Infrastructure.OutOfScopeMessage", StringComparison.Ordinal)
            || n.StartsWith("AlRunner.Infrastructure.RunnerScope", StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Would a human read this literal as ending in a pointer sentence? Deliberately more
    /// permissive than <see cref="RunnerOutOfScopeException.TrimTrailingDocPointer"/>: every
    /// trailing non-alphanumeric character is peeled, so any wrapping punctuation an author
    /// invents is still recognised here even when the normaliser does not handle it yet.
    /// </summary>
    private static bool EndsWithAPointerSentence(string s)
    {
        var t = s.TrimEnd();
        int end = t.Length;
        while (end > 0 && !char.IsLetterOrDigit(t[end - 1])) end--;
        return t[..end].EndsWith("see " + Pointer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoThrowSiteWritesAPointerTheNormaliserCannotStrip()
    {
        var path = typeof(RunnerOutOfScopeException).Assembly.Location;
        using var asm = AssemblyDefinition.ReadAssembly(path);

        var offenders = new List<string>();
        var candidates = 0;
        var mentions = 0;

        foreach (var type in asm.MainModule.GetTypes())
        {
            if (IsMechanism(type)) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr || ins.Operand is not string s) continue;
                    if (!s.Contains(Pointer, StringComparison.Ordinal)) continue;
                    mentions++;

                    var where = $"{type.FullName}.{method.Name}";

                    // A whole hand-built message: nothing gets appended to it, so it must
                    // already name the file exactly once.
                    if (s.Contains(Convention, StringComparison.Ordinal))
                    {
                        var n = Occurrences(s, Pointer);
                        if (n != 1)
                            offenders.Add(
                                $"{where}\n    names {Pointer} {n} times in one hand-built "
                                + $"'{Convention}' message, which nothing normalises\n    literal: {s}");
                        continue;
                    }

                    if (!EndsWithAPointerSentence(s)) continue;
                    candidates++;

                    if (RunnerOutOfScopeException.TrimTrailingDocPointer(s) == s)
                        offenders.Add(
                            $"{where}\n    ends with a {Pointer} pointer that "
                            + $"TrimTrailingDocPointer does not strip, so BuildMessage's appended "
                            + $"link names the file twice\n    literal: {s}");
                }
            }
        }

        // A guard that scans nothing passes for the wrong reason (#3022). These two floors say
        // the walk actually reached the refusal surface; they are lower bounds, not counts, so
        // deleting a refusal does not fail this test spuriously.
        Assert.True(mentions >= 20,
            $"expected the IL walk to find at least 20 {Pointer} literals in {path}, found "
            + $"{mentions} — the scan is not reaching the refusal sites and this guard is "
            + "certifying nothing");
        Assert.True(candidates >= 15,
            $"expected at least 15 trailing-pointer reason literals, found {candidates} — see "
            + "the comment above; a collapse here means the classifier stopped recognising the "
            + "shape it exists to police");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} out-of-scope throw site(s) produce a message naming {Pointer} "
            + "more than once.\n\n"
            + "Write the reason WITHOUT a trailing 'see docs/scope.md' — "
            + "RunnerOutOfScopeException appends the canonical link itself. An anchored pointer "
            + "('docs/scope.md#email') is fine and survives on purpose. See issue #3073 and "
            + "AlRunner.Tests/RunnerOutOfScopeMessagePointerTests.cs.\n\n"
            + string.Join("\n\n", offenders));
    }
}
