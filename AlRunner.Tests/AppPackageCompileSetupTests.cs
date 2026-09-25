// AppPackageCompileSetupTests — #3530's categories 1 and 3, and the part-naming diagnostic.
//
// TWO KINDS OF TEST HERE, DELIBERATELY
//
//   Hand-written manifests pin the SHAPES: a feature declared and omitted, a help URL present
//   and absent, a percent-encoded name, a case-folded resource. These run everywhere and are
//   where the mutation testing lives.
//
//   A REAL PACKAGE CORPUS pins that those shapes are the ones shipped packages actually have.
//   That half is the point of #3530 that a hand-written fixture cannot reach: the setup is
//   derived from an ablation across real vendor packages, and a fixture I write agrees with my
//   own model of them by construction. It asserts on the PRODUCT — a non-empty declaration, a
//   resolvable resource, a decoded entry name — never on "nothing threw", because emit is
//   atomic per module and a broken setup yields exactly zero (#3530).
//
//   The corpus half SKIPS when no packages are present and FAILS when they are missing on CI —
//   see SkipIfNoCorpus. A dev box without artifacts is an honest skip; a CI leg, where the
//   workflow downloads platform and test apps before `dotnet test`, has them by construction, so
//   an absence there is a provisioning defect. That asymmetry is
//   .claude/rules/guards-need-a-third-state.md's: the same condition is a legitimate pass in one
//   environment and a defect in the other, and collapsing it would let a leg report green having
//   read no packages at all.

using System.IO.Compression;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AppPackageCompileSetupTests
{
    // A manifest in the shape real packages ship, so the parser is exercised against the real
    // namespace rather than a bare-element simplification of it.
    private const string Ns = "http://schemas.microsoft.com/navx/2015/manifest";

    private static string Manifest(
        string appAttrs = "",
        string features = "<PreprocessorSymbols />",
        string extra = "")
        => $"""
        <Package xmlns="{Ns}">
          <App Id="3099ffc7-4cf7-4df6-9b96-7e4bc2bb587c" Name="Subscription Billing"
               Publisher="Microsoft" Version="28.1.49838.54308" {appAttrs} />
          {features}
          {extra}
        </Package>
        """;

    // ── Category 1: read what the package states ────────────────────────────────────

    [Fact]
    public void ReadDeclaration_CarriesTheAppsRealIdentity()
    {
        // Positive direction. The identity is what makes a dependency's InternalsVisibleTo
        // grants match; without it #3530 measured AL0161 rising from 2 to 138 at declaration
        // and up to 442 at emit with 0 objects, on 6 of 8 third-party apps.
        var d = AppPackageCompileSetup.ReadDeclaration(Manifest());

        Assert.Equal(Guid.Parse("3099ffc7-4cf7-4df6-9b96-7e4bc2bb587c"), d.AppId);
        Assert.Equal("Subscription Billing", d.Name);
        Assert.Equal("Microsoft", d.Publisher);
        Assert.Equal("28.1.49838.54308", d.Version);
    }

    [Fact]
    public void ReadDeclaration_NoAppElement_ThrowsNamingTheConsequence()
    {
        // The third state. A package whose identity cannot be read is UNMEASURABLE, not empty:
        // returning a blank identity would reproduce the AL0161 cluster with nothing saying
        // why. The message has to name the consequence, not just the absence.
        var ex = Assert.Throws<ArgumentException>(
            () => AppPackageCompileSetup.ReadDeclaration($"""<Package xmlns="{Ns}" />"""));

        Assert.Contains("no <App> element", ex.Message);
        Assert.Contains("InternalsVisibleTo", ex.Message);
    }

    [Fact]
    public void ReadDeclaration_UnparseableXml_ThrowsRatherThanReturningEmpty()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => AppPackageCompileSetup.ReadDeclaration("<Package"));
        Assert.Contains("did not parse", ex.Message);
    }

    [Fact]
    public void ReadDeclaration_NoImplicitWith_IsReadWhenDeclared()
    {
        // Positive. NOIMPLICITWITH is the feature whose absence is SILENT at declaration on six
        // of eight apps and fatal at EMIT on the other two — the measurement that killed the
        // probe-and-retry design (#3530).
        var d = AppPackageCompileSetup.ReadDeclaration(Manifest(features: """
            <Features>
              <Feature>TRANSLATIONFILE</Feature>
              <Feature>GENERATECAPTIONS</Feature>
              <Feature>NOIMPLICITWITH</Feature>
            </Features>
            """));

        Assert.True(d.NoImplicitWith);
        Assert.Equal(new[] { "TRANSLATIONFILE", "GENERATECAPTIONS", "NOIMPLICITWITH" }, d.Features);
    }

    [Fact]
    public void ReadDeclaration_NoImplicitWith_StaysFalseWhenTheManifestOmitsIt()
    {
        // Negative, and the one that matters: implicit `with` must stay ON for an app that never
        // opted in, so the diagnostics real BC raises are still raised. A reader that defaulted
        // this to true would silence them for every app.
        var d = AppPackageCompileSetup.ReadDeclaration(Manifest(features: """
            <Features><Feature>TRANSLATIONFILE</Feature></Features>
            """));

        Assert.False(d.NoImplicitWith);
        Assert.Equal(new[] { "TRANSLATIONFILE" }, d.Features);
    }

    [Fact]
    public void ReadDeclaration_PreprocessorSymbols_AreReadVerbatim()
    {
        // #3530 measured 19 distinct symbol sets across 76 apps, including vendor-invented ones
        // (CLEAN20..CLEAN28, lowerthanCB28, BC212). So these cannot be matched against a known
        // list — they are carried through as declared.
        var d = AppPackageCompileSetup.ReadDeclaration(Manifest(features: """
            <PreprocessorSymbols>
              <PreprocessorSymbol>CLEAN26</PreprocessorSymbol>
              <PreprocessorSymbol>lowerthanCB28</PreprocessorSymbol>
            </PreprocessorSymbols>
            """));

        Assert.Equal(new[] { "CLEAN26", "lowerthanCB28" }, d.PreprocessorSymbols);
    }

    [Fact]
    public void ReadDeclaration_HelpUrl_BothDirections()
    {
        // Needed on 5 of 8 apps: AL0543 × 3, 14, 41, 80 and 125 (#3530).
        var with = AppPackageCompileSetup.ReadDeclaration(
            Manifest(appAttrs: """ContextSensitiveHelpUrl="https://go.microsoft.com/fwlink/?linkid=2104024" """));
        Assert.Equal("https://go.microsoft.com/fwlink/?linkid=2104024", with.ContextSensitiveHelpUrl);

        // Negative: an omitted URL stays "", so BC's own AL0543 is still raised rather than
        // silenced by a manufactured default.
        var without = AppPackageCompileSetup.ReadDeclaration(Manifest());
        Assert.Equal("", without.ContextSensitiveHelpUrl);
    }

    // ── Category 3: unconditional normalization ─────────────────────────────────────

    [Theory]
    // OPplus: shipped percent-encoded, declared literal — 61 × AL1081 on that app (#3530).
    [InlineData("layout/layout/OPP%20Print%20Application.rdlc", "layout/layout/OPP Print Application.rdlc")]
    // Base Application 28.1 ships 87 DOUBLE-encoded entries; measured on this box.
    [InlineData("src/Permissions/OnPrem/Fixed%2520Assets/FixedAssetsAdmin.PermissionSet.al",
                "src/Permissions/OnPrem/Fixed Assets/FixedAssetsAdmin.PermissionSet.al")]
    // Free when there is nothing to decode — the "no-op on an app that does not need it" claim.
    [InlineData("src/Sales/SalesHeader.Table.al", "src/Sales/SalesHeader.Table.al")]
    public void NormalizeEntryName_DecodesRepeatedlyAndIsANoOpOtherwise(string input, string expected)
        => Assert.Equal(expected, AppPackageCompileSetup.NormalizeEntryName(input));

    [Fact]
    public void NormalizeEntryName_Terminates_OnAPercentThatIsNotAnEscape()
    {
        // The bounded-loop trap named in the implementation: a bare '%' is not a valid escape,
        // and must come back unchanged rather than spinning or throwing inside a compile.
        Assert.Equal("layout/100%/x.rdlc", AppPackageCompileSetup.NormalizeEntryName("layout/100%/x.rdlc"));
    }

    [Theory]
    // Six Base Application report files, 18 properties, measured on 28.1. Emit throws on these
    // and produces ZERO objects (#3530).
    [InlineData(@".\Inventory\Reports\InventoryCustomerSales.rdlc", "./Inventory/Reports/InventoryCustomerSales.rdlc")]
    // Zero instances across all 76 third-party apps — a no-op there, which is why it is free.
    [InlineData("./layout/x.rdlc", "./layout/x.rdlc")]
    public void NormalizeLayoutPath_BothDirections(string input, string expected)
        => Assert.Equal(expected, AppPackageCompileSetup.NormalizeLayoutPath(input));

    [Fact]
    public void ResolveResourceEntry_CaseFolds_WindowsBuiltLinuxCompiled()
    {
        // OPplus declares images/loader.gif, the package holds images/Loader.gif. One file on
        // the build host, two on the compile host — AL0327 (#3530).
        var entries = new[] { "images/Loader.gif", "src/x.al" };
        Assert.Equal("images/Loader.gif",
            AppPackageCompileSetup.ResolveResourceEntry("images/loader.gif", entries));
    }

    [Fact]
    public void ResolveResourceEntry_PrefersAnExactMatchOverACaseFold()
    {
        // A package genuinely shipping two entries differing only in case must still resolve to
        // the declared one. Without this, case-folding would be a silent wrong answer rather
        // than a fix.
        var entries = new[] { "images/Loader.gif", "images/loader.gif" };
        Assert.Equal("images/loader.gif",
            AppPackageCompileSetup.ResolveResourceEntry("images/loader.gif", entries));
    }

    [Fact]
    public void ResolveResourceEntry_ResolvesThroughALayeredRoot()
    {
        // Banking declares 'ControlAddIns/javascript/PopupStartup.js' resolved under addin/src/.
        // Flattening the roots into one directory is what loses the overlay — 1-6 × AL0327 on
        // EVERY app (#3530) — so the tail has to match across a root prefix.
        var entries = new[] { "addin/src/ControlAddIns/javascript/PopupStartup.js" };
        Assert.Equal("addin/src/ControlAddIns/javascript/PopupStartup.js",
            AppPackageCompileSetup.ResolveResourceEntry("ControlAddIns/javascript/PopupStartup.js", entries));
    }

    [Fact]
    public void ResolveResourceEntry_ReturnsNull_WhenTheResourceGenuinelyIsNotThere()
    {
        // The negative that keeps the other four honest: a genuinely missing resource must be a
        // real absence the caller reports, never papered over by a looser match.
        Assert.Null(AppPackageCompileSetup.ResolveResourceEntry(
            "images/missing.gif", new[] { "images/Loader.gif", "src/x.al" }));
    }

    // ── Point 2: the diagnostic names the responsible part ──────────────────────────

    [Theory]
    [InlineData("AL0133", AppPackageSetupPart.ManifestFeatures)]
    [InlineData("AL0135", AppPackageSetupPart.ManifestFeatures)]
    [InlineData("AL0543", AppPackageSetupPart.ManifestHelpUrl)]
    [InlineData("AL0161", AppPackageSetupPart.AppIdentity)]
    [InlineData("AL0327", AppPackageSetupPart.EntryNameNormalization)]
    [InlineData("AL1081", AppPackageSetupPart.EntryNameNormalization)]
    // A code the setup cannot cause. PartFor must be CALLED for None rows too: an assertion that
    // short-circuits on `expected == None` accepts any mapping for them (#4496).
    [InlineData("AL0432", AppPackageSetupPart.None)]
    public void PartFor_NamesTheSetupPartTheAblationAttributedTheCodeTo(
        string code, AppPackageSetupPart expected)
        => Assert.Equal(expected, AppPackageSetupDiagnosis.PartFor(code));

    [Fact]
    public void PartFor_ACodeTheSetupCannotCause_IsNone()
    {
        // Negative. Most AL diagnostics are the AL's own; attributing one of those to a setup
        // part would send the reader to re-examine a setup that is working.
        Assert.Equal(AppPackageSetupPart.None, AppPackageSetupDiagnosis.PartFor("AL0118"));
    }

    [Fact]
    public void Explain_ZeroObjects_SaysTheRunProducedNothing_EvenWithNoAttributableCode()
    {
        // #3530's central trap: emit is atomic per module, so a broken setup yields EXACTLY zero
        // objects and a check on "did it throw" passes a run that produced nothing. The zero has
        // to be reported even when no diagnostic is attributable to the setup.
        var text = AppPackageSetupDiagnosis.Explain(new[] { "AL0118" }, objectCount: 0);

        Assert.Contains("ZERO objects", text);
        Assert.Contains("atomic per module", text);
        Assert.Contains("No diagnostic is attributable", text);
    }

    [Fact]
    public void Explain_NamesEveryImplicatedPart_AndTheCodesThatImplicatedIt()
    {
        var text = AppPackageSetupDiagnosis.Explain(
            new[] { "AL0133", "AL0135", "AL0161", "AL0118" }, objectCount: 0);

        Assert.Contains("ManifestFeatures: implicated by AL0133, AL0135.", text);
        Assert.Contains("AppIdentity: implicated by AL0161.", text);
        // AL0118 is the AL's own and must not acquire a part.
        Assert.DoesNotContain("AL0118", text);
    }

    [Fact]
    public void Explain_ASuccessfulCompile_NeitherWarnsNorAttributes()
    {
        // The other direction of the same discrimination: a run that produced objects and no
        // attributable diagnostic must not read as a setup failure.
        var text = AppPackageSetupDiagnosis.Explain(Array.Empty<string>(), objectCount: 7850);

        Assert.Contains("7850 object(s)", text);
        Assert.DoesNotContain("ZERO objects", text);
    }

    // ── The real package corpus ─────────────────────────────────────────────────────

    /// <summary>
    /// Skip locally, FAIL on CI, when no shipped package could be read.
    ///
    /// An empty corpus makes every assertion below vacuous, and vacuous assertions are exactly
    /// what these tests exist to rule out. On a CI leg bc-tests.yml downloads platform and test
    /// apps before `dotnet test`, so "no packages" there is a provisioning defect rather than a
    /// legitimate absence — reporting it as a skip would leave the leg green having measured
    /// nothing (.claude/rules/guards-need-a-third-state.md).
    /// </summary>
    private static void SkipIfNoCorpus(
        IReadOnlyList<(string Path, string Manifest, IReadOnlyList<string> Entries)> corpus)
    {
        if (corpus.Count > 0) return;

        const string reason =
            "no shipped .app package could be read under ~/.al-runner/platform-apps or "
            + "~/.al-runner/test-apps. Provision with `tools/DownloadArtifacts` "
            + "(see .github/workflows/bc-tests.yml).";

        if (TestArtifacts.RunningOnCi)
            Assert.Fail(
                "Shipped .app packages are missing on a CI leg, where they are downloaded before "
                + "`dotnet test` by construction, so this is a provisioning defect and NOT a "
                + "legitimate skip — a leg whose package-corpus tests all skip would otherwise "
                + "report green having read no packages. " + reason);

        throw new SkipException(reason);
    }

    /// <summary>
    /// Skip when Base Application is not among the packages on this box. Unlike
    /// <see cref="SkipIfNoCorpus"/> this is a legitimate skip ON CI TOO: the backslash-layout
    /// instances are a property of specific Microsoft report files, and a leg provisioned with a
    /// test-app set that omits Base Application has nothing to measure rather than a defect.
    /// </summary>
    private static void SkipIfNoBaseApplication(int instanceCount)
    {
        if (instanceCount > 0) return;
        throw new SkipException(
            "Base Application is not among the packages on this box, so the shipped "
            + "backslash-layout instances cannot be read.");
    }

    /// <summary>
    /// Every shipped .app on this box, as (path, manifest-xml, entry-names). Reads the inner
    /// package when the outer one is an R2R wrapper, which is what Base/System Application ship
    /// as — a reader that stopped at the outer archive would see 9 entries and no manifest.
    /// </summary>
    private static IReadOnlyList<(string Path, string Manifest, IReadOnlyList<string> Entries)> Corpus()
    {
        var roots = new[]
        {
            Path.Combine(AlRunnerPaths.UserHome ?? "", ".al-runner", "platform-apps"),
            Path.Combine(AlRunnerPaths.UserHome ?? "", ".al-runner", "test-apps"),
        };

        var found = new List<(string, string, IReadOnlyList<string>)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.app").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var read = ReadPackage(File.ReadAllBytes(path));
                    if (read != null) found.Add((path, read.Value.Manifest, read.Value.Entries));
                }
                catch (Exception) { /* a truncated download is not this test's subject */ }
            }
        }
        return found;
    }

    private static (string Manifest, IReadOnlyList<string> Entries)? ReadPackage(byte[] raw)
    {
        // A .app is a 40-byte NAVX header followed by a zip.
        var off = IndexOfZipHeader(raw);
        if (off < 0) return null;
        using var outer = new ZipArchive(new MemoryStream(raw, off, raw.Length - off), ZipArchiveMode.Read);

        var manifest = outer.GetEntry("NavxManifest.xml");
        if (manifest != null)
            return (ReadText(manifest), outer.Entries.Select(e => e.FullName).ToList());

        // R2R wrapper: the real package is a nested .app at the archive root.
        var inner = outer.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                 && !e.FullName.Contains('/'));
        if (inner == null) return null;

        using var innerStream = new MemoryStream();
        using (var s = inner.Open()) s.CopyTo(innerStream);
        return ReadPackage(innerStream.ToArray());
    }

    private static int IndexOfZipHeader(byte[] raw)
    {
        for (var i = 0; i + 3 < Math.Min(raw.Length, 1024); i++)
            if (raw[i] == 'P' && raw[i + 1] == 'K' && raw[i + 2] == 3 && raw[i + 3] == 4) return i;
        return -1;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [SkippableFact]
    public void Corpus_EveryShippedPackageDeclarationIsReadable_AndCarriesAnIdentity()
    {
        var corpus = Corpus();
        SkipIfNoCorpus(corpus);

        // Assert on the PRODUCT. A run that read zero packages, or read them and produced blank
        // identities, must not pass: both are what a broken reader looks like, and neither
        // throws.
        var failures = new List<string>();
        var withFeatures = 0;
        var withHelpUrl = 0;

        foreach (var (path, manifest, _) in corpus)
        {
            AppPackageDeclaration d;
            try { d = AppPackageCompileSetup.ReadDeclaration(manifest); }
            catch (ArgumentException ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); continue; }

            if (d.AppId == Guid.Empty) failures.Add($"{Path.GetFileName(path)}: blank AppId");
            if (string.IsNullOrWhiteSpace(d.Name)) failures.Add($"{Path.GetFileName(path)}: blank Name");
            if (string.IsNullOrWhiteSpace(d.Publisher)) failures.Add($"{Path.GetFileName(path)}: blank Publisher");
            if (string.IsNullOrWhiteSpace(d.Version)) failures.Add($"{Path.GetFileName(path)}: blank Version");

            if (d.Features.Count > 0) withFeatures++;
            if (d.ContextSensitiveHelpUrl.Length > 0) withHelpUrl++;
        }

        Assert.Empty(failures);

        // The population has to actually EXERCISE the reader, not merely be non-empty: a corpus
        // of packages that declare no features and no help URL would pass the loop above while
        // proving nothing about either field. These floors are well under what this box holds
        // (measured 28.1: 76 of 113 declare a help URL; EVERY readable package declares
        // <Features> -- 108 of 108 on the 28.1.49838.53910 platform + test apps, #4496), so they pin
        // "the corpus reaches these fields" without pinning a count that drifts per artifact set.
        Assert.True(withFeatures >= 2,
            $"only {withFeatures} of {corpus.Count} package(s) declared any <Features>; "
            + "the corpus is not exercising feature reading.");
        Assert.True(withHelpUrl >= 2,
            $"only {withHelpUrl} of {corpus.Count} package(s) declared a ContextSensitiveHelpUrl; "
            + "the corpus is not exercising help-URL reading.");
    }

    [SkippableFact]
    public void Corpus_NoImplicitWithIsReadFromRealPackages_AndIsNotUniversal()
    {
        var corpus = Corpus();
        SkipIfNoCorpus(corpus);

        var declaring = new List<string>();
        var notDeclaring = new List<string>();
        foreach (var (path, manifest, _) in corpus)
        {
            AppPackageDeclaration d;
            try { d = AppPackageCompileSetup.ReadDeclaration(manifest); } catch (ArgumentException) { continue; }
            (d.NoImplicitWith ? declaring : notDeclaring).Add(Path.GetFileName(path));
        }

        // BOTH directions have to be non-empty on a real corpus. A reader hardwired to true, and
        // one hardwired to false, each satisfy one of these and fail the other — which a
        // single-direction assertion could not tell apart. Base Application 28.1 declares it;
        // the great majority of test apps do not (measured: 14 of 113 on this box).
        Assert.True(declaring.Count > 0,
            "no shipped package declared NOIMPLICITWITH; the reader is not reading features.");
        Assert.True(notDeclaring.Count > 0,
            "every shipped package read as declaring NOIMPLICITWITH; the reader is not "
            + "discriminating and would silence implicit-with diagnostics for every app.");
    }

    [SkippableFact]
    public void Corpus_PercentEncodedEntryNames_AreShippedAndDecodeToRealPaths()
    {
        var corpus = Corpus();
        SkipIfNoCorpus(corpus);

        // The measured claim: real packages DO ship percent-encoded entry names, so category 3's
        // URL-decoding is not a defensive nicety. Base Application 28.1 ships 87 double-encoded
        // src/ entries.
        var encoded = new List<(string Pkg, string Entry)>();
        foreach (var (path, _, entries) in corpus)
            foreach (var e in entries)
                if (e.Contains('%')) encoded.Add((Path.GetFileName(path), e));

        Assert.True(encoded.Count > 0,
            "no shipped package carried a percent-encoded entry name; either the corpus changed "
            + "or the package reader is not returning entry names.");

        foreach (var (pkg, entry) in encoded)
        {
            var decoded = AppPackageCompileSetup.NormalizeEntryName(entry);
            Assert.False(decoded.Contains('%'),
                $"{pkg}: {entry} still carries '%' after normalization -> {decoded}");
            Assert.NotEqual(entry, decoded);
        }
    }

    [SkippableFact]
    public void Corpus_BackslashLayoutPaths_AreShippedByMicrosoftAndNormalizeToForwardSlashes()
    {
        var corpus = Corpus();
        SkipIfNoCorpus(corpus);

        // Assert the claim the issue makes about WHERE this occurs, not just that the helper
        // works: backslash layout paths are a defect in specific Microsoft report files, not a
        // convention. Each one is a file emit throws on, producing zero objects.
        var raw = new List<string>();

        // Read the AL sources of the one package known to carry them, if present.
        foreach (var (path, _, _) in corpus)
        {
            if (!Path.GetFileName(path).Contains("Base Application", StringComparison.OrdinalIgnoreCase))
                continue;
            raw.AddRange(BackslashLayoutProperties(File.ReadAllBytes(path)));
        }

        SkipIfNoBaseApplication(raw.Count);

        foreach (var value in raw)
        {
            Assert.Contains('\\', value);
            var normalized = AppPackageCompileSetup.NormalizeLayoutPath(value);
            Assert.DoesNotContain('\\', normalized);
            Assert.Equal(value.Replace('\\', '/'), normalized);
        }
    }

    private static IReadOnlyList<string> BackslashLayoutProperties(byte[] raw)
    {
        var read = ReadPackageArchive(raw);
        if (read == null) return Array.Empty<string>();
        using var archive = read;

        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?:RDLCLayout|WordLayout|LayoutFile|ExcelLayout)\s*=\s*'([^']*)'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var hits = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;
            var text = ReadText(entry);
            if (!text.Contains('\\')) continue;
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
                if (m.Groups[1].Value.Contains('\\')) hits.Add(m.Groups[1].Value);
        }
        return hits;
    }

    private static ZipArchive? ReadPackageArchive(byte[] raw)
    {
        var off = IndexOfZipHeader(raw);
        if (off < 0) return null;
        var outer = new ZipArchive(new MemoryStream(raw, off, raw.Length - off), ZipArchiveMode.Read);
        if (outer.GetEntry("NavxManifest.xml") != null) return outer;

        var inner = outer.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                 && !e.FullName.Contains('/'));
        if (inner == null) { outer.Dispose(); return null; }

        using var innerStream = new MemoryStream();
        using (var s = inner.Open()) s.CopyTo(innerStream);
        outer.Dispose();
        return ReadPackageArchive(innerStream.ToArray());
    }
}
