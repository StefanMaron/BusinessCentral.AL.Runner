namespace AlRunner;

// #4567: a bundle whose dependency closure could not be resolved reports that cause once. The
// AL errors its compile then produces ("Codeunit 'X' is missing" for every reference into the
// unresolved app, and the emit crash that follows) are a consequence, so a default run counts
// them and --verbose lists them.
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
    /// The listing under an EMIT-ZERO / AL-DIAGNOSTIC-FAIL / COMPILE-FAIL header. After an
    /// unresolved dependency a default run gets one line in place of the list.
    /// </summary>
    internal static IReadOnlyList<string> AlDiagnosticListing(
        IReadOnlyCollection<string> diagnostics, bool dependencyUnresolved, bool verbose)
    {
        if (!dependencyUnresolved || verbose)
            return diagnostics.Select(d => $"  {d}").ToList();
        return new[]
        {
            $"  These {diagnostics.Count} error(s) follow from the unresolved dependency above; "
            + "re-run with --verbose to list them.",
        };
    }

    /// <summary>Suffix for the bundle-error entry, so the summary names the cause too.</summary>
    internal static string DependencyUnresolvedSuffix(bool dependencyUnresolved)
        => dependencyUnresolved ? ", after an unresolved dependency" : "";
}
