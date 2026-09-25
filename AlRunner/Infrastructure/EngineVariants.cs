namespace AlRunner.Infrastructure;

/// <summary>
/// Discovers and selects per-BC-minor engine variants shipped in the packed tool
/// (issue #2024 item 3 / #2027). BC-free by construction — this runs before
/// <see cref="BcArtifacts.SelectVersion"/> even completes, let alone before any BC type
/// is touched, so it may only use <c>System.*</c>.
///
/// <para><b>Layout.</b> A packed install carries <c>&lt;packageRoot&gt;/variants/&lt;full-build-version&gt;/</c>
/// — one directory per <c>.github/bc-versions.txt</c> entry, each holding that build's
/// own <c>al-runner.dll</c>/<c>.pdb</c>/<c>.deps.json</c>/<c>.runtimeconfig.json</c> (no
/// native apphost — variants are only ever entered via <c>dotnet exec</c> through
/// <see cref="NclShadowRuntime"/>'s re-exec, never launched directly). A plain
/// <c>dotnet build</c>/<c>dotnet run</c> dev checkout has no <c>variants/</c> directory at
/// all — <see cref="Discover"/> returns empty, and every caller must treat that as "no
/// variants shipped, behave exactly as the single-build runner always has," not as an
/// error.</para>
/// </summary>
public static class EngineVariants
{
    public const string VariantsDirName = "variants";
    public const string EntryAssemblyFileName = "al-runner.dll";

    public sealed record Variant(Version BuildVersion, string Dir)
    {
        public string EntryAssemblyPath => Path.Combine(Dir, EntryAssemblyFileName);
    }

    /// <summary>
    /// Every variant shipped alongside <paramref name="baseDirectory"/> (normally
    /// <c>AppContext.BaseDirectory</c>). Empty — never throws — when there is no
    /// <c>variants/</c> directory, or it's empty, or malformed entries are found (a
    /// non-version directory name, or a version directory missing its own
    /// <c>al-runner.dll</c>, is silently skipped rather than failing discovery for the
    /// variants that ARE well-formed).
    /// </summary>
    public static IReadOnlyList<Variant> Discover(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, VariantsDirName);
        if (!Directory.Exists(root)) return Array.Empty<Variant>();

        var list = new List<Variant>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!Version.TryParse(name, out var v)) continue;
            if (!File.Exists(Path.Combine(dir, EntryAssemblyFileName))) continue;
            list.Add(new Variant(v, dir));
        }
        return list;
    }

    /// <summary>
    /// Best-matching shipped variant for <paramref name="targetVersion"/> (the SELECTED
    /// BC artifact version — see <see cref="BcArtifacts.SelectedVersion"/>), in
    /// descending order of tightness:
    /// <list type="number">
    ///   <item>exact 4-part build match — the only tier immune to the
    ///     <c>Microsoft.Dynamics.Nav.CodeAnalysis</c> per-BUILD strong-name skew (see
    ///     <see cref="BcArtifacts.DefaultVersionPrefix"/> for the same lesson one level
    ///     down); returned with <c>Degraded: false</c>.</item>
    ///   <item>same major.minor, different build — a KNOWN-DEGRADED but usually-survivable
    ///     configuration (the shipped variant's compiled-against
    ///     <c>Microsoft.Dynamics.Nav.CodeAnalysis</c> may not strong-name-match the
    ///     selected artifact's own copy); returned with <c>Degraded: true</c>.</item>
    /// </list>
    /// Never falls back across MAJOR or MINOR — <c>.claude/rules/loud-failures.md</c> /
    /// #2020 is explicit that silently landing on a nearby minor is the bug this whole
    /// mechanism exists to retire. A caller seeing <c>null</c> must fail loud, naming
    /// <paramref name="targetVersion"/> and <paramref name="variants"/>, never guess.
    /// </summary>
    public static (Variant Variant, bool Degraded)? SelectBestMatch(
        IReadOnlyList<Variant> variants, Version targetVersion)
    {
        foreach (var v in variants)
            if (v.BuildVersion == targetVersion)
                return (v, false);

        foreach (var v in variants)
            if (v.BuildVersion.Major == targetVersion.Major && v.BuildVersion.Minor == targetVersion.Minor)
                return (v, true);

        return null;
    }

    public enum ResolutionKind
    {
        /// <summary>No variants/ directory: a single-build install, proceed in place.</summary>
        NoVariantsShipped,
        /// <summary>The best-matching variant is the engine already running.</summary>
        RunningEngineMatches,
        /// <summary>A different variant must be entered; <see cref="Resolution.SwapDir"/> names it.</summary>
        SwapRequired,
        /// <summary>No shipped variant serves the selected version; the caller must exit 2 with
        /// <see cref="Resolution.FailureMessage"/>.</summary>
        NoneSupported,
    }

    public sealed record Resolution(
        ResolutionKind Kind, Variant? Variant, bool Degraded, string? FailureMessage, string? DegradedWarning)
    {
        public string? SwapDir => Kind == ResolutionKind.SwapRequired ? Variant!.Dir : null;
    }

    /// <summary>
    /// The one variant decision, shared by the bundle-run flow and every subcommand that
    /// dispatches before it (#2190: `--precompile` used to skip it).
    /// </summary>
    public static Resolution Resolve(IReadOnlyList<Variant> variants, Version selected, Version? runningBuild)
    {
        if (variants.Count == 0)
            return new Resolution(ResolutionKind.NoVariantsShipped, null, false, null, null);

        var match = SelectBestMatch(variants, selected);
        if (match == null)
            return new Resolution(ResolutionKind.NoneSupported, null, false,
                $"BC version selection failed: no shipped engine variant supports BC {selected} " +
                $"(major {selected.Major}). Available variants: {DescribeAvailable(variants)}. Select a " +
                $"cached BC version this install ships an engine for (--bc-version), or update al-runner.",
                null);

        var (variant, degraded) = match.Value;
        var warning = degraded
            ? $"[bc] warning: the shipped {variant.BuildVersion.Major}.{variant.BuildVersion.Minor} engine " +
              $"variant was built against {variant.BuildVersion}, not the selected {selected} — " +
              $"different BUILDS of the same minor can still fail to load " +
              $"Microsoft.Dynamics.Nav.CodeAnalysis (it's strong-named per build, not per minor). Expected: " +
              $"variants pin the newest build of a minor AT PACK TIME, so any user on a different build of " +
              $"that same minor hits this. See docs/limitations.md."
            : null;
        var kind = runningBuild != variant.BuildVersion ? ResolutionKind.SwapRequired : ResolutionKind.RunningEngineMatches;
        return new Resolution(kind, variant, degraded, null, warning);
    }

    /// <summary>Human-readable list of available variant versions, for the loud-fail message.</summary>
    public static string DescribeAvailable(IReadOnlyList<Variant> variants) =>
        variants.Count == 0 ? "(none)" : string.Join(", ", variants.Select(v => v.BuildVersion.ToString()));

    /// <summary>The shipped BC minors, ascending and distinct ("27.0, 27.3, ... 28.4").</summary>
    public static string DescribeSupportedMinors(IReadOnlyList<Variant> variants) =>
        variants.Count == 0 ? "(none)" : string.Join(", ", variants
            .Select(v => new Version(v.BuildVersion.Major, v.BuildVersion.Minor))
            .Distinct().OrderBy(v => v).Select(v => v.ToString()));

    /// <summary>What a no-flags (or bare-major) selection should target, and which cached
    /// versions it passed over because no shipped variant runs them.</summary>
    public sealed record DefaultChoice(string? Version, IReadOnlyList<string> SkippedUnsupported);

    /// <summary>
    /// The default BC version for a multi-variant install (#4557): the newest cached version a
    /// shipped variant runs, else the newest shipped variant's <c>major.minor</c> — a prefix the
    /// provisioning step resolves against the CDN, so it can only ever fetch a minor this install
    /// has an engine for. Restricted to <paramref name="major"/> when given (a bare-major
    /// <c>--bc-version</c>). <c>Version</c> is null when no variant is shipped (for that major).
    /// Trap: the CDN publishes a minor before a release ships its variant, so "newest on the CDN"
    /// and "newest cached" are both unsafe defaults — each left a fresh box exiting 2 forever.
    /// </summary>
    public static DefaultChoice ChooseDefault(
        IReadOnlyList<Variant> variants, IEnumerable<string> cachedVersionNames, int? major = null)
    {
        var inScope = variants.Where(v => major == null || v.BuildVersion.Major == major).ToList();
        if (inScope.Count == 0) return new DefaultChoice(null, Array.Empty<string>());

        var cached = cachedVersionNames
            .Select(n => (Name: n, Ver: Version.TryParse(n, out var v) ? v : null))
            .Where(t => t.Ver != null && (major == null || t.Ver.Major == major))
            .OrderByDescending(t => t.Ver)
            .ToList();

        var skipped = new List<string>();
        foreach (var (name, ver) in cached)
        {
            if (SelectBestMatch(inScope, ver!) != null)
                return new DefaultChoice(name, skipped);
            skipped.Add(name);
        }

        var newest = inScope.Max(v => v.BuildVersion)!;
        return new DefaultChoice($"{newest.Major}.{newest.Minor}", skipped);
    }

    /// <summary>
    /// The loud refusal for an explicit <c>--bc-version</c> naming a minor no shipped variant runs
    /// — returned BEFORE any download, so the user is not charged a ~340 MB fetch for a version
    /// the variant check would refuse anyway. Null when a variant runs it, when no variant is
    /// shipped (a single-build install decides for itself), or when the request is a bare major
    /// (<see cref="ChooseDefault"/> maps that one onto a supported minor instead).
    /// </summary>
    public static string? DescribeUnsupported(IReadOnlyList<Variant> variants, string requested)
    {
        if (variants.Count == 0) return null;
        var parts = requested.Trim().Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min))
            return null;
        if (variants.Any(v => v.BuildVersion.Major == maj && v.BuildVersion.Minor == min))
            return null;
        return $"BC version selection failed: this install ships no engine for BC {maj}.{min} " +
               $"(requested '{requested}'). Supported BC versions: {DescribeSupportedMinors(variants)}. " +
               $"Pass one of those with --bc-version, or omit --bc-version to use the newest supported one.";
    }
}
