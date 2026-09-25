// LogSingleWordTagContractTests — issue #2221.
//
// Log's filter suppresses any line starting with a single-word `[Tag]` unless --verbose,
// except for a hardcoded exemption list. Nothing failed when a tag was missing from that
// list, so five separate user-facing fixes each shipped invisible: `[bc]`, `[expectations]`
// (#1984), `[reexec]` (#2034), `[dap]` (#1642) and `[warn]` (#2206). Each was found by
// accident — a harness timing out, a debug print vanishing — never by a test.
//
// All five were a NEW TAG, which is what this census guards: a tag appearing in production
// source with no entry in Declared fails EverySingleWordTagIsClassified, so the visibility
// decision has to be made rather than defaulted. Sibling shape for hyphenated tags, which
// the filter does not act on at all: LogHyphenatedTagContractTests (#2257),
// docs/log-filter.md#hyphenated-tags. Census and per-tag reasoning:
// docs/log-filter.md#single-word-tags.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// Serial: swaps the process-wide Console writers and Log.Verbose. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class LogSingleWordTagContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public enum Kind
    {
        /// <summary>Survives the default filter: a result, a failure, or readiness the user asked for.</summary>
        UserFacing,
        /// <summary>Per-object or per-method chatter. Suppressed by default; --verbose recovers it.</summary>
        Internal,
        /// <summary>Matches the tag shape but is never written as a console line. Needs a Reason.</summary>
        NotALogLine,
    }

    public sealed record Tag(string Name, Kind Kind, string? Reason = null);

    // Severity is a CLASS, not three more exemptions (#2221). A line the author thought worth
    // calling a warning or an error is worth the user seeing, whatever component raised it.
    // `fatal` has no call site today and is declared so the next author to reach for it is
    // already covered; it changes nothing that runs now.
    public static readonly string[] SeverityLevels = { "warn", "error", "fatal" };

    private static readonly Tag[] Declared =
    {
        // --- UserFacing: reaches the terminal at default verbosity. Log.cs exempts these.
        new("bc", Kind.UserFacing),
        new("dap", Kind.UserFacing),
        new("dep", Kind.UserFacing),
        new("expectations", Kind.UserFacing),
        new("layered", Kind.UserFacing),
        new("provision", Kind.UserFacing),
        new("reexec", Kind.UserFacing),
        new("warn", Kind.UserFacing),
        new("watch", Kind.UserFacing),

        // --- NotALogLine: matches the tag shape but is never written as a console line.
        new("blue", Kind.NotALogLine, "Spectre.Console markup"),
        new("bold", Kind.NotALogLine, "Spectre.Console markup"),
        new("Content_Types", Kind.NotALogLine, "the [Content_Types].xml entry name inside an .app package"),
        new("green", Kind.NotALogLine, "Spectre.Console markup"),
        new("NavByReferenceAttribute", Kind.NotALogLine, "a RuntimeAttributes value BC's metadata emitter writes verbatim, rendered into a <Parameter> element (#3788)"),
        new("grey", Kind.NotALogLine, "Spectre.Console markup"),
        new("Oo", Kind.NotALogLine, "a regex character class, [Oo]bject, in a BcCompiler diagnostic pattern"),
        new("red", Kind.NotALogLine, "Spectre.Console markup"),
        new("yellow", Kind.NotALogLine, "Spectre.Console markup"),

        // --- Internal: per-object/per-method chatter. Suppressed by default, --verbose recovers it.
        new("AoCtor", Kind.Internal),
        new("AsyncSM", Kind.Internal),
        new("BcAppSymbolCache", Kind.Internal),
        new("BcAssembler", Kind.Internal),
        new("BcCompiler", Kind.Internal),
        new("BcRuntime", Kind.Internal),
        new("cache", Kind.Internal),
        new("CalcFormula", Kind.Internal),
        new("Cecil", Kind.Internal),
        new("ColumnFilter", Kind.Internal),
        new("coverage", Kind.Internal),
        new("deps", Kind.Internal),
        new("DiagIC", Kind.Internal),
        new("Dispatch", Kind.Internal),
        new("DispatchArg", Kind.Internal),
        new("DispatchRethrow", Kind.Internal),
        new("DispatchThrow", Kind.Internal),
        new("EnumMetadata", Kind.Internal),
        new("EventPipeJIT", Kind.Internal),
        new("FCE", Kind.Internal),
        new("FlowFieldPatches", Kind.Internal),
        new("HandlerFunctions", Kind.Internal),
        new("IndirectSpike", Kind.Internal),
        new("InstallBaselineDisk", Kind.Internal),
        new("iterations", Kind.Internal),
        new("JmpHook", Kind.Internal),
        new("JmpHook.InstallIndirect", Kind.Internal),
        new("JmpHook.WriteJmp", Kind.Internal),
        new("MediaPatches", Kind.Internal),
        new("MockTestPage.EagerlyBuildParts", Kind.Internal),
        new("MockTestPage.GetPart", Kind.Internal),
        new("ModalPageHandler", Kind.Internal),
        new("NavEventSubscriber", Kind.Internal),
        new("NavForm.RunModalAsync", Kind.Internal),
        new("NavFormHandle_CreateTarget", Kind.Internal),
        new("NavRecordIdPatches", Kind.Internal),
        new("NavReportSync", Kind.Internal),
        new("NclMetaQueryBuilder", Kind.Internal),
        new("Patch", Kind.Internal),
        new("PermissionSetAssignment", Kind.Internal),
        new("PublishedApplication", Kind.Internal),
        new("RecordPatches", Kind.Internal),
        new("ReportLayout", Kind.Internal),
        new("resources", Kind.Internal),
        new("ReturnTrue", Kind.Internal),
        new("RowVersionPatches", Kind.Internal),
        new("RunnerModalDispatch", Kind.Internal),
        new("RunnerPageInstance", Kind.Internal),
        new("SecurityFiltering", Kind.Internal),
        new("SeedDebug", Kind.Internal),
        new("server", Kind.Internal),
        new("shutdown", Kind.Internal),
        new("Spike4", Kind.Internal),
        new("Subscribers", Kind.Internal),
        new("TableConnectionPatches", Kind.Internal),
        new("TableExt", Kind.Internal),
        new("TableExtDelta", Kind.Internal),
        new("TableRelation", Kind.Internal),
        new("Test", Kind.Internal),
        new("testpage", Kind.Internal),
    };

    private sealed record Found(string File, int Line, string Tag, string Rendered);

    // Log.cs's own tag body, so the census sees exactly what the filter sees: NO hyphen.
    private static readonly Regex SingleWordTagLiteral = new(
        "(?:\\$@|@\\$|\\$|@)?\"(?<lit>\\[(?<tag>[A-Za-z][A-Za-z0-9._+]*)\\](?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    private static List<Found> ScanProductionSource()
    {
        var root = Path.Combine(RepoRoot, "AlRunner");
        Assert.True(Directory.Exists(root), $"production source not found at {root}");
        var found = new List<Found>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            foreach (var (line, i) in File.ReadAllLines(path).Select((l, i) => (l, i)))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in SingleWordTagLiteral.Matches(line))
                    found.Add(new Found(rel, i + 1, m.Groups["tag"].Value,
                        Regex.Replace(m.Groups["lit"].Value, @"\{[^{}]*\}", "<value>")));
            }
        }
        // A scan that silently matched nothing would pass every assertion below (#2221 is a
        // bug about silence; its guard must not have one). 1016 sites at the census.
        Assert.True(found.Count >= 500, $"scan found only {found.Count} single-word tag literals; expected ~1000");
        return found;
    }

    private static string FilterOnce(string line, bool verbose)
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();
            Log.Verbose = verbose;
            Console.Error.WriteLine(line);
            return sink.ToString();
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    private static Tag? Resolve(string tag) => Declared.FirstOrDefault(d => d.Name == tag);

    /// <summary>The ratchet. All five known instances were a new tag; this is what stops the sixth.</summary>
    [Fact]
    public void EverySingleWordTagIsClassified_AndNoDeclarationIsStale()
    {
        var found = ScanProductionSource();
        var undeclared = found.Where(f => Resolve(f.Tag) == null)
            .GroupBy(f => f.Tag)
            .Select(g => $"[{g.Key}] ({g.Count()} site(s), first at {g.First().File}:{g.First().Line})")
            .ToList();
        Assert.True(undeclared.Count == 0,
            "a single-word tag reaching Log's filter needs a visibility decision in Declared "
            + "(UserFacing / Internal / NotALogLine) — without one it is silently suppressed:\n  "
            + string.Join("\n  ", undeclared));

        var live = found.Select(f => f.Tag).ToHashSet();
        var stale = Declared.Where(d => !live.Contains(d.Name)).Select(d => $"[{d.Name}]").ToList();
        Assert.True(stale.Count == 0, "declarations matching no call site:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>Read from the real call site, pushed through the real filter — not asserted over the list.</summary>
    [Fact]
    public void UserFacingTags_SurviveTheDefaultFilter()
    {
        var eaten = ScanProductionSource()
            .Where(f => Resolve(f.Tag) is { Kind: Kind.UserFacing })
            .Where(f => !FilterOnce(f.Rendered, verbose: false).Contains(f.Rendered, StringComparison.Ordinal))
            .Select(f => $"{f.File}:{f.Line} {f.Rendered}")
            .ToList();
        Assert.True(eaten.Count == 0,
            "the default filter drops lines declared user-facing — this is #2221's defect:\n  "
            + string.Join("\n  ", eaten));
    }

    /// <summary>Both halves matter: suppressed by default, and still RECOVERABLE with --verbose.</summary>
    [Fact]
    public void InternalTags_AreSuppressedByDefault_ButRecoverableWithVerbose()
    {
        var internals = ScanProductionSource().Where(f => Resolve(f.Tag) is { Kind: Kind.Internal }).ToList();
        Assert.True(internals.Count >= 400, $"expected the bulk of sites to be Internal, found {internals.Count}");

        var leaked = internals
            .Where(f => FilterOnce(f.Rendered, verbose: false).Contains(f.Rendered, StringComparison.Ordinal))
            .Select(f => $"{f.File}:{f.Line} {f.Rendered}").Take(20).ToList();
        Assert.True(leaked.Count == 0,
            "chatter declared Internal now prints on every run:\n  " + string.Join("\n  ", leaked));

        var lost = internals
            .Where(f => !FilterOnce(f.Rendered, verbose: true).Contains(f.Rendered, StringComparison.Ordinal))
            .Select(f => $"{f.File}:{f.Line} {f.Rendered}").Take(20).ToList();
        Assert.True(lost.Count == 0,
            "--verbose no longer recovers chatter, so it is unreachable rather than hidden:\n  "
            + string.Join("\n  ", lost));
    }

    /// <summary>#2221: a severity is never an internal diagnostic, whatever component raised it.</summary>
    [Theory]
    [InlineData("warn")]
    [InlineData("error")]
    [InlineData("fatal")]
    public void SeverityTags_AreNeverSuppressed(string severity)
    {
        Assert.Contains(severity, SeverityLevels);
        var line = $"[{severity}] SomeComponent: the row was not seeded, so downstream AL will fail";
        Assert.Contains(line, FilterOnce(line, verbose: false));
    }

    /// <summary>A NotALogLine claim is unverifiable by the filter, so it carries its reason (#2221).</summary>
    [Fact]
    public void NotALogLineDeclarations_CarryAReason()
    {
        var bare = Declared.Where(d => d.Kind == Kind.NotALogLine && string.IsNullOrWhiteSpace(d.Reason))
            .Select(d => d.Name).ToList();
        Assert.True(bare.Count == 0,
            "NotALogLine is the one kind the filter cannot check, so it must say why:\n  "
            + string.Join("\n  ", bare));
    }
}
