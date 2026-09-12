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
}
