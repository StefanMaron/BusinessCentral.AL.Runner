using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards the scope decision recorded in <c>docs/limitations.md</c> ("Document-service
/// providers"): the runner must never ship an <c>IDocumentServiceHandler</c> implementation,
/// and in particular never one answering to <c>DOCUMENTSERVICEMOCK</c>.
///
/// The mechanism, read out of <c>Microsoft.Dynamics.Nav.DocumentService.dll</c> (28.1):
/// <c>DocumentServiceFactory.Create</c> composes a MEF <c>DirectoryCatalog</c> over
/// <c>Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)</c> with the pattern
/// <c>*.nav.*DocumentService*.dll</c>, then picks the export whose
/// <c>IDocumentServiceMetadata.ServiceType</c> matches the requested type, case-insensitively.
/// So a provider is discoverable only if BOTH halves line up: an assembly FILE NAME matching
/// that glob, and an exported type carrying the metadata attribute. This guard checks both,
/// which is why it can be exact rather than approximate.
///
/// Why a guard and not just the paragraph in the doc: this exact category of violation has
/// already happened once and had to be reverted (#1502, MockImage), and issue #2493 proposed
/// doing it again as its first option. Measured on 25 cached BC artifacts spanning 26.0
/// through 28.4, <c>DOCUMENTSERVICEMOCK</c> appears in no shipped DLL and the only provider
/// present is <c>Microsoft.Dynamics.Nav.SharePointOnlineDocumentService.dll</c> — the mock is
/// a Microsoft-internal test binary, not something missing from provisioning that the runner
/// could legitimately supply.
///
/// The harm is specific, not stylistic. Of the 11 failures in Microsoft's
/// <c>Codeunit139101</c>, five assert that the handler SUCCEEDS and five assert an exact error
/// literal that Microsoft's own AL marks <c>Comment = 'Text is copied from Mock assembly.'</c>.
/// A runner-authored handler would therefore be graded against strings copied out of the test
/// that grades it — green would mean the runner agrees with itself, which is precisely what
/// <c>.claude/rules/loud-failures.md</c> and the SA scope policy exist to prevent. BC's own
/// "provider could not be found" exception is the correct, already-tested outcome; it is pinned
/// from the AL side by <c>tests/runner-extras/document-service-session-seed</c>.
///
/// This file is excluded from its own scans: it necessarily quotes the very identifiers and
/// literals it forbids, and flagging itself would train readers to ignore the result.
/// </summary>
public sealed class DocumentServiceProviderScopeGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// This file, keyed by its path relative to the repo root — not by its bare name, so a
    /// same-named file in another directory cannot inherit the exemption (#3021).
    /// </summary>
    private const string SelfSourcePath = "AlRunner.Tests/DocumentServiceProviderScopeGuardTests.cs";

    /// <summary>MEF's discovery glob, as a regex over a bare file name.</summary>
    private static readonly Regex MefProviderFileName = new(
        @"^.*\.nav\..*DocumentService.*\.dll$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The two halves of the MEF export contract a provider must declare to be discoverable.
    /// </summary>
    private static readonly Regex HandlerContract = new(
        @"IDocumentServiceHandler|DocumentServiceMetadata",
        RegexOptions.Compiled);

    /// <summary>
    /// The literal prefix of every error string Microsoft's mock produces, which the test
    /// codeunit copies verbatim into its expectations. Reproducing it in runner code is the
    /// sharpest form of the harm this guard exists to prevent.
    /// </summary>
    private static readonly Regex CopiedMockLiteral = new(
        @"DocumentServiceMock says",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The reporting key: a path relative to the repo root, '/' separated.</summary>
    private static string Rel(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static IReadOnlyList<string> RunnerSourceFiles()
    {
        var paths = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(RepoRoot, "AlRunner*"))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Build intermediates are copies of the sources already scanned. Segments, not a
                // substring, so this means the same thing as the other three source guards (#3021).
                if (Rel(path).Split('/').Any(seg => seg is "bin" or "obj")) continue;
                if (Rel(path) == SelfSourcePath) continue;
                paths.Add(path);
            }
        }

        // #3021 — non-vacuity, here rather than only in the first fact below:
        // NoRunnerSource_CopiesTheMockErrorLiterals reads the same enumeration and had no such
        // check, so an empty scan left it green while proving nothing.
        Assert.True(paths.Count > 0,
            $"expected the runner's C# sources under {RepoRoot}, found none — " +
            "the guard is not looking at anything.");

        return paths;
    }

    [Fact]
    public void NoRunnerSource_DeclaresADocumentServiceHandler()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var path in RunnerSourceFiles())
        {
            scanned++;
            if (HandlerContract.IsMatch(File.ReadAllText(path)))
                offenders.Add(Rel(path));
        }

        // Non-vacuity: a scan that found no files would pass while proving nothing.
        Assert.True(scanned > 100,
            $"expected to scan the runner's C# sources, but only {scanned} file(s) were found " +
            $"under {RepoRoot} — the guard is not looking at anything.");

        Assert.True(offenders.Count == 0,
            "These files reference the document-service handler contract, which means the runner " +
            "is shipping (or preparing to ship) its own provider. That is out of scope — see the " +
            "\"Document-service providers\" entry in docs/limitations.md and issue #2493. The " +
            "supported path is for the user to supply their own assembly; the runner must let " +
            "BC's own \"provider could not be found\" exception surface:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoRunnerSource_CopiesTheMockErrorLiterals()
    {
        var offenders = new List<string>();

        foreach (var path in RunnerSourceFiles())
        {
            if (CopiedMockLiteral.IsMatch(File.ReadAllText(path)))
                offenders.Add(Rel(path));
        }

        Assert.True(offenders.Count == 0,
            "These files reproduce error text from Microsoft's internal DocumentServiceMock " +
            "assembly. Microsoft's own test codeunit marks those labels 'Text is copied from " +
            "Mock assembly', so any runner code carrying them is being graded against strings " +
            "copied from the test that grades it:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoBuildOutput_ShipsAnAssemblyMatchingTheMefDiscoveryGlob()
    {
        // The runner's own build output is the directory a shipped provider would have to
        // land in to be discovered alongside the runner's assemblies.
        var outputDir = AppContext.BaseDirectory;
        var dlls = Directory.GetFiles(outputDir, "*.dll", SearchOption.TopDirectoryOnly);

        // Non-vacuity: the runner's output always contains its own assemblies.
        Assert.True(dlls.Length > 0,
            $"expected assemblies in the build output at {outputDir}, found none — " +
            "the guard is not looking at anything.");

        var offenders = dlls
            .Select(Path.GetFileName)
            .Where(name => name is not null && MefProviderFileName.IsMatch(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These assemblies in the runner's build output match MEF's document-service " +
            "discovery glob (*.nav.*DocumentService*.dll), so BC's DocumentServiceFactory would " +
            "load them as providers. The runner must not ship one — see docs/limitations.md " +
            "and issue #2493:\n  " + string.Join("\n  ", offenders));
    }
}
