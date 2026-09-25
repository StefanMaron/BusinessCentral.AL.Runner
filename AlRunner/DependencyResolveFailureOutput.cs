namespace AlRunner;

// #4567: a bundle whose dependency closure could not be resolved reports that cause once. The
// AL errors of the kind that produces ("Codeunit 'X' is missing" for every reference into the
// unresolved app, and the emit crash that follows) are counted on a default run and listed under
// --verbose; any other AL error is listed either way.
internal static class DependencyResolveFailureOutput
{
    /// <summary>
    /// The lines printed when resolving a bundle's dependencies failed. A provisioning or
    /// version gap prints its own plain-words explanation (what is needed, what was found, the
    /// dependency chain, how to get it); anything else prints its message.
    /// </summary>
    internal static IReadOnlyList<string> DependencyResolveFailureLines(
        string rel, Exception ex, string? bcVersion, bool verbose)
    {
        if (ex is not AlRunner.Infrastructure.IDependencyProvisioningDiagnostic diag)
            return new[] { $"  [{rel}] DEP-RESOLVE-FAIL: {ex.Message}" };

        var lines = new List<string>
        {
            $"  [{rel}] DEP-RESOLVE-FAIL: this app's dependencies could not be resolved, so it cannot compile.",
        };
        foreach (var l in diag.ToDetailedMessage(bcVersion).Split('\n'))
            lines.Add(("    " + l.TrimEnd('\r')).TrimEnd());
        // The searched directories are only in the short message.
        if (verbose)
            lines.Add($"    ({ex.Message})");
        return lines;
    }

    internal static void WriteDependencyResolveFailure(string rel, Exception ex, string? bcVersion, bool verbose)
    {
        foreach (var line in DependencyResolveFailureLines(rel, ex, bcVersion, verbose))
            Console.Error.WriteLine(line);
    }

    /// <summary>
    /// The listing under an EMIT-ZERO / AL-DIAGNOSTIC-FAIL / COMPILE-FAIL / EMIT-EXCLUDED header.
    /// After an unresolved dependency a default run hides only the diagnostics of the kind that
    /// dependency produces and counts them; every other diagnostic is still listed.
    /// </summary>
    internal static IReadOnlyList<string> AlDiagnosticListing(
        IReadOnlyCollection<string> diagnostics, bool dependencyUnresolved, bool verbose)
    {
        if (!dependencyUnresolved || verbose)
            return diagnostics.Select(d => $"  {d}").ToList();
        var lines = diagnostics.Where(d => !IsMissingDependencyKind(d)).Select(d => $"  {d}").ToList();
        var hidden = diagnostics.Count - lines.Count;
        if (hidden > 0)
            lines.Add($"  {hidden} error(s) of the kind a missing dependency produces (AL0185 \"is missing\", "
                + "emit-crash) are hidden; re-run with --verbose to list them.");
        return lines;
    }

    // A user's own misspelled object name is also AL0185, which is why the count line says
    // "of the kind" rather than "caused by".
    internal static bool IsMissingDependencyKind(string diagnostic)
        => diagnostic.StartsWith("emit-crash:", StringComparison.Ordinal)
           || (diagnostic.Contains("error AL0185:", StringComparison.Ordinal)
               && diagnostic.Contains(" is missing", StringComparison.Ordinal));

    /// <summary>
    /// Whether to print <c>[cache] NOKEY</c>. A default run skips it only when it would restate
    /// this bundle's DEP-RESOLVE-FAIL: the blocker is the dependency side's own unresolved-closure
    /// reason, not the runner fingerprint or a degraded dependency term.
    /// </summary>
    internal static bool ShouldPrintNokey(
        bool verbose, bool dependencyUnresolved, string? fingerprintReason, ProgramSupport.OrderedDependencyIds ids)
    {
        var restatesDependencyFailure = dependencyUnresolved
            && fingerprintReason == null
            && ids.UncacheableReason != null
            && ids.Terms.Count == 1
            && ids.Terms[0].StartsWith("unresolved:", StringComparison.Ordinal);
        return verbose || !restatesDependencyFailure;
    }

    /// <summary>Suffix for the bundle-error entry, so the summary names the cause too.</summary>
    internal static string DependencyUnresolvedSuffix(bool dependencyUnresolved)
        => dependencyUnresolved ? ", after an unresolved dependency" : "";
}
