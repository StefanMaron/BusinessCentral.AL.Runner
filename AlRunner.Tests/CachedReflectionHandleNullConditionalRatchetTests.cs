// CachedReflectionHandleNullConditionalRatchetTests — the ratchet for #3702.
//
// THE SHAPE
//   private static PropertyInfo? _pSomething;              // resolved once, cached
//   _pSomething = t.GetProperty("X", Flags);               // the lookup: silent on a miss
//   var v = _pSomething?.GetValue(target);                 // the use, elsewhere: null reads as an answer
//
//   The LOOKUP is what SilentReflectionLookupRatchetTests counts, and only when it is one
//   expression (`t.GetProperty(..)?.GetValue(..)`); storing it in a field is that file's stated
//   abstention. The USE is what nobody counted. If BC moves X the handle stays null, `?.` yields
//   null at the use, and the caller acts on "no value" — the same wrong-answer-not-a-failure
//   mechanism as #3647/#3656/#3660, one statement removed from where the ratchet looks.
//
// WHAT THIS ASSERTS
//   Only that the population does not GROW and that every site in it is recorded. It does not
//   say a recorded site is a bug: a handle genuinely optional on some BC version stays silent
//   (`guards-need-a-third-state.md`: a genuinely absent thing must stay a pass), and which uses
//   are load-bearing is per-site adjudication this guard paces and does not perform (#3702 point 1).
//   Where a refusal belongs is at RESOLUTION time — one `BcShape.Property(...)` where the lookup
//   failed, not N guarded reads — so converting a handle's resolution removes ALL its uses here.
//
// WHAT COUNTS (the classifier is CachedReflectionHandleScanner; its header has the rules)
//   a `?.` on a FIELD or PROPERTY of a reflection-handle type that some assignment stores a
//   possibly-null value into.
//   not counted: `!`-resolved handles (BcInternalsNullForgivingGuardTests owns that lookup),
//   `?? throw` and `BcShape.*` resolutions, `typeHandle?.GetProperty(..)` (the lookup ratchet's),
//   and handles that are method LOCALS (see ThisPopulationExcludesLocals).
//
// HOW TO MOVE THE NUMBER
//   Convert a handle's resolution to BcShape.* / `?? throw` and delete its entries. A NEW entry
//   is not the answer to a failure here — give the new handle a loud resolution instead.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class CachedReflectionHandleNullConditionalRatchetTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly record struct UseKey(string File, string Owner, string Handle, string Accessed);

    // ── The assertions ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ratchet, both directions. A NEW use is reported with its line; a RECORDED one that
    /// no longer exists is reported too, so converting a handle cannot leave a stale entry that
    /// later excuses a different site with the same key.
    /// </summary>
    [Fact]
    public void TheCachedHandleUses_AreTheOnesRecorded()
    {
        var (uses, _) = TreeScan.Value;
        var (appeared, vanished) = Diff(Population(uses), KnownUses);

        Assert.True(appeared.Count == 0,
            "`?.` on a cached reflection handle whose lookup answers null silently, not recorded in "
            + "KnownUses. If BC moves the member the handle stays null and the use reads as \"no "
            + "value\" instead of failing (#3702; the lookup half is #3663). Make the handle's "
            + "RESOLUTION refuse — BcShape.Property/Method/Field, or `?? throw` — rather than adding "
            + $"an entry:{Environment.NewLine}"
            + string.Join(Environment.NewLine, appeared));

        Assert.True(vanished.Count == 0,
            "these uses are recorded in KnownUses but are no longer found. If you converted the "
            + "handle's resolution, delete its entries; if you merely MOVED or RENAMED a use, update "
            + $"its entry:{Environment.NewLine}"
            + string.Join(Environment.NewLine, vanished));
    }

    /// <summary>
    /// The third state. A scan that parsed nothing, bound nothing, or could not bind the lookups
    /// it exists to read would find an empty population and pass the test above — the success
    /// state spelled by an instrument that measured nothing. Every figure here is a floor the
    /// real tree clears by a wide margin.
    /// </summary>
    [Fact]
    public void TheScan_MeasuredSomething()
    {
        var (_, stats) = TreeScan.Value;

        Assert.True(stats.FilesParsed >= 100, $"parsed only {stats.FilesParsed} files under AlRunner/");
        Assert.True(stats.NullConditionalAccesses >= 100, $"saw only {stats.NullConditionalAccesses} `?.` expressions");
        Assert.True(stats.HandleReceivers >= 150, $"found only {stats.HandleReceivers} `?.` receivers of a reflection-handle type");
        Assert.True(stats.SilentHandles >= 100 && stats.LoudHandles >= 5,
            $"classified {stats.SilentHandles} silent / {stats.LoudHandles} loud handles — one side is empty, "
            + "so the nullable analysis is not distinguishing resolutions");
        Assert.True(stats.LookupAssignments >= 100, $"read only {stats.LookupAssignments} lookup assignments");
        Assert.True(stats.BcUnresolvedNames.Count == 0,
            "the right-hand side of a handle assignment failed to resolve names that exist in the BCL "
            + "namespaces AlRunner.csproj imports implicitly, so the compilation lost a reference: "
            + string.Join(", ", stats.BcUnresolvedNames));
        Assert.True(stats.UnresolvedAssignments > 0 && stats.UnresolvedAssignments < stats.Assignments,
            $"{stats.UnresolvedAssignments} of {stats.Assignments} handle assignments involved an unresolved "
            + "symbol. BC types ARE unresolved here by design, so some must be (zero means the rule that reads "
            + "them as possibly-null never ran) and not all (all means nothing resolved).");
        Assert.True(stats.Unbound * 10 < stats.LookupAssignments,
            $"{stats.Unbound} of {stats.LookupAssignments} lookup assignments did not BIND. A few do — an "
            + "argument of a BC type is an error type here — and the scanner reads those as nullable. A "
            + "tenth or more means the compilation lost a reference or a global using that "
            + $"AlRunner.csproj supplies: fix CachedReflectionHandleScanner, not this bound:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stats.UnboundSites));
    }

    // ── Arming the ratchet ──────────────────────────────────────────────────────────────
    //
    // A ratchet nobody has seen fail is not one. These drive the scanner over synthetic sources,
    // so the arms prove the MECHANISM without a regression committed to the tree.

    private const string Usings = "using System.Reflection;\n";

    private static IReadOnlyList<(string Handle, string Accessed)> Found(params string[] sources)
    {
        var files = sources.Select((s, i) => ($"f{i}.cs", Usings + s));
        var uses = CachedReflectionHandleScanner.Scan(files, out _);
        return Population(uses).Select(u => (u.Handle, u.Accessed)).OrderBy(x => x).ToList();
    }

    /// <summary>The armed direction, in each spelling a silent resolution takes. Each is driven
    /// separately: a scanner that understood only a bare assignment would still pass an arm that
    /// tested only that.</summary>
    [Theory]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\"); } static object? U(object o) => _p?.GetValue(o);",
                "a raw lookup assigned in a method")]
    [InlineData("static PropertyInfo? _p = typeof(string).GetProperty(\"Length\"); static object? U(object o) => _p?.GetValue(o);",
                "a raw lookup in the field initializer")]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p ??= t.GetProperty(\"X\"); } static object? U(object o) => _p?.GetValue(o);",
                "lazy `??=` initialisation")]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\") ?? t.GetProperty(\"Y\"); } static object? U(object o) => _p?.GetValue(o);",
                "an alternate-name chain can still miss both")]
    [InlineData("static PropertyInfo? Find(System.Type t) => t.GetProperty(\"X\"); static PropertyInfo? _p; static void R(System.Type t) { _p = Find(t); } static object? U(object o) => _p?.GetValue(o);",
                "a runner helper declared to return PropertyInfo?")]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\"); _p = null; } static object? U(object o) => _p?.GetValue(o);",
                "a later reset to null does not make the earlier lookup loud")]
    [InlineData("static PropertyInfo? _p { get; set; } static void R(System.Type t) { _p = t.GetProperty(\"X\"); } static object? U(object o) => _p?.GetValue(o);",
                "a cached handle held in a property rather than a field")]
    [InlineData("static System.Type? _t; static void R(System.Reflection.Assembly a) { _t = a.GetType(\"N.T\"); } static bool U(object o) => _t?.IsInstanceOfType(o) ?? false;",
                "a Type handle (#3622's shape)")]
    [InlineData("static PropertyInfo? _p; static object? U(object o) => _p?.GetValue(o);",
                "a field no source line assigns cannot be proven loud, so it counts")]
    [InlineData("static PropertyInfo? _p = typeof(Nope).GetProperty(\"X\", BindingFlags.NonPublic | BindingFlags.Instance); static object? U(object o) => _p?.GetValue(o);",
                "typeof(<unresolved BC type>).GetProperty(...) — it BINDS, and the compiler then reports it never-null")]
    [InlineData("static PropertyInfo? _p; static void R() { _p ??= typeof(Nope).GetProperty(\"X\", BindingFlags.NonPublic | BindingFlags.Instance); } static object? U(object o) => _p?.GetValue(o);",
                "the same, assigned lazily with ??=")]
    [InlineData("static MethodInfo? _m; static void R() { _m = typeof(Nope).GetMethod(\"X\", new[] { typeof(Nope2) }); } static object? U(object o) => _m?.Invoke(o, null);",
                "an unresolved type in an ARGUMENT of the lookup")]
    [InlineData("static ConstructorInfo? _c; static void R() { _c ??= System.Array.Find(typeof(Nope).GetConstructors(), c => c.GetParameters().Length == 1); } static object? U() => _c?.Invoke(null);",
                "Array.Find over an unresolved type's constructors — not a Get* call, still unmeasurable")]
    public void ANewSilentlyResolvedHandleUse_TripsTheScanner(string source, string why)
    {
        var found = Found("class C { " + source + " }");

        Assert.True(found.Count == 1, $"the scanner must see `{why}`; found {found.Count}");
    }

    /// <summary>The other half of armed: the SAME use, with the resolution made loud, is not
    /// counted. Without it the theory above would also pass a scanner that flagged every `?.`
    /// on a handle unconditionally.</summary>
    [Theory]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\") ?? throw new System.InvalidOperationException(\"gone\"); } static object? U(object o) => _p?.GetValue(o);",
                "`?? throw` at resolution")]
    [InlineData("static PropertyInfo? _p; static PropertyInfo Req(System.Type t) => t.GetProperty(\"X\")!; static void R(System.Type t) { _p = Req(t); } static object? U(object o) => _p?.GetValue(o);",
                "a helper declared non-nullable — the BcShape.Property shape")]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\")!; } static object? U(object o) => _p?.GetValue(o);",
                "null-forgiving: BcInternalsNullForgivingGuardTests owns that lookup")]
    [InlineData("static PropertyInfo? _p = typeof(Nope).GetProperty(\"X\", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new System.InvalidOperationException(\"gone\"); static object? U(object o) => _p?.GetValue(o);",
                "`?? throw` over an unresolved-type lookup stays loud — an over-refusing scanner is also a failure")]
    [InlineData("static PropertyInfo? _p = typeof(Nope).GetProperty(\"X\", BindingFlags.NonPublic | BindingFlags.Instance)!; static object? U(object o) => _p?.GetValue(o);",
                "`!` over an unresolved-type lookup stays owned by the null-forgiving guard")]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\"); } static object? U(object o) => _p!.GetValue(o);",
                "no `?.` at the use — a failed lookup NREs rather than answering")]
    [InlineData("static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\")!; _p = null; } static object? U(object o) => _p?.GetValue(o);",
                "a reset to null is not a resolution, so it cannot make a loud handle silent")]
    [InlineData("static System.Type? _t; static void R(System.Reflection.Assembly a) { _t = a.GetType(\"N.T\"); } static object? U() => _t?.GetProperty(\"X\");",
                "`typeHandle?.GetProperty(..)` is SilentReflectionLookupRatchetTests' population")]
    [InlineData("static string? _s; static void R() { _s = \"x\"; } static int? U() => _s?.Length;",
                "a `?.` on something that is not a reflection handle")]
    [InlineData("static System.Type? U(System.Reflection.MemberInfo m) => m.DeclaringType?.BaseType;",
                "a BCL property is not a handle this code resolved")]
    [InlineData("static PropertyInfo? _p; /* _p = t.GetProperty(\"X\"); _p?.GetValue(o) */ static string S = \"_p?.GetValue(o)\";",
                "prose and string literals describing the shape")]
    public void TheSameUseMadeLoudOrNotAHandle_IsNotCounted(string source, string why)
    {
        Assert.True(Found("class C { " + source + " }").Count == 0, why);
    }

    /// <summary>A handle is silent when ANY assignment is, so one loud resolution beside a silent
    /// one does not launder the other. Mutating the aggregation to `all` instead of `any` is what
    /// this pins.</summary>
    [Fact]
    public void OneSilentAssignment_AmongLoudOnes_StillCounts()
    {
        const string src = """
            class C {
                static PropertyInfo? _p;
                static void A(System.Type t) { _p = t.GetProperty("X") ?? throw new System.Exception("a"); }
                static void B(System.Type t) { _p = t.GetProperty("Y"); }
                static object? U(object o) => _p?.GetValue(o);
            }
            """;

        Assert.Equal(new[] { ("_p", "GetValue") }, Found(src));
    }

    /// <summary>The use and the lookup live in different files of one partial class — the
    /// normal case for BcRuntime and RecordPatches, and the reason this reads symbols rather
    /// than lines.</summary>
    [Fact]
    public void TheResolutionAndTheUse_MayLiveInDifferentFilesOfAPartialClass()
    {
        const string decl = "partial class C { static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\"); } }";
        const string use = "partial class C { static object? U(object o) => _p?.GetValue(o); }";

        Assert.Equal(new[] { ("_p", "GetValue") }, Found(decl, use));
        Assert.Empty(Found("partial class C { static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty(\"X\")!; } }", use));
    }

    /// <summary>Two classes that happen to name a handle the same are different handles: only
    /// the one with a silent resolution counts.</summary>
    [Fact]
    public void TwoHandlesSharingAName_AreNotConflated()
    {
        const string src = """
            class A { static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty("X"); } static object? U(object o) => _p?.GetValue(o); }
            class B { static PropertyInfo? _p; static void R(System.Type t) { _p = t.GetProperty("X")!; } static object? U(object o) => _p?.GetValue(o); }
            """;

        Assert.Equal(new[] { ("_p", "GetValue") }, Found(src));
    }

    /// <summary>A local the compiler populated — a pattern variable, an `out var` — was never
    /// assigned by a lookup this code wrote, so it abstains instead of reading as unresolved.</summary>
    [Fact]
    public void ALocalNoStatementAssigns_Abstains()
    {
        const string src = "class C { static object? M(System.Type t, object o) { if (t.GetProperty(\"X\") is { } p) return p?.GetValue(o); return null; } }";

        Assert.Empty(CachedReflectionHandleScanner.Scan(new[] { ("f.cs", Usings + src) }, out _));
    }

    /// <summary>Method locals are a different shape — the null never leaves its method, so the
    /// failure path is local — and this ratchet's population is CACHED handles. The scanner still
    /// reports them (kind Local) so the follow-up has an instrument; this pins that the ratchet
    /// does not silently absorb them into a baseline they were not measured for.</summary>
    [Fact]
    public void ThisPopulationExcludesLocals()
    {
        const string src = "class C { static object? M(System.Type t, object o) { var p = t.GetProperty(\"X\"); return p?.GetValue(o); } }";

        var all = CachedReflectionHandleScanner.Scan(new[] { ("f.cs", Usings + src) }, out _);

        Assert.Equal(HandleKind.Local, Assert.Single(all).Kind);
        Assert.Empty(Found(src));
    }

    /// <summary>A lookup that fails to BIND — here because its receiver type is unresolvable — is
    /// reported in the stats AND read as nullable, not as never-null. This is the fallback the
    /// real tree needs for the few lookups with a BC-typed argument; `!` still makes it loud.</summary>
    [Fact]
    public void ALookupThatFailsToBind_IsCountedAsUnbound_AndReadAsNullable()
    {
        const string use = "static object? U(object o) => _p?.GetValue(o);";
        string Decl(string rhs) => "class C { static PropertyInfo? _p; static void R(Nope t) { _p = " + rhs + "; } " + use + " }";

        CachedReflectionHandleScanner.Scan(new[] { ("f.cs", Usings + Decl("t.GetProperty(\"X\")")) }, out var stats);

        Assert.True(stats.Unbound >= 1, "an unbound lookup must be visible in the stats");
        Assert.Single(Found(Decl("t.GetProperty(\"X\")")));
        Assert.Empty(Found(Decl("t.GetProperty(\"X\")!")));
        Assert.Empty(Found(Decl("t.GetProperty(\"X\") ?? throw new System.Exception(\"gone\")")));
    }

    /// <summary>BC types are unresolved in this compilation on purpose, so that alone must not
    /// refuse; an unresolved name that EXISTS in the BCL is what means a reference was lost.
    /// Driven by compiling without the global usings the project supplies.</summary>
    [Fact]
    public void UnresolvedBcTypes_AreCounted_AndOnlyAMissingBclNameIsARefusal()
    {
        const string bc = "class C { static PropertyInfo? _p; static void R() { _p = typeof(Nope).GetProperty(\"X\", BindingFlags.Instance); } static object? U(object o) => _p?.GetValue(o); }";
        CachedReflectionHandleScanner.Scan(new[] { ("f.cs", Usings + bc) }, out var withBc);
        Assert.Equal(1, withBc.UnresolvedAssignments);
        Assert.Empty(withBc.BcUnresolvedNames);

        const string bcl = "class C { static PropertyInfo? _p; static PropertyInfo? Pick(object o) => null; static void R() { _p = Pick(new List<int>()); } static object? U(object o) => _p?.GetValue(o); }";
        CachedReflectionHandleScanner.Scan(new[] { ("f.cs", Usings + bcl) }, out var intact);
        Assert.Empty(intact.BcUnresolvedNames);

        CachedReflectionHandleScanner.Scan(new[] { ("f.cs", Usings + bcl) }, out var lost, globalUsings: Array.Empty<string>());
        Assert.Contains(lost.BcUnresolvedNames, n => n.StartsWith("List<", StringComparison.Ordinal));
    }

    /// <summary>The scanner's implicit-usings list is a COPY of what the SDK adds for
    /// `ImplicitUsings`. Removing one is caught by the unbound ratio; ADDING a `Using` item to the
    /// project is not — an unresolved `PropertyInfo` drops its receivers before any floor moves —
    /// so this reads the production files.</summary>
    [Fact]
    public void TheImplicitUsingsCopy_MatchesTheProjectFiles()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot, "AlRunner", "AlRunner.csproj"));
        Assert.Equal("enable", project.Descendants("ImplicitUsings").Single().Value);

        foreach (var f in new[] { Path.Combine("AlRunner", "AlRunner.csproj"), "Directory.Build.props", "Directory.Build.targets" })
        {
            var path = Path.Combine(RepoRoot, f);
            if (!File.Exists(path)) continue;
            var usings = System.Xml.Linq.XDocument.Load(path).Descendants("Using").ToList();
            Assert.True(usings.Count == 0,
                $"{f} declares <Using> item(s) ({string.Join(", ", usings.Select(u => (string?)u.Attribute("Include") ?? (string?)u.Attribute("Remove")))}); "
                + "add them to CachedReflectionHandleScanner.ImplicitUsings, or files relying on them stop binding "
                + "and their handles drop out of the scan unseen.");
        }
    }

    /// <summary>The comparison is a pure function, so the two failure directions are driven
    /// here without editing the tree: a new use appears, and a recorded one vanishes.</summary>
    [Fact]
    public void TheComparison_FailsBothWays_AndNamesTheSite()
    {
        var known = new Dictionary<UseKey, int>
        {
            [new UseKey("a.cs", "C", "_gone", "GetValue")] = 1,
            [new UseKey("a.cs", "C", "_two", "Invoke")] = 2,
        };
        var actual = new[]
        {
            new HandleUse("a.cs", 10, HandleKind.Field, "C", "_two", "Invoke"),
            new HandleUse("a.cs", 11, HandleKind.Field, "C", "_two", "Invoke"),
            new HandleUse("a.cs", 12, HandleKind.Field, "C", "_two", "Invoke"),   // a third use of a key recorded twice
            new HandleUse("b.cs", 20, HandleKind.Field, "D", "_new", "GetValue"),
        };

        var (appeared, vanished) = Diff(actual, known);

        Assert.Equal(2, appeared.Count);
        Assert.Contains(appeared, s => s.Contains("b.cs") && s.Contains("_new"));
        Assert.Contains(appeared, s => s.Contains("_two") && s.Contains("recorded 2, found 3"));
        Assert.Single(vanished);
        Assert.Contains("_gone", vanished[0]);
    }

    // ── The scan of the tree ────────────────────────────────────────────────────────────

    private static readonly Lazy<(IReadOnlyList<HandleUse> Uses, HandleScanStats Stats)> TreeScan = new(() =>
    {
        var root = Path.Combine(RepoRoot, "AlRunner");
        Assert.True(Directory.Exists(root), $"AlRunner/ not found at {root} — the repo root walk is wrong.");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                     && !p.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path.GetRelativePath(RepoRoot, p).Replace('\\', '/'), File.ReadAllText(p)))
            .ToList();
        var uses = CachedReflectionHandleScanner.Scan(files, out var stats);
        return (uses, stats);
    });

    private static IEnumerable<HandleUse> Population(IEnumerable<HandleUse> uses) =>
        uses.Where(u => u.Kind != HandleKind.Local);

    /// <summary>Keyed on file + owner + handle + accessed member, never on a line number: a line
    /// number goes stale on the next edit above it and would turn this guard into noise.</summary>
    private static (List<string> Appeared, List<string> Vanished) Diff(
        IEnumerable<HandleUse> uses, IReadOnlyDictionary<UseKey, int> known)
    {
        var actual = uses.GroupBy(u => new UseKey(u.File, u.Owner, u.Handle, u.Accessed)).ToList();
        var appeared = new List<string>();
        var vanished = new List<string>();

        foreach (var g in actual.OrderBy(g => g.Key.File, StringComparer.Ordinal).ThenBy(g => g.Key.Handle, StringComparer.Ordinal))
        {
            known.TryGetValue(g.Key, out var was);
            if (g.Count() > was)
                appeared.Add($"{g.Key.File}:{string.Join(",", g.Select(u => u.Line))}  {g.Key.Owner}.{g.Key.Handle}?.{g.Key.Accessed}  (recorded {was}, found {g.Count()})");
        }
        var present = actual.ToDictionary(g => g.Key, g => g.Count());
        foreach (var (key, was) in known.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.Handle, StringComparer.Ordinal))
        {
            present.TryGetValue(key, out var now);
            if (now < was)
                vanished.Add($"{key.File}  {key.Owner}.{key.Handle}?.{key.Accessed}  (recorded {was}, found {now})");
        }
        return (appeared, vanished);
    }

    // ── The allowlist ───────────────────────────────────────────────────────────────────
    //
    // Generated from the tree at the commit that introduced this file, NOT curated: every entry
    // is a use somebody has yet to adjudicate, and being on the list says only "this existed when
    // the ratchet was installed", never "this is correct".
    //
    // TO MOVE THE NUMBER: give the handle a loud resolution and delete its entries.

    private static readonly IReadOnlyDictionary<UseKey, int> KnownUses = new (string File, string Owner, string Handle, string Accessed, int Count)[]
    {
        // BcRuntime.cs — 1
        ("AlRunner/BcRuntime.cs", "BcRuntime", "_fCu151InitializationInProgress", "SetValue", 1),
        // AlCallStackCapture.cs — 9
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_fiMsFlags", "FieldType", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piApplicationObject", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piEncodedSpans", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piIsRootScope", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piObjectName", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piParentScope", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piScopeName", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piSigEncodedSpan", "GetValue", 1),
        ("AlRunner/Infrastructure/AlCallStackCapture.cs", "AlCallStackCapture", "_piStatementNumber", "GetValue", 1),
        // ApplicationObjectBasePatches.cs — 1
        ("AlRunner/Patches/ApplicationObjectBasePatches.cs", "BcRuntime", "_fAoExecPermValidated", "GetValue", 1),
        // CodeunitEventDispatcher.cs — 3
        ("AlRunner/Patches/CodeunitEventDispatcher.cs", "BcRuntime", "_pNavOption_Value", "GetValue", 1),
        ("AlRunner/Patches/CodeunitEventDispatcher.cs", "BcRuntime", "_piSessionEventBindings", "GetValue", 2),
        // CodeunitPatches.MetaCodeunit.cs — 1
        ("AlRunner/Patches/CodeunitPatches.MetaCodeunit.cs", "BcRuntime", "_fNCLMetaAOClrTypeContainer", "FieldType", 1),
        // CompanyAccessPatches.cs — 1
        ("AlRunner/Patches/CompanyAccessPatches.cs", "CompanyAccessPatches", "_fCompanyName", "GetValue", 1),
        // FlowFieldPatches.cs — 18
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fEmptyFiltersAndMarks", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fRecImplDataAccess", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fRecImplMutableRecordBuffer", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fRecImplMutableRecordBuffer", "SetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fRecImplSecurityFiltering", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fRecImplTableState", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_fSessionDataAccessSource", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_mMutableBufferGetChangedFieldValue", "Invoke", 2),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_mMutableBufferGetOriginalValue", "Invoke", 2),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pCalcFormulaFieldId", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pCalcFormulaNegateResult", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pCalcFormulaTableId", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pDataAccessDataProvider", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pRecImplCurrentBufferOrDefault", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pTableStateFiltersAndMarks", "GetValue", 1),
        ("AlRunner/Patches/FlowFieldPatches.cs", "FlowFieldPatches", "_pTableStateReadIsolation", "GetValue", 1),
        // GetCallerRecordPatches.cs — 1
        ("AlRunner/Patches/GetCallerRecordPatches.cs", "BcRuntime", "_fSessCurrentScope", "GetValue", 1),
        // HelperShims.cs — 1
        ("AlRunner/Patches/HelperShims.cs", "BcRuntime", "_fTruncateTtdpTable", "GetValue", 1),
        // MediaSetPatches.cs — 2
        ("AlRunner/Patches/MediaSetPatches.cs", "MediaValueReflectors", "Key", "GetValue", 1),
        ("AlRunner/Patches/MediaSetPatches.cs", "MediaValueReflectors", "SaveValueToTableField", "Invoke", 1),
        // MetadataPatches.cs — 1
        ("AlRunner/Patches/MetadataPatches.cs", "BcRuntime", "_piTestExecution", "GetValue", 1),
        // MethodScopePatches.cs — 1
        ("AlRunner/Patches/MethodScopePatches.cs", "BcRuntime", "_fSessLastException", "SetValue", 1),
        // NavAppResourcePatches.cs — 3
        ("AlRunner/Patches/NavAppResourcePatches.cs", "NavAppResourcePatches", "_pApplicationObject", "GetValue", 1),
        ("AlRunner/Patches/NavAppResourcePatches.cs", "NavAppResourcePatches", "_pCurrentMethodScope", "GetValue", 1),
        ("AlRunner/Patches/NavAppResourcePatches.cs", "NavAppResourcePatches", "_pParentScope", "GetValue", 1),
        // NavRecordRefPatches.cs — 12
        ("AlRunner/Patches/NavRecordRefPatches.cs", "BcRuntime", "_mTreeGetReferenceTarget", "Invoke", 6),
        ("AlRunner/Patches/NavRecordRefPatches.cs", "BcRuntime", "_mTreeSetReferenceTarget", "Invoke", 6),
        // NavReportSync.cs — 22
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_appObjBaseSessionProp", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_dataItemMetaData", "SetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_dataItemPrintOnlyIfDetail", "SetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_dataItemSetAutoCalcFields", "Invoke", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_dataItemsField", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_diDataItemsProp", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_getDataItemByName", "Invoke", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_masterPagePagePropsProp", "CanWrite", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_masterPagePagePropsProp", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_metaDataItemPrintOnlyIfDetail", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_metaReportDataItems", "GetValue", 2),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_metaReportDataItemsField", "GetValue", 2),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_metadataSetter", "CanWrite", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_objectNumberProp", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_ppSourceObjectProp", "CanWrite", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_processingOnlyBackingField", "SetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_realMetaDefaultLayout", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_synthMdiExtNameProp", "SetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_synthMdiVarNameProp", "GetValue", 1),
        ("AlRunner/Patches/NavReportSync.cs", "NavReportSync", "_synthMdiVarNameProp", "SetValue", 1),
        // RecordPatches.CreateObjectInstance.cs — 2
        ("AlRunner/Patches/RecordPatches.CreateObjectInstance.cs", "RecordPatches", "_baseNavRecordCtor", "Invoke", 1),
        ("AlRunner/Patches/RecordPatches.CreateObjectInstance.cs", "RecordPatches", "_fOrderedExtensionObjects", "GetValue", 1),
        // RecordPatches.DataAccessDispatch.cs — 7
        ("AlRunner/Patches/RecordPatches.DataAccessDispatch.cs", "RecordPatches", "_fDasGlobalFilters", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.DataAccessDispatch.cs", "RecordPatches", "_fDasSession", "GetValue", 6),
        // RecordPatches.FeatureKeyVirtualTable.cs — 1
        ("AlRunner/Patches/RecordPatches.FeatureKeyVirtualTable.cs", "RecordPatches", "_fkProviderType", "GetConstructors", 1),
        // RecordPatches.FieldFindIntercept.cs — 1
        ("AlRunner/Patches/RecordPatches.FieldFindIntercept.cs", "RecordPatches", "_pFieldNo", "GetValue", 1),
        // RecordPatches.FieldVirtualTable.cs — 2
        ("AlRunner/Patches/RecordPatches.FieldVirtualTable.cs", "RecordPatches", "_fNclMetaTableAllFields", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.FieldVirtualTable.cs", "RecordPatches", "_pNclMetaTableAllFields", "GetValue", 1),
        // RecordPatches.NavAppExtraVirtualTable.cs — 1
        ("AlRunner/Patches/RecordPatches.NavAppExtraVirtualTable.cs", "RecordPatches", "_naeProviderType", "GetConstructors", 1),
        // RecordPatches.ObjectInventoryStore.cs — 1
        ("AlRunner/Patches/RecordPatches.ObjectInventoryStore.cs", "RecordPatches", "_pDataAccessDataProvider", "GetValue", 1),
        // RecordPatches.QueryProjection.cs — 3
        ("AlRunner/Patches/RecordPatches.QueryProjection.cs", "RecordPatches", "_pDataItemSourceFlowField", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.QueryProjection.cs", "RecordPatches", "_pDataItemSubQueryDefinition2", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.QueryProjection.cs", "RecordPatches", "_pNavCurrentThreadSession", "GetValue", 1),
        // RecordPatches.TestFieldErrors.cs — 5
        ("AlRunner/Patches/RecordPatches.TestFieldErrors.cs", "RecordPatches", "_mGetFieldValue", "Invoke", 2),
        ("AlRunner/Patches/RecordPatches.TestFieldErrors.cs", "RecordPatches", "_pNCLMetaFieldFieldName", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.TestFieldErrors.cs", "RecordPatches", "_pNCLMetaFieldParent", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.TestFieldErrors.cs", "RecordPatches", "_pNCLMetaTableTableName", "GetValue", 1),
        // RecordPatches.TextConstant.cs — 3
        ("AlRunner/Patches/RecordPatches.TextConstant.cs", "RecordPatches", "_fMultiLanguage_languageIds", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.TextConstant.cs", "RecordPatches", "_fMultiLanguage_texts", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.TextConstant.cs", "RecordPatches", "_fNavTextConstant_multiLanguage", "GetValue", 1),
        // RecordPatches.cs — 7
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_fSessionDataAccessSource", "GetValue", 1),
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_fSessionDataAccessSource", "SetValue", 1),
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_fTtdpComparer", "SetValue", 1),
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_fTtdpNavSession", "SetValue", 1),
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_fTtdpPrimaryKeySortingFields", "SetValue", 1),
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_fTtdpTable", "SetValue", 1),
        ("AlRunner/Patches/RecordPatches.cs", "RecordPatches", "_pNclMetaKeySortingFieldsWithPK", "GetValue", 1),
        // RecordWritePatches.cs — 8
        ("AlRunner/Patches/RecordWritePatches.cs", "BcRuntime", "_fNavRecordRecordImplementation", "GetValue", 4),
        ("AlRunner/Patches/RecordWritePatches.cs", "BcRuntime", "_fRecordImplementationDataAccess", "GetValue", 1),
        ("AlRunner/Patches/RecordWritePatches.cs", "BcRuntime", "_fRecordImplementationMetaTable", "GetValue", 1),
        ("AlRunner/Patches/RecordWritePatches.cs", "BcRuntime", "_fRecordImplementationMutableRecordBuffer", "SetValue", 1),
        ("AlRunner/Patches/RecordWritePatches.cs", "BcRuntime", "_pMrbResultRecordBuffer", "GetValue", 1),
        // RunnerPageInstance.cs — 1
        ("AlRunner/Patches/RunnerPageInstance.cs", "RunnerPageInstance", "_pFormExtensionParentObject", "SetValue", 1),
        // RunnerTestPageState.cs — 1
        ("AlRunner/Patches/RunnerTestPageState.cs", "RunnerTestPageState", "_testPageField", "GetValue", 1),
        // SessionPatches.cs — 3
        ("AlRunner/Patches/SessionPatches.cs", "BcRuntime", "_pRefTarget", "GetValue", 1),
        ("AlRunner/Patches/SessionPatches.cs", "BcRuntime", "_pTlsCurrent", "GetValue", 1),
        ("AlRunner/Patches/SessionPatches.cs", "BcRuntime", "_pTlsSession", "GetValue", 1),
        // TableConnectionPatches.cs — 7
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_fSessionManager", "GetValue", 1),
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_mGetCurrentTableConnectionOpen", "GetParameters", 1),
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_pConnectionName", "GetValue", 1),
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_pMetaTableTableType", "GetValue", 1),
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_tCrmConnection", "GetMethods", 1),
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_tManager", "GetConstructors", 1),
        ("AlRunner/Patches/TableConnectionPatches.cs", "TableConnectionPatches", "_tManager", "GetMethods", 1),
        // TruncateValidationPatches.cs — 7
        ("AlRunner/Patches/TruncateValidationPatches.cs", "BcRuntime", "_mMtIsEventSubscribed", "GetParameters", 2),
        ("AlRunner/Patches/TruncateValidationPatches.cs", "BcRuntime", "_pMtMediaFieldCount", "GetValue", 1),
        ("AlRunner/Patches/TruncateValidationPatches.cs", "BcRuntime", "_pMtSupportsTruncation", "GetValue", 1),
        ("AlRunner/Patches/TruncateValidationPatches.cs", "BcRuntime", "_pRecIsTemporary", "GetValue", 1),
        ("AlRunner/Patches/TruncateValidationPatches.cs", "BcRuntime", "_pRecMetaTable", "GetValue", 1),
        ("AlRunner/Patches/TruncateValidationPatches.cs", "BcRuntime", "_pRecMetaTable", "PropertyType", 1),
    }.ToDictionary(e => new UseKey(e.File, e.Owner, e.Handle, e.Accessed), e => e.Count);
}
