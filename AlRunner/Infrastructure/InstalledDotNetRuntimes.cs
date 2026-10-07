namespace AlRunner.Infrastructure;

/// <summary>
/// The .NET runtime majors installed beside the <c>dotnet</c> muxer this process runs under.
/// An engine variant is entered by re-exec under the runtime its own runtimeconfig.json names, and
/// BC 29's engine names net10.0 while the tool itself and BC 27/28 run on net8.0 — so which
/// runtimes exist decides which BC versions this machine can run (#5382).
/// </summary>
internal static class InstalledDotNetRuntimes
{
    /// <summary>
    /// The installed <c>Microsoft.NETCore.App</c> majors, or null when they cannot be listed. Null
    /// is not "none": it is "unknown", and every caller treats unknown as runnable
    /// (<see cref="EngineVariants.Variant.RuntimeAvailable"/>). The listing is
    /// <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;</c> directory names, where root is
    /// the muxer's own install directory.
    /// </summary>
    public static IReadOnlyCollection<int>? Majors()
    {
        try
        {
            var muxer = NclShadowRuntime.FindDotnetMuxer();
            var root = Path.GetDirectoryName(muxer);
            return root == null ? null : MajorsUnder(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The majors under one install root; null when the shared framework directory is absent.</summary>
    internal static IReadOnlyCollection<int>? MajorsUnder(string dotnetRoot)
    {
        var shared = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(shared)) return null;
        var majors = new SortedSet<int>();
        foreach (var dir in Directory.EnumerateDirectories(shared))
        {
            var name = Path.GetFileName(dir);
            var dot = name.IndexOf('.');
            if (dot > 0 && int.TryParse(name[..dot], out var major)) majors.Add(major);
        }
        return majors;
    }
}
