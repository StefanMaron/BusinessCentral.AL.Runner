// SourceFilesAreSearchableGuardTests — issue #3179.
//
// A raw NUL byte in a .cs file makes it read as BINARY to file(1) and to ripgrep, and ripgrep
// then SKIPS it. Not "reports it differently" — skips it, silently, so a search across the tree
// comes back with no match rather than with an error. That is the exact false-negative family
// CLAUDE.md documents for `grep -E` (rejects the flag, exits 0, prints nothing) and for `rg`
// without --hidden (skips dot-directories): a failure wearing the costume of a result.
//
// It was not hypothetical here. AlRunner/Patches/ApplicationObjectBasePatches.cs carried one, in
//
//     if (!_oosTrapReported.TryAdd($"{oos.Api}<NUL>{oos.Reason}", 0)) return;
//
// where a literal NUL had been typed instead of the escape \0. The byte is a perfectly
// reasonable separator and the code was correct — but that file also holds
// IsPermanentOutOfScope, the method deciding whether an AL [TryFunction] may swallow a runner
// refusal. So `rg "oos-in-try"` and `rg "IsPermanentOutOfScope"` both returned NOTHING while the
// logic sat right there, and the only way to find it was `grep -r`, which reported
// "binary file matches" and no line. Writing it as \0 keeps the identical key and the identical
// behaviour while leaving the file searchable.
//
// WHY A GUARD RATHER THAN JUST THE FIX
// ------------------------------------
// The failure is invisible by construction. Nothing about a silently-skipped file appears in a
// diff, a build log, or a test run — the next literal NUL would restore the blind spot and no
// one would learn of it until a search came back empty and was believed. A structural fix needs
// a structural test (the same reasoning BracelessIfSecondStatementGuardTests sets out), so this
// asserts the property directly against the tree.
//
// THE DETECTOR IS HELD HONEST TOO
// -------------------------------
// A scanner that quietly stops matching passes forever and guards nothing. So the detector is
// tested in both directions before it is trusted: fed content that must trip it, and content
// that must not — including the escape sequence \0 spelled as source text, which is the whole
// point of the fix and must never be flagged.
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlRunner.Tests;

public class SourceFilesAreSearchableGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// The property ripgrep actually applies: a NUL byte anywhere in the file makes it binary.
    /// Deliberately not "is it valid UTF-8" — a file can be valid UTF-8 and still be skipped,
    /// which is exactly what happened, so testing the encoding would have missed it.
    /// </summary>
    private static bool ReadsAsBinaryToRipgrep(byte[] content) => Array.IndexOf(content, (byte)0) >= 0;

    // ── Negative: the detector must fire on the shape, and only on it ──

    [Fact]
    public void Detector_FiresOnALiteralNul_AndNotOnTheEscapeSequence()
    {
        // The bug: a real NUL byte in the file.
        var withLiteralNul = "var k = $\"{a}\0{b}\";"u8.ToArray();
        Assert.True(ReadsAsBinaryToRipgrep(withLiteralNul),
            "a literal NUL byte must be detected — that is the whole failure mode");

        // The fix: the same key written as a two-character escape. Must NOT be flagged, otherwise
        // the guard would forbid its own remedy.
        var withEscape = "var k = $\"{a}\\0{b}\";"u8.ToArray();
        Assert.False(ReadsAsBinaryToRipgrep(withEscape),
            "the \\0 escape is ordinary source text and must never be flagged");

        // Near misses that must stay clean: other control characters and non-ASCII text do not
        // make ripgrep skip a file, so flagging them would be a false positive.
        Assert.False(ReadsAsBinaryToRipgrep("tab\there\r\nnewline"u8.ToArray()));
        Assert.False(ReadsAsBinaryToRipgrep("em dash — and \"smart quotes\""u8.ToArray()));
    }

    // ── Positive: the tree itself is clean ──

    [Fact]
    public void CSharpSources_AreAllSearchable_NoneReadAsBinary()
    {
        var offenders = SourceFiles()
            .Where(f => ReadsAsBinaryToRipgrep(File.ReadAllBytes(f)))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these .cs files contain a literal NUL byte, so ripgrep SKIPS them silently and a "
            + "search across the tree returns a false negative rather than an error. Write the "
            + "byte as the escape \\0 instead — same value, searchable file:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The one site this issue fixed, pinned by name. The sweep above would catch a regression
    /// anywhere; this says out loud which file taught us the lesson, so a reader who reintroduces
    /// it here gets the history rather than a generic complaint.
    /// </summary>
    [Fact]
    public void TheOutOfScopeClassifier_IsSearchable()
    {
        var path = Path.Combine(RepoRoot, "AlRunner", "Patches", "ApplicationObjectBasePatches.cs");
        Assert.True(File.Exists(path), $"expected the out-of-scope classifier at {path}");

        var bytes = File.ReadAllBytes(path);
        Assert.False(ReadsAsBinaryToRipgrep(bytes),
            "ApplicationObjectBasePatches.cs holds IsPermanentOutOfScope, which decides whether "
            + "an AL [TryFunction] may swallow a runner refusal. A NUL byte here makes every "
            + "ripgrep search of that logic come back empty — see this file's header.");

        // Guard against a fix that removed the byte by removing the dedup key with it.
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("_oosTrapReported.TryAdd", text, StringComparison.Ordinal);
    }

    private static System.Collections.Generic.List<string> SourceFiles()
    {
        var files = new[] { "AlRunner", "AlRunner.Tests" }
            .Select(d => Path.Combine(RepoRoot, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // If the walk finds nothing, the guard is vacuous — say so instead of passing.
        Assert.True(files.Count > 50,
            $"expected the source tree to hold many .cs files, found {files.Count}");
        return files;
    }
}
