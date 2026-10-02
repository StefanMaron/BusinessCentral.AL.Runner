// BcCompiler.DotNetRefPacks — saying out loud that the .NET reference packs are missing (#5134).
//
// BC's compiler binds every `DotNet` alias through the directories EnumerateDotNetRefAssemblyDirs
// yields. A runtime-only install has no packs/, so that yields nothing, and each object using an
// alias then fails AL0185 "DotNet '<type>' is missing" and is dropped — with a diagnostic that
// points at the AL, not at the .NET install. Everything the runner reports about the drop is
// true; what was missing is the cause, so this file supplies it. It never changes what binds.
// see docs/limitations.md#dotnet-reference-packs
namespace AlRunner;

public sealed partial class BcCompiler
{
    /// <summary>
    /// What is wrong with the reference packs under one dotnet root. <see cref="Warning"/> is the
    /// text printed once before compiling; <see cref="AliasesCannotBind"/> says whether an AL0185
    /// "DotNet '…' is missing" drop is explained by it (false when a pack exists but is for another
    /// major, which fails differently — AL0133 — rather than leaving the alias unbound).
    /// </summary>
    internal sealed record DotNetRefPackGap(string Warning, bool AliasesCannotBind, string Cause);

    private static readonly Lazy<DotNetRefPackGap?> _runningDotNetRefPackGap = new(
        () => DiagnoseDotNetRefPacks(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            Environment.GetEnvironmentVariable("DOTNET_ROOT")));

    /// <summary>The gap on the dotnet install this process is running on; null when none.</summary>
    internal static DotNetRefPackGap? RunningDotNetRefPackGap => _runningDotNetRefPackGap.Value;

    private const string RefPackFix =
        "Fix: install the .NET SDK (it ships packs/), or unpack the microsoft.netcore.app.ref and "
        + "netstandard.library.ref NuGet packages under <dotnet root>/packs/.";

    /// <summary>
    /// CLAIM: the packs the enumeration reads are absent, or present only for another .NET major.
    /// Judged by the same <see cref="ResolveDotNetRoot"/> / <see cref="SelectCoreRefPack"/> /
    /// <see cref="SelectNetStandardRefPack"/> the enumeration uses, so "what was diagnosed" and
    /// "what was probed" are the same directories (pinned per pack by DotNetRefPackDiagnosisTests).
    /// Null when the running major's core pack and the netstandard pack are both usable.
    ///
    /// TRAP: a pack directory without a <c>ref/&lt;tfm&gt;</c> subdirectory yields nothing to
    /// probe, so it counts as missing — an existing but empty <c>packs/…</c> folder is not "fine".
    /// </summary>
    internal static DotNetRefPackGap? DiagnoseDotNetRefPacks(string runtimeDir, string? envDotnetRoot)
    {
        var major = RunningRuntimeMajor(runtimeDir);
        var majorText = major?.ToString() ?? "?";
        var root = ResolveDotNetRoot(runtimeDir, envDotnetRoot);
        if (root == null)
            return new DotNetRefPackGap(
                $"[dotnet-ref-packs] Could not locate the dotnet root (runtime directory '{runtimeDir}', "
                + $"DOTNET_ROOT '{envDotnetRoot ?? "<unset>"}'), so the .NET reference packs cannot be read. "
                + "`DotNet` aliases that bind through them will fail AL0185 and the objects using one "
                + "are dropped. " + RefPackFix,
                AliasesCannotBind: true,
                Cause: "the dotnet root could not be located, so the .NET reference packs cannot be read");

        var packs = Path.Combine(root, "packs");
        var coreRef = Path.Combine(packs, "Microsoft.NETCore.App.Ref");
        var problems = new List<string>();
        var aliasesCannotBind = false;

        var core = Directory.Exists(coreRef) ? SelectCoreRefPack(coreRef, major) : null;
        if (core == null || !HasRefTfmDir(core.Value.Dir))
        {
            problems.Add($"Microsoft.NETCore.App.Ref for .NET {majorText} is not installed ('{coreRef}' "
                + (Directory.Exists(coreRef) ? "has no usable version" : "does not exist") + ")");
            aliasesCannotBind = true;
        }
        else if (!core.Value.MajorMatches)
        {
            problems.Add($"Microsoft.NETCore.App.Ref has no .NET {majorText} version under '{coreRef}'; the "
                + $"runner falls back to '{Path.GetFileName(core.Value.Dir)}', whose System.Runtime does not "
                + "match the 8.0.0.0 BC's Ncl.dll references, so types such as System.Uri can fail to convert (AL0133)");
        }

        var nsRef = Path.Combine(packs, "NETStandard.Library.Ref");
        // The directory the enumeration reads (highest version), not "any version": a lower
        // version with a ref/ folder is never probed, so it must not make this read as usable.
        var nsBest = Directory.Exists(nsRef) ? SelectNetStandardRefPack(nsRef) : null;
        var nsUsable = nsBest != null && HasRefTfmDir(nsBest);
        if (!nsUsable)
        {
            problems.Add($"NETStandard.Library.Ref is not installed ('{nsRef}' "
                + (Directory.Exists(nsRef) ? "has no usable version" : "does not exist") + ")");
            aliasesCannotBind = true;
        }

        if (problems.Count == 0) return null;
        return new DotNetRefPackGap(
            $"[dotnet-ref-packs] Searched the dotnet root '{root}': " + string.Join("; ", problems) + ". "
            + (aliasesCannotBind
                ? "Without them every `DotNet` alias that resolves through them fails AL0185 "
                  + "(\"DotNet '<type>' is missing\") and the objects using one are dropped, so the run "
                  + "reports fewer tests passing for a reason the AL does not show. "
                : "")
            + RefPackFix.Replace("<dotnet root>", root),
            aliasesCannotBind,
            Cause: $"the .NET reference packs are missing or incomplete under '{Path.Combine(root, "packs")}'");
    }

    private static bool HasRefTfmDir(string versionDir)
    {
        var refSub = Path.Combine(versionDir, "ref");
        return Directory.Exists(refSub) && Directory.EnumerateDirectories(refSub).Any();
    }

    /// <summary>
    /// The cause to append to a report of dropped objects whose diagnostics include an AL0185
    /// "DotNet '…' is missing", or null. Only when the packs are really missing
    /// (<see cref="DotNetRefPackGap.AliasesCannotBind"/>) and only for a DotNet AL0185: any other
    /// diagnostic stays unattributed, because an unrecognised drop may well be the AL's own.
    /// </summary>
    internal static string? AttributeToMissingDotNetRefPack(
        IReadOnlyList<string>? excludedDiagnostics, DotNetRefPackGap? gap)
    {
        if (gap is not { AliasesCannotBind: true } || excludedDiagnostics == null) return null;
        foreach (var d in excludedDiagnostics)
            if (d != null && d.Contains("AL0185", StringComparison.Ordinal)
                && d.Contains("DotNet '", StringComparison.Ordinal)
                // A type no packs can supply keeps its own #3890 attribution; claiming the packs
                // for it would contradict "permanent and expected here".
                && !DependencyLoader.UnobtainableDotNetTypes.Any(t => d.Contains($"'{t}'", StringComparison.Ordinal)))
                return "Probable cause, not the AL: " + gap.Cause + ". Install the .NET SDK, or unpack "
                    + "microsoft.netcore.app.ref and netstandard.library.ref under <dotnet root>/packs/ "
                    + "(the [dotnet-ref-packs] line printed before compiling has the full account).";
        return null;
    }

    internal static string? AttributeToMissingDotNetRefPack(IReadOnlyList<string>? excludedDiagnostics)
        => AttributeToMissingDotNetRefPack(excludedDiagnostics, RunningDotNetRefPackGap);

    /// <summary>
    /// Print the gap before the first compile. Called from <c>GetOrCreateDotNetFactory</c>, which
    /// builds the factory once per process under a lock, so that is the once-guard. Stderr and
    /// unconditional rather than through <c>ProvisionGapLog</c>: that log is reset per bundle, and
    /// a once-per-process line would then reach only the first bundle's summary.
    /// </summary>
    private static void WarnOnceIfDotNetRefPacksMissing()
    {
        var gap = RunningDotNetRefPackGap;
        if (gap != null)
            Console.Error.WriteLine(gap.Warning);
    }

    /// <summary>
    /// The compile-cache key term for which reference-pack directories BC's binder is given.
    /// CLAIM: a source dependency compiled without packs is a different (partial) output from one
    /// compiled with them, so the key must change when packs appear — otherwise a persisted entry
    /// compiled on a runtime-only install is replayed, with its "packs missing" report, after the
    /// user installs the SDK. Root-independent (pack/version/tfm only), so two machines with the
    /// same packs in different places share entries. Built from the enumeration itself.
    /// </summary>
    internal static string DotNetRefPackCacheTerm(string runtimeDir, string? envDotnetRoot)
        => DotNetRefPackCacheTerm(EnumerateDotNetRefAssemblyDirsUnder(runtimeDir, envDotnetRoot));

    /// <summary>
    /// Sorted: the enumeration yields directories in filesystem order (probe order, which matters
    /// there), and the key must not depend on the order a filesystem happens to list siblings.
    /// </summary>
    internal static string DotNetRefPackCacheTerm(IEnumerable<string> probedDirs)
    {
        var dirs = probedDirs
            .Select(d => string.Join("/", d.Replace('\\', '/').TrimEnd('/').Split('/').TakeLast(4)))
            .OrderBy(d => d, StringComparer.Ordinal);
        return "refpacks:" + string.Join(",", dirs);
    }

    private static readonly Lazy<string> _runningRefPackCacheTerm = new(
        () => DotNetRefPackCacheTerm(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            Environment.GetEnvironmentVariable("DOTNET_ROOT")));

    /// <summary>The term for the install this process runs on (fixed for the process, like the probing paths).</summary>
    internal static string RunningDotNetRefPackCacheTerm => _runningRefPackCacheTerm.Value;
}
