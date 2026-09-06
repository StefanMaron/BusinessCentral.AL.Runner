// BcInternalsNullForgivingGuardTests — the repo-wide half of #3051.
//
// WHY A SOURCE GUARD AT ALL, AND WHY #2994's SWEEP COULD NOT SEE THIS
//   #2994 swept the runner for refusals that answer a default instead of naming the gap. Its
//   search shape was a `throw`. A null-forgiving `!` is not a throw — it is a COMPILER
//   ANNOTATION that emits no code — so 134 BC-internals member lookups were invisible to it,
//   and #3046 (five sites) and #3034 (56 throw sites) each cleared only their own slice.
//
//   The failure mode is specific and it INVERTS results rather than merely hiding them.
//   `t.GetProperty("X")!` on a member Microsoft has moved hands back a silent null; the
//   NullReferenceException lands at the first USE — `.PropertyType`, `.GetValue`, `.Invoke` —
//   on a line that no longer names X. MethodScopePatches.NavMethodScope_AssertError is an
//   unfiltered catch(Exception), so on any AL-entered path that NRE is swallowed and
//   `asserterror` PASSES on a read real BC performs fine. #3046 measured exactly that: all
//   five of its AssertError arms failed pre-fix with "No exception was thrown".
//
// WHAT THIS FILE ASSERTS
//   #3051 converted the 73 lookups that read BC's OWN internals to BcShape.Property/Method/
//   Field/Constructor, which raise a BcShapeGapException naming Declaring.Member. 61 sites
//   remain, and every one of them is here on purpose — they are not BC-layout reads:
//
//     RunnerOwnMember  the receiver is `typeof(<a runner type>)`. `nameof` or not, this asks
//                      the runner about the runner. Microsoft cannot move it, and a rename
//                      inside this repository is a compile-time or a same-PR question. 36.
//     Bcl              the receiver is a BCL type — object.MemberwiseClone, Guid.NewGuid,
//                      string.Empty, InvalidOperationException(string), Task.FromResult. The
//                      .NET surface does not move under a BC update. 7.
//     FrameworkTuple   `Item1` / `Item2` on a ValueTuple or Tuple<,>. The tuple is BC's, the
//                      MEMBER is the framework's, so a BC update cannot move it. 8.
//     Listed           ten sites the three rules above cannot classify structurally, each with
//                      its own reason below. 10.
//
//   So the assertion is two-directional. An unclassified site fails ("account for it or
//   convert it"), and a change in any category's count fails too — removing a site is as loud
//   as adding one, which is what keeps the classification from rotting while nobody looks.
//
// WHAT IT DELIBERATELY DOES NOT ASSERT
//   Not "no `!` anywhere". `!` on a local, a cast or a `GetType()` is ordinary C#. The scanner
//   below looks only at MEMBER LOOKUPS — GetProperty/GetMethod/GetField/GetConstructor/
//   GetNestedType immediately followed by `!` — because that is the shape whose failure is a
//   silent null rather than an exception.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcInternalsNullForgivingGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private enum Cat { RunnerOwnMember, Bcl, FrameworkTuple, Listed, Unclassified }

    // ── The ten sites the structural rules cannot classify ──────────────────────────────
    //
    // Keyed by file + receiver expression + first lookup argument, never by line number: a
    // line number goes stale on the next edit above it and would turn this guard into noise.
    private static readonly Dictionary<(string File, string Recv, string Member), string> Listed = new()
    {
        // The runner's OWN side-loaded assembly, not BC's. AlRunner.QueryJoin.dll ships beside
        // al-runner.dll and is loaded by path (so its Ncl-touching IL is not bound at startup),
        // which is why these are reflection at all. A rename here is a rename inside this
        // repository — BcShapeGapException would claim BC's layout moved, which would be false.
        { ("AlRunner/Patches/RecordPatches.QueryJoin.cs", "_tJoinExecutor", "\"IsMultiDataItem\""),
          "AlRunner.QueryJoin.JoinExecutor — the runner's own side-loaded assembly" },
        { ("AlRunner/Patches/RecordPatches.QueryJoin.cs", "_tJoinExecutor", "\"Execute\""),
          "AlRunner.QueryJoin.JoinExecutor — the runner's own side-loaded assembly" },
        { ("AlRunner/Patches/RecordPatches.QueryJoin.cs", "_tJoinContext!", "field"),
          "AlRunner.QueryJoin.JoinContext — the runner's own side-loaded assembly" },
        { ("AlRunner/Patches/RecordPatches.QueryJoin.cs", "_tJoinContext!", "f"),
          "AlRunner.QueryJoin.JoinContext — the runner's own side-loaded assembly" },

        // Closed generics over a BCL type. The receiver is a local, so `typeof(...)` cannot be
        // read off the text, but the type is the framework's and cannot move under BC.
        { ("AlRunner/Patches/CodeunitPatches.cs", "rt", "\"AsTask\""),
          "ValueTask<T>.AsTask — BCL" },
        { ("AlRunner/Patches/RecordPatches.NclMetaTableBuilder.cs", "arrType", "\"Empty\""),
          "ImmutableArray<T>.Empty — BCL" },
        { ("AlRunner/Patches/RecordPatches.NclMetaTableBuilder.cs", "kvpType", "new[] { typeof(int), _tMetaField! }"),
          "KeyValuePair<int, TMetaField>..ctor — BCL" },
        { ("AlRunner/Patches/RecordPatches.cs", "stackType", "\"Push\""),
          "Stack<T>.Push — BCL" },

        // The runner's own artifacts, not BC's.
        { ("AlRunner/Program.cs", "target", "\"OnRun\""),
          "OnRun on a codeunit THIS runner emitted — absence is an emit defect, not BC's layout" },
        { ("AlRunner/Reporter.cs", "f.GetType()", "\"classification\""),
          "a property of the runner's own result record — not a BC type" },
    };

    private static readonly Dictionary<Cat, int> Expected = new()
    {
        [Cat.RunnerOwnMember] = 36,
        [Cat.Bcl] = 7,
        [Cat.FrameworkTuple] = 8,
        [Cat.Listed] = 10,
    };

    /// <summary>Receivers of the form <c>typeof(X…)</c> that name a type in this repository.</summary>
    private static readonly string[] RunnerTypePrefixes =
        { "typeof(AlRunner.", "typeof(BcRuntime)", "typeof(RecordPatches)", "typeof(FlowFieldPatches)", "typeof(MediaPatches)" };

    /// <summary>Receivers naming a BCL type outright.</summary>
    private static readonly string[] BclReceivers =
        { "typeof(object)", "typeof(Guid)", "typeof(InvalidOperationException)", "typeof(string)",
          "typeof(System.Threading.Tasks.Task)" };

    // ── The assertions ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryRemainingNullForgivingMemberLookup_IsAccountedFor()
    {
        var unclassified = AllSites()
            .Where(s => Classify(s) == Cat.Unclassified)
            .Select(s => $"{s.File}:{s.Line}  {s.Recv}.{s.Kind}({Trim(s.Member)})!")
            .ToList();

        Assert.True(unclassified.Count == 0,
            "these BC-internals member lookups are guarded only by `!`, so a member Microsoft moves "
            + "hands back a silent null and NREs into NavMethodScope_AssertError's unfiltered "
            + "catch(Exception) — where `asserterror` PASSES on a read real BC performs fine (#3051). "
            + "Convert them to BcShape.Property/Method/Field/Constructor, or add them to Listed with "
            + $"the reason they are not a BC-layout read:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unclassified));
    }

    [Fact]
    public void TheRemainingPopulation_IsExactlyWhatWasClassified()
    {
        var actual = AllSites().GroupBy(Classify).ToDictionary(g => g.Key, g => g.Count());

        foreach (var (cat, expected) in Expected.OrderBy(e => e.Key.ToString()))
        {
            actual.TryGetValue(cat, out var got);
            Assert.True(expected == got,
                $"{cat}: expected {expected} null-forgiving lookups, found {got}. Adding one is as "
                + "much a change to the classification as removing one — update the count here and "
                + "say in the PR body which it was (#3051).");
        }

        Assert.Equal(Expected.Values.Sum(), AllSites().Count);
    }

    /// <summary>
    /// The scanner is the load-bearing part of both assertions above, so it gets its own
    /// arms: a `!=` comparison must NOT read as a null-forgiving `!` (the naive
    /// statement-level regex #3051 was filed with counted eight of those, which is most of
    /// why its headline number was 143 rather than 134), and neither a comment nor a string
    /// literal describing the shape may register as a live site.
    /// </summary>
    [Fact]
    public void Scanner_FindsALiveLookup_AndIgnoresNotEquals_Comments_AndStringLiterals()
    {
        const string src = """
            class C {
                void M() {
                    var a = t.GetProperty("Live", BindingFlags.Public)!.GetValue(x);
                    if (t.GetProperty("NotEquals") != null) return;
                    // t.GetProperty("InAComment")!
                    var s = "t.GetProperty(\"InAString\")!";
                    var b = t.GetMethod("Wrapped",
                        BindingFlags.Public)!;
                }
            }
            """;

        var found = Scan("x.cs", src).ToList();

        Assert.Equal(2, found.Count);
        Assert.Equal("\"Live\"", Trim(found[0].Member));
        Assert.Equal("GetProperty", found[0].Kind);
        Assert.Equal("\"Wrapped\"", Trim(found[1].Member));
        Assert.Equal("GetMethod", found[1].Kind);
    }

    /// <summary>
    /// The converted sites are the other half of the population and they are not otherwise
    /// pinned anywhere: without this arm, deleting every BcShape.Property call and putting the
    /// `!` back would still leave the two assertions above green, because they only count what
    /// is NOT converted.
    /// </summary>
    [Fact]
    public void TheConvertedSites_AreStillConverted()
    {
        var converted = SourceFiles()
            .Sum(f => CountConverted(File.ReadAllText(f)));

        Assert.Equal(73, converted);
    }

    private static int CountConverted(string src)
    {
        var n = 0;
        foreach (var name in new[] { "BcShape.Property(", "BcShape.Method(", "BcShape.Field(",
                                     "BcShape.Constructor(", "BcShape.NestedType(" })
        {
            for (var i = src.IndexOf(name, StringComparison.Ordinal); i >= 0;
                 i = src.IndexOf(name, i + 1, StringComparison.Ordinal))
                n++;
        }
        return n;
    }

    // ── Classification ──────────────────────────────────────────────────────────────────

    private static Cat Classify(Site s)
    {
        if (RunnerTypePrefixes.Any(p => s.Recv.StartsWith(p, StringComparison.Ordinal))) return Cat.RunnerOwnMember;
        // NclCecilRewrite.Media.cs hoists `typeof(MediaSetPatches)` into a local before six
        // consecutive lookups; the receiver text is the local, the type is still the runner's.
        if (s.Recv == "patchTypeMi") return Cat.RunnerOwnMember;
        if (BclReceivers.Contains(s.Recv)) return Cat.Bcl;
        var member = Trim(s.Member);
        if (member is "\"Item1\"" or "\"Item2\"") return Cat.FrameworkTuple;
        if (Listed.ContainsKey((s.File, s.Recv, member))) return Cat.Listed;
        return Cat.Unclassified;
    }

    // ── The scanner ─────────────────────────────────────────────────────────────────────

    private sealed record Site(string File, int Line, string Kind, string Recv, string Member);

    private static readonly string[] Kinds =
        { "GetProperty", "GetMethod", "GetField", "GetConstructor", "GetNestedType" };

    private static IReadOnlyList<Site> _all = Array.Empty<Site>();

    private static IReadOnlyList<Site> AllSites()
    {
        if (_all.Count > 0) return _all;
        var sites = new List<Site>();
        foreach (var path in SourceFiles())
        {
            var rel = Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
            sites.AddRange(Scan(rel, File.ReadAllText(path)));
        }
        _all = sites;
        return _all;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(RepoRoot, "AlRunner");
        Assert.True(Directory.Exists(root), $"AlRunner/ not found at {root} — the repo root walk is wrong.");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every <c>.GetProperty(…)!</c> / <c>.GetMethod(…)!</c> / … in <paramref name="src"/>,
    /// with the receiver expression and the first lookup argument. Comments and string
    /// literals are blanked first (so prose describing the shape — this file's own header does
    /// exactly that — is not a live read), and a <c>!=</c> is not a null-forgiving <c>!</c>.
    /// </summary>
    private static IEnumerable<Site> Scan(string file, string src)
    {
        var code = Blank(src);
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] != '.') continue;
            var kind = Kinds.FirstOrDefault(k =>
                string.CompareOrdinal(code, i + 1, k, 0, k.Length) == 0);
            if (kind == null) continue;
            var j = i + 1 + kind.Length;
            if (j < code.Length && code[j] == 's') j++;          // GetProperties / GetMethods / …
            while (j < code.Length && char.IsWhiteSpace(code[j])) j++;
            if (j >= code.Length || code[j] != '(') continue;
            var close = MatchClose(code, j);
            if (close < 0) continue;
            var k = close + 1;
            while (k < code.Length && char.IsWhiteSpace(code[k])) k++;
            if (k >= code.Length || code[k] != '!') continue;
            if (k + 1 < code.Length && code[k + 1] == '=') continue;   // `!=`

            // The argument text comes from the ORIGINAL source: Blank() erases string bodies,
            // and the member name is exactly what lives inside them.
            var args = src.Substring(j + 1, close - j - 1);
            yield return new Site(file, code.Take(i).Count(c => c == '\n') + 1, kind,
                                  StripCast(Receiver(code, src, i)), FirstArg(args, kind));
            i = close;
        }
    }

    private static string FirstArg(string args, string kind)
    {
        if (kind == "GetConstructor") return args;
        var depth = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) return args.Substring(0, i);
        }
        return args;
    }

    /// <summary>Whitespace-normalised, so a wrapped call and a one-liner key the same.</summary>
    private static string Trim(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string StripCast(string recv)
    {
        // `(NCLMetaTable)rt.GetProperty(…)!` — the cast binds to the RESULT, not the receiver.
        if (!recv.StartsWith("(", StringComparison.Ordinal)) return recv;
        var close = MatchClose(recv, 0);
        if (close < 0 || close + 1 >= recv.Length) return recv;
        var after = recv[close + 1];
        return char.IsLetter(after) || after == '_' || after == '(' ? recv.Substring(close + 1) : recv;
    }

    private static string Receiver(string code, string src, int dot)
    {
        var i = dot - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
        var end = i + 1;
        while (i >= 0)
        {
            var c = code[i];
            if (c is ')' or ']')
            {
                var open = c == ')' ? '(' : '[';
                var depth = 0;
                while (i >= 0)
                {
                    if (code[i] == c) depth++;
                    else if (code[i] == open) { depth--; if (depth == 0) break; }
                    i--;
                }
                i--;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '!' or '?') { i--; continue; }
            if (char.IsWhiteSpace(c))
            {
                var j = i;
                while (j >= 0 && char.IsWhiteSpace(code[j])) j--;
                if (j >= 0 && code[j] == '.') { i = j; continue; }
                break;
            }
            break;
        }
        return Trim(src.Substring(i + 1, end - i - 1));
    }

    private static int MatchClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>
    /// <paramref name="src"/> with comment and string-literal CONTENT replaced by spaces,
    /// same length so every offset still lines up with the original.
    /// </summary>
    private static string Blank(string src)
    {
        var b = new StringBuilder(src);
        var i = 0;
        while (i < src.Length)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') { b[i] = ' '; i++; }
                continue;
            }
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                { if (src[i] != '\n') b[i] = ' '; i++; }
                if (i < src.Length) { b[i] = ' '; b[i + 1] = ' '; i += 2; }
                continue;
            }
            if (src[i] == '"' && i >= 2 && src[i - 1] == '"' && src[i - 2] == '"')
            {
                // raw string literal `"""…"""` — blank through the closing fence
                var endFence = src.IndexOf("\"\"\"", i + 1, StringComparison.Ordinal);
                var stop = endFence < 0 ? src.Length : endFence + 3;
                for (; i < stop; i++) if (src[i] != '\n') b[i] = ' ';
                continue;
            }
            if (src[i] == '@' && i + 1 < src.Length && src[i + 1] == '"')
            {
                b[i] = ' '; i += 2;
                while (i < src.Length)
                {
                    if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"') { b[i] = ' '; b[i + 1] = ' '; i += 2; continue; }
                    if (src[i] == '"') { b[i] = ' '; i++; break; }
                    if (src[i] != '\n') b[i] = ' ';
                    i++;
                }
                continue;
            }
            if (src[i] == '"')
            {
                i++;
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\') { b[i] = ' '; i++; if (i < src.Length) { b[i] = ' '; i++; } continue; }
                    b[i] = ' '; i++;
                }
                if (i < src.Length) i++;
                continue;
            }
            if (src[i] == '\'')
            {
                i++;
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\\') { b[i] = ' '; i++; if (i < src.Length) { b[i] = ' '; i++; } continue; }
                    b[i] = ' '; i++;
                }
                if (i < src.Length) i++;
                continue;
            }
            i++;
        }
        return b.ToString();
    }
}
