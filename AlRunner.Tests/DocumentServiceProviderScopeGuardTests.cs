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
/// #3153 — this file used to exclude its own source from the scan below (<c>SelfSourcePath</c>),
/// because it necessarily quotes the very identifiers and literals it forbids. That exclusion
/// was a hole: the one file never checked was the one whose author is most likely to be
/// experimenting with the contract it forbids. Fixed the same way #3064 fixed
/// <c>BaseAppFloorFixtureGuardTests</c> — reusing <see cref="CSharpSource.CodeOnly"/>
/// rather than a second hand-rolled comment stripper — with one refinement <c>DeclaresADocumentServiceHandler</c>
/// needs: it blanks string-literal content too (<c>blankStringContents: true</c>), because the
/// contract this half checks is IDENTIFIERS IN CODE (implementing an interface, applying an
/// attribute), never string content, so this file's own regex pattern — a string literal that
/// spells both names verbatim — disappears with the rest of the string text instead of needing
/// its own exclusion. The copied-error-text half stays string-content-sensitive
/// (<c>blankStringContents: false</c>) because a copied literal IS its subject, so its own regex
/// pattern is split across two concatenated C# literals below so no single token spells the full
/// phrase — the same "compose it, don't spell it" trick <c>BaseAppFloorFixtureGuardTests</c> uses
/// for its synthetic fixtures.
/// </summary>
public sealed class DocumentServiceProviderScopeGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>MEF's discovery glob, as a regex over a bare file name.</summary>
    private static readonly Regex MefProviderFileName = new(
        @"^.*\.nav\..*DocumentService.*\.dll$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The two halves of the MEF export contract a provider must declare to be discoverable.
    /// Matched against code with string-literal content blanked (see
    /// <see cref="DeclaresADocumentServiceHandler"/>), so this pattern's own text — which spells
    /// both names verbatim — is never itself a match.
    /// </summary>
    private static readonly Regex HandlerContract = new(
        @"IDocumentServiceHandler|DocumentServiceMetadata",
        RegexOptions.Compiled);

    /// <summary>
    /// The literal prefix of every error string Microsoft's mock produces, which the test
    /// codeunit copies verbatim into its expectations. Reproducing it in runner code is the
    /// sharpest form of the harm this guard exists to prevent. Built from two literals — never
    /// one spelling the full phrase — because <see cref="CopiesTheMockErrorLiteral"/> keeps
    /// string content, so a single contiguous literal here would flag this very field.
    /// </summary>
    private static readonly Regex CopiedMockLiteral = new(
        "DocumentServiceMock" + " says",
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

    /// <summary>
    /// True when <paramref name="sourceText"/> declares (implements, or applies the metadata
    /// attribute for) the MEF handler contract — never a comment or string mentioning the names,
    /// because those cannot make BC's <c>DirectoryCatalog</c> discover anything (#3153).
    /// </summary>
    internal static bool DeclaresADocumentServiceHandler(string sourceText) =>
        HandlerContract.IsMatch(
            CSharpSource.CodeOnly(sourceText));

    /// <summary>
    /// True when <paramref name="sourceText"/> reproduces Microsoft's mock error text inside a
    /// string literal — a comment mentioning the phrase is not a copy, so only comments are
    /// stripped; string content is kept, since it is the subject (#3153).
    /// </summary>
    internal static bool CopiesTheMockErrorLiteral(string sourceText) =>
        CopiedMockLiteral.IsMatch(
            CSharpSource.CommentsBlanked(sourceText));

    [Fact]
    public void NoRunnerSource_DeclaresADocumentServiceHandler()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var path in RunnerSourceFiles())
        {
            scanned++;
            if (DeclaresADocumentServiceHandler(File.ReadAllText(path)))
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
            if (CopiesTheMockErrorLiteral(File.ReadAllText(path)))
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

    // ── #3153: what counts as DECLARING the contract, versus merely spelling it ───────────
    //
    // These facts drive DeclaresADocumentServiceHandler / CopiesTheMockErrorLiteral directly on
    // synthetic content, mirroring #3064's ThePlaceholderExpansion-style controls in
    // BaseAppFloorFixtureGuardTests: the two scanning facts above can only ever assert about the
    // sources checked in today, not about a shape nobody has written yet — including the shape
    // this very file's own regex definitions are.

    [Fact]
    public void AProseCommentNamingTheHandlerContract_IsNotADeclaration()
    {
        var src = """"
            public class Fixture
            {
                // Not a real reference: IDocumentServiceHandler is only named here.
                /* Nor DocumentServiceMetadata in a block comment. */
                /// <summary>Nor <c>IDocumentServiceHandler</c> in a doc comment.</summary>
                public void Noop() { }
            }
            """";

        Assert.False(DeclaresADocumentServiceHandler(src),
            "a comment naming the contract declares nothing: MEF's DirectoryCatalog never " +
            "reads a comment, so no provider becomes discoverable because of it.");
    }

    /// <summary>
    /// The control that stops the fix above from being a weakening: an actual implementation
    /// must still be caught. Green before AND after, which is what makes the RED case above
    /// believable rather than a matcher that returns false unconditionally.
    /// </summary>
    [Fact]
    public void AnActualImplementation_IsADeclaration()
    {
        var src = """"
            public interface IDocumentServiceHandler { }

            public sealed class MyProvider : IDocumentServiceHandler { }
            """";

        Assert.True(DeclaresADocumentServiceHandler(src));
    }

    /// <summary>And the metadata-attribute half of the contract, on its own.</summary>
    [Fact]
    public void AnAppliedMetadataAttribute_IsADeclaration()
    {
        var src = """"
            [DocumentServiceMetadata(typeof(object))]
            public sealed class MyProvider { }
            """";

        Assert.True(DeclaresADocumentServiceHandler(src));
    }

    /// <summary>
    /// The defect this issue reports, reproduced directly: this guard's OWN regex pattern is a
    /// string literal spelling both contract names verbatim. It must not read as a declaration —
    /// that is what lets <see cref="NoRunnerSource_DeclaresADocumentServiceHandler"/> scan this
    /// file's own source without an exclusion.
    /// </summary>
    [Fact]
    public void ARegexPatternLiteralSpellingTheContract_IsNotADeclaration()
    {
        var src = """"
            public class Fixture
            {
                private static readonly System.Text.RegularExpressions.Regex R =
                    new(@"IDocumentServiceHandler|DocumentServiceMetadata");
            }
            """";

        Assert.False(DeclaresADocumentServiceHandler(src));
    }

    /// <summary>
    /// The scan below (<see cref="NoRunnerSource_CopiesTheMockErrorLiterals"/>) keeps string
    /// content, so this file's OWN fixtures must never spell the mock phrase contiguously either
    /// — the same obligation <see cref="CopiedMockLiteral"/> carries for its own field two
    /// literals above. Composed at runtime, exactly like <c>BaseAppFloorFixtureGuardTests.Sub</c>
    /// composes <c>"application":</c> for the same reason (#3064, #3153).
    /// </summary>
    private const string MockPhrase = "DocumentServiceMock" + " says";

    private static string SubPhrase(string template) => template.Replace("PHRASE", MockPhrase);

    /// <summary>
    /// Anchors the composition against reality, exactly like
    /// <c>BaseAppFloorFixtureGuardTests.ThePlaceholderExpansion_MatchesARealAllowlistedManifest</c>:
    /// without this, every fixture below could agree with a matcher that does not actually
    /// recognize the phrase it claims to guard against.
    /// </summary>
    [Fact]
    public void TheComposedPhrase_MatchesTheGuardsOwnPattern() =>
        Assert.True(CopiedMockLiteral.IsMatch(MockPhrase));

    [Fact]
    public void AProseCommentNamingTheMockLiteral_IsNotACopy()
    {
        var src = SubPhrase(""""
            public class Fixture
            {
                // Never copy "PHRASE" into an Assert expectation.
                /// <summary>Forbids reproducing <c>PHRASE</c> anywhere.</summary>
                public void Noop() { }
            }
            """");

        Assert.False(CopiesTheMockErrorLiteral(src),
            "a comment naming the phrase copies nothing: Microsoft's test codeunit never reads " +
            "a comment, so no assertion is graded against it because of one.");
    }

    /// <summary>The control: an actual copy into a string literal must still be caught.</summary>
    [Fact]
    public void AStringLiteralCopyingTheMockText_IsACopy()
    {
        var src = SubPhrase(""""
            public class Fixture
            {
                public void T() => Assert.ExpectedError("PHRASE: boom");
            }
            """");

        Assert.True(CopiesTheMockErrorLiteral(src));
    }

    /// <summary>
    /// This guard's own <see cref="CopiedMockLiteral"/> field is exactly this shape once its two
    /// literal fragments are concatenated back into one string at compile time — the fixture
    /// below reproduces the same split, and must still read as a non-copy in RAW SOURCE TEXT,
    /// because no single token spells the full phrase.
    /// </summary>
    [Fact]
    public void ARegexPatternBuiltFromTwoLiteralFragments_IsNotACopy()
    {
        var src = """"
            public class Fixture
            {
                private static readonly System.Text.RegularExpressions.Regex R =
                    new("DocumentServiceMock" + " says");
            }
            """";

        Assert.False(CopiesTheMockErrorLiteral(src));
    }
}
