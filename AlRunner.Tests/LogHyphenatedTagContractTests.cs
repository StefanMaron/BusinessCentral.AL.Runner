// LogHyphenatedTagContractTests — issue #2257.
//
// Log's ComponentTag pattern has no `-` in its character class, so a hyphenated tag is never
// filtered: its call site alone decides whether it prints. That is the contract, not an
// accident, because every hyphenated call site in AlRunner/ is one of three kinds and a
// widened pattern breaks two of them — a Loud line (a failure or a result) would vanish at
// default verbosity, and an OptIn trace (behind AL_RUNNER_TRACE_*, BCCOMPILER_TIMING, …) would
// print nothing when its switch is set, because the switch does not set Log.Verbose.
// Chatter is gated with `if (Log.Verbose)` at the site instead (#2239's lever).
// Classification and the reasoning per tag: docs/log-filter.md#hyphenated-tags.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// Serial: swaps the process-wide Console writers and Log.Verbose. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class LogHyphenatedTagContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public enum Kind { Loud, OptIn, VerboseGated }

    /// <param name="Anchor">Required when one file carries the tag at sites of different kinds.</param>
    /// <param name="Gate">OptIn only: the switch that must appear within <see cref="GateWindow"/> lines above.</param>
    public sealed record Site(string File, string Tag, Kind Kind, string? Anchor = null, string? Gate = null);

    private const int GateWindow = 40;

    private static readonly Site[] Declared =
    {
        // --- Loud: a failure, a refusal, or a result the user asked for. Must print by default.
        new("AlRunner/Infrastructure/AlNavNameReflection.cs", "al-locals", Kind.Loud),
        new("AlRunner/Infrastructure/InProcessAppPackager.cs", "bc-floor", Kind.Loud),
        new("AlRunner/Program.cs", "count-baseline", Kind.Loud),
        new("AlRunner/Program.cs", "count-out", Kind.Loud),
        new("AlRunner/DependencyLoader.cs", "dep-load-fail", Kind.Loud),
        new("AlRunner/Infrastructure/DependencyLoadException.cs", "dep-load-fail", Kind.Loud),
        new("AlRunner/DependencyLoader.cs", "dep-metadata", Kind.Loud),
        new("AlRunner/DependencyMetadataProducer.cs", "dep-metadata", Kind.Loud, Anchor: "cache write failed"),
        new("AlRunner/DependencyMetadataProducer.cs", "dep-metadata-fail", Kind.Loud),
        new("AlRunner/InstallTriggerRunner.cs", "install-trigger", Kind.Loud),
        new("AlRunner/Patches/ApplicationObjectBasePatches.cs", "oos-in-try", Kind.Loud),
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "page-control-field", Kind.Loud),
        new("AlRunner/Infrastructure/PhaseLog.cs", "phase-log", Kind.Loud),
        new("AlRunner/Infrastructure/ProvisioningCheck.cs", "provision-gap", Kind.Loud),
        new("AlRunner/Patches/RecordPatches.ReportRowFromBcDocument.cs", "report-metadata", Kind.Loud),
        new("AlRunner/Infrastructure/ServiceTierDllIndex.cs", "servicetier-dll", Kind.Loud, Anchor: "failed to load"),
        new("AlRunner/Infrastructure/ServiceTierDllIndex.cs", "servicetier-dll", Kind.Loud, Anchor: "index skip"),
        // [source-dep] is the source-sibling twin of the exempt [layered] progress lines, and
        // CacheRootsIsolationTests asserts `[source-dep] WROTE` in a default-verbosity run.
        new("AlRunner/ProgramSupport/SiblingCompile.cs", "source-dep", Kind.Loud),
        new("AlRunner/Infrastructure/AlCoverageSourceMap.cs", "source-map", Kind.Loud),
        new("AlRunner/Infrastructure/MissingTestDataDiagnosis.cs", "test-data", Kind.Loud),
        new("AlRunner/Infrastructure/TestDataNormalization.cs", "test-data", Kind.Loud),
        new("AlRunner/TestDataProvisioner.cs", "test-data", Kind.Loud),
        new("AlRunner/TestExecutor.cs", "test-exec", Kind.Loud),
        // Unlike the two gated siblings below, these three mean event subscribers were NOT registered.
        new("AlRunner/Infrastructure/AssemblyTypeIndex.cs", "type-index", Kind.Loud, Anchor: "cannot be registered"),
        new("AlRunner/Infrastructure/AssemblyTypeIndex.cs", "type-index", Kind.Loud, Anchor: "GetMethods failed"),
        new("AlRunner/Infrastructure/AssemblyTypeIndex.cs", "type-index", Kind.Loud, Anchor: "no matching declared MethodInfo"),

        // --- OptIn: printed only when a switch the user set asks for it.
        new("AlRunner/BcCompiler.cs", "BcCompiler-diag", Kind.OptIn, Gate: "BCCOMPILER_DIAG"),
        new("AlRunner/BcCompiler.cs", "DIAG-RETRY", Kind.OptIn, Gate: "AL_RUNNER_DIAG_EMITRETRY"),
        new("AlRunner/Program.cs", "FCE-NRE", Kind.OptIn, Gate: "AL_RUNNER_TRACE_NRE"),
        new("AlRunner/Patches/RecordPatches.AggregatePermissionSetVirtualTable.cs", "aggregate-permission-set", Kind.OptIn, Gate: "AL_RUNNER_TRACE_AGGREGATE_PERMISSION_SET"),
        new("AlRunner/Patches/RecordPatches.AllProfileVirtualTable.cs", "all-profile", Kind.OptIn, Gate: "AL_RUNNER_TRACE_ALL_PROFILE"),
        new("AlRunner/Infrastructure/AlDapSession.cs", "dap-step-trace", Kind.OptIn, Gate: "_traceEnabled"),
        new("AlRunner/DependencyMetadataProducer.cs", "dep-metadata", Kind.OptIn, Anchor: "{message}", Gate: "AL_RUNNER_TRACE_DEP_METADATA"),
        new("AlRunner/BcAssembler.cs", "emit-timing", Kind.OptIn, Gate: "timing"),
        new("AlRunner/BcCompiler.cs", "emit-timing", Kind.OptIn, Gate: "BCCOMPILER_TIMING"),
        new("AlRunner/BcCompiler.Incremental.cs", "emit-timing", Kind.OptIn, Gate: "BCCOMPILER_TIMING"),
        new("AlRunner/Program.cs", "first-chance", Kind.OptIn, Gate: "fcFilter"),
        new("AlRunner/Infrastructure/JmpHook.cs", "hook-audit", Kind.OptIn, Gate: "_audit"),
        new("AlRunner/Program.cs", "instrumentation-counters", Kind.OptIn, Gate: "AL_RUNNER_DUMP_INSTRUMENTATION_COUNTERS"),
        new("AlRunner/MemoryCensus.cs", "mem-census", Kind.OptIn, Gate: "Enabled"),
        new("AlRunner/Patches/ObjectMetadataRegistry.cs", "object-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_OBJECT_METADATA"),
        new("AlRunner/Patches/RunnerPageInstance.cs", "option-captions", Kind.OptIn, Gate: "AL_RUNNER_TRACE_PAGE_METADATA"),
        new("AlRunner/Patches/PageMetadataRegistry.cs", "page-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_PAGE_METADATA"),
        new("AlRunner/Patches/RecordPatches.PageControlFieldFromBcDocument.cs", "page-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_PAGE_METADATA_SOURCE"),
        new("AlRunner/Patches/RecordPatches.RealPageMetadata.cs", "page-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_PAGE_METADATA"),
        new("AlRunner/Patches/RecordPatches.PageTriggerMetadata.cs", "page-trigger-audit", Kind.OptIn, Gate: "Enabled"),
        new("AlRunner/Patches/RecordPatches.TableTriggerMetadata.cs", "table-trigger-audit", Kind.OptIn, Gate: "Enabled"),
        new("AlRunner/Patches/RecordPatches.cs", "parse-counts", Kind.OptIn, Gate: "AL_RUNNER_TRACE_PARSE_COUNTS"),
        new("AlRunner/Patches/RecordPatches.PermissionMetadataPopulator.cs", "perm-metadata", Kind.OptIn, Gate: "AL_RUNNER_DIAG_PERMMETA"),
        new("AlRunner/Patches/RecordPatches.PermissionSetFromBcDocument.cs", "perm-metadata", Kind.OptIn, Gate: "AL_RUNNER_DIAG_PERMMETA"),
        new("AlRunner/Patches/RecordPatches.PermissionSystemTable.cs", "permission-table", Kind.OptIn, Gate: "AL_RUNNER_TRACE_PERMISSION_TABLE"),
        new("AlRunner/Patches/RecordPatches.MetaQueryFromBcDocument.cs", "query-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_QUERY_METADATA_SOURCE"),
        new("AlRunner/Patches/RecordPatches.ReportMetadataVirtualTable.cs", "report-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_REPORT_METADATA"),
        new("AlRunner/BcCompiler.cs", "shared-refs", Kind.OptIn, Gate: "BCCOMPILER_TIMING"),
        new("AlRunner/Patches/RecordPatches.NclMetaTableFromBcDocument.cs", "table-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_TABLE_METADATA_SOURCE"),
        new("AlRunner/Patches/RecordPatches.TableMetadataVirtualTable.cs", "table-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_TABLE_METADATA"),
        new("AlRunner/Patches/RecordPatches.RealXmlPortMetadata.cs", "xmlport-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_XMLPORT_METADATA"),
        new("AlRunner/Patches/XmlPortMetadataRegistry.cs", "xmlport-metadata", Kind.OptIn, Gate: "AL_RUNNER_TRACE_XMLPORT_METADATA"),

        // --- VerboseGated: internal chatter on a healthy run. Hidden by its own call-site gate.
        new("AlRunner/Infrastructure/AssemblyTypeIndex.cs", "type-index", Kind.VerboseGated, Anchor: "falling back to Assembly.GetTypes()"),
        new("AlRunner/Infrastructure/ServiceTierDllIndex.cs", "servicetier-dll", Kind.VerboseGated, Anchor: "indexed "),
    };

    /// <summary>A hyphenated-tag string literal found in production source.</summary>
    private sealed record Found(string File, string Tag, int Line, string[] Lines, string Rendered);

    private static readonly Regex HyphenTagLiteral = new(
        "(?:\\$@|@\\$|\\$|@)?\"(?<lit>\\[(?<tag>[A-Za-z][A-Za-z0-9._+]*-[A-Za-z0-9._+-]*)\\](?:[^\"\\\\]|\\\\.)*)\"",
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
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in HyphenTagLiteral.Matches(lines[i]))
                {
                    var rendered = Regex.Replace(m.Groups["lit"].Value, @"\{[^{}]*\}", "<value>");
                    found.Add(new Found(rel, m.Groups["tag"].Value, i, lines, rendered));
                }
            }
        }
        // Guard against a scan that silently matched nothing (moved tree, broken pattern).
        Assert.True(found.Count >= 50, $"scan found only {found.Count} hyphenated-tag literals; expected ~100");
        return found;
    }

    private static IEnumerable<Site> Matching(Found f) => Declared.Where(d =>
        d.File == f.File && d.Tag == f.Tag &&
        (d.Anchor == null || Window(f, 3, 3).Any(l => l.Contains(d.Anchor, StringComparison.Ordinal))));

    private static IEnumerable<string> Window(Found f, int before, int after) =>
        f.Lines.Skip(Math.Max(0, f.Line - before)).Take(Math.Min(f.Line, before) + 1 + after);

    /// <summary>Sites in one (file, tag) group with anchors win over an anchor-less entry.</summary>
    private static Site? Resolve(Found f)
    {
        var hits = Matching(f).ToList();
        var anchored = hits.Where(h => h.Anchor != null).ToList();
        if (anchored.Count == 1) return anchored[0];
        return anchored.Count == 0 && hits.Count == 1 ? hits[0] : null;
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

    [Fact]
    public void EveryHyphenatedTagSite_IsClassified_AndNoDeclarationIsStale()
    {
        var found = ScanProductionSource();
        var unclassified = found.Where(f => Resolve(f) == null)
            .Select(f => $"{f.File}:{f.Line + 1} [{f.Tag}] ({Matching(f).Count()} matching declaration(s))")
            .ToList();
        Assert.True(unclassified.Count == 0,
            "hyphenated-tag call sites need a visibility decision in Declared (Loud / OptIn / VerboseGated):\n  "
            + string.Join("\n  ", unclassified));

        var used = found.Select(Resolve).Where(s => s != null).ToHashSet();
        var stale = Declared.Where(d => !used.Contains(d)).Select(d => $"{d.File} [{d.Tag}] {d.Anchor}").ToList();
        Assert.True(stale.Count == 0, "declarations matching no call site:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>Loud and OptIn lines must reach the terminal without --verbose, read from the real call site.</summary>
    [Fact]
    public void LoudAndOptInSites_SurviveTheDefaultFilter()
    {
        var eaten = ScanProductionSource()
            .Where(f => Resolve(f) is { Kind: Kind.Loud or Kind.OptIn })
            .Where(f => !FilterOnce(f.Rendered, verbose: false).Contains(f.Rendered, StringComparison.Ordinal))
            .Select(f => $"{f.File}:{f.Line + 1} {f.Rendered}")
            .ToList();
        Assert.True(eaten.Count == 0,
            "the default filter drops lines whose visibility their call site already decided:\n  "
            + string.Join("\n  ", eaten));
    }

    [Fact]
    public void OptInSites_AreBehindTheirSwitch()
    {
        var ungated = ScanProductionSource()
            .Select(f => (f, site: Resolve(f)))
            .Where(x => x.site is { Kind: Kind.OptIn })
            .Where(x => !Window(x.f, GateWindow, 0).Any(l => l.Contains(x.site!.Gate!, StringComparison.Ordinal)))
            .Select(x => $"{x.f.File}:{x.f.Line + 1} [{x.f.Tag}] expected gate '{x.site!.Gate}'")
            .ToList();
        Assert.True(ungated.Count == 0,
            "an OptIn trace lost its switch and now prints on every run:\n  " + string.Join("\n  ", ungated));
    }

    [Fact]
    public void VerboseGatedSites_CarryAnExplicitLogVerboseGate()
    {
        var sites = ScanProductionSource().Where(f => Resolve(f) is { Kind: Kind.VerboseGated }).ToList();
        Assert.True(sites.Count >= 3, $"expected at least 3 VerboseGated sites, found {sites.Count}");
        var ungated = sites
            .Where(f => !Window(f, 3, 0).Any(l =>
                !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains("Log.Verbose", StringComparison.Ordinal)))
            .Select(f => $"{f.File}:{f.Line + 1} {f.Rendered}")
            .ToList();
        Assert.True(ungated.Count == 0,
            "internal chatter with a hyphenated tag prints at default verbosity unless its call site gates it:\n  "
            + string.Join("\n  ", ungated));
    }

    /// <summary>The four tag shapes the filter distinguishes (#2257, point 2).</summary>
    [Theory]
    [InlineData("[Cecil] rewriting NavDialog", false)]            // plain: suppressed
    [InlineData("[Foo.Bar] dotted component", false)]             // dotted: suppressed
    [InlineData("[dep-load-fail] X v1: EMIT-FAIL — boom", true)]  // hyphenated: call site decides
    [InlineData("[bc] selected BC 28.1", true)]                   // exemption list
    public void TagShapes_DefaultVerbosity(string line, bool visible)
    {
        Assert.Equal(visible, FilterOnce(line, verbose: false).Contains(line, StringComparison.Ordinal));
        Assert.Contains(line, FilterOnce(line, verbose: true));
    }
}
