// AppPackageCompileSetup — one compile setup for a SHIPPED .app package's own src/, derived
// from what the package itself declares. See #3530.
//
// WHY THIS IS NOT "THE RECIPE"
//   The question this answers used to be framed as a list of ingredients to apply when
//   compiling a shipped app's sources. An ablation across eight third-party apps (#3491's
//   thread) falsified that shape: two of the five named ingredients never fire on non-Microsoft
//   code, one has no instance in 76 apps, and three ingredients the list did not mention are
//   fatal on the majority of apps — two of them producing ZERO objects.
//
//   What survives is a split by WHERE THE ANSWER COMES FROM, which is what this file encodes:
//
//     1. Replayed from the package    — per-app, but free. The package already states it; the
//                                       setup just has to stop ignoring it. Deterministic,
//                                       single-pass, cannot produce a wrong answer.
//     2. Facts about the BC artifact  — resolved once per BC version, identical for every app.
//     3. Unconditional normalization  — a no-op on a package that does not need it, so
//                                       conditioning it on detection buys nothing and adds a
//                                       way to be wrong.
//
//   Only category 1 is per-app, and it is a READ rather than a decision. There is no detection
//   step and no per-app configuration anywhere in this file.
//
// WHY NOT PROBE-AND-RETRY
//   The tempting alternative is to compile, see what fails, adjust, retry. Beyond the cost
//   (11-40 s per third-party app, minutes for Base Application, paid several times over), the
//   obvious success signal is WRONG: NOIMPLICITWITH is silent at declaration on six of eight
//   apps and fatal at EMIT on the other two. A probe keyed on declaration diagnostics would
//   conclude the feature is unnecessary and ship an app that emits nothing.
//
// THE DIAGNOSTIC IS THE DELIVERABLE, NOT THE LIST
//   The unknown-unknown here is the next vendor quirk nobody has seen. A list of known
//   ingredients cannot cover it by construction. What can is a failure that NAMES THE
//   RESPONSIBLE PART: see AppPackageSetupDiagnosis, which maps a compile's diagnostic codes
//   back onto the part of this setup that would have prevented them, so the next quirk is a
//   five-minute diagnosis instead of a session of ablation.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AlRunner.Infrastructure;

/// <summary>
/// Which part of <see cref="AppPackageCompileSetup"/> a compile failure is attributable to.
/// The values are the vocabulary <see cref="AppPackageSetupDiagnosis"/> reports in, and the
/// reason this type exists rather than a list of ingredient names: a part stays meaningful when
/// the specific quirk behind it is one nobody has seen yet.
/// </summary>
public enum AppPackageSetupPart
{
    /// <summary>No part of the setup is implicated — the AL itself did not compile.</summary>
    None = 0,

    /// <summary>The manifest's <c>&lt;Features&gt;</c>, e.g. NOIMPLICITWITH (category 1).</summary>
    ManifestFeatures,

    /// <summary>The manifest's <c>&lt;PreprocessorSymbols&gt;</c> (category 1).</summary>
    ManifestPreprocessorSymbols,

    /// <summary>The manifest's <c>ContextSensitiveHelpUrl</c> (category 1).</summary>
    ManifestHelpUrl,

    /// <summary>The app's real Name/Publisher/Version/AppId on the compilation (category 1).</summary>
    AppIdentity,

    /// <summary>The package's resource roots, layered rather than flattened (category 1).</summary>
    ResourceRoots,

    /// <summary>A reference the BC artifact supplies that is not on the probing path (category 2).</summary>
    ArtifactReferences,

    /// <summary>Package entry names needing URL-decoding or case-folding (category 3).</summary>
    EntryNameNormalization,

    /// <summary>A layout property whose path separator needs normalizing (category 3).</summary>
    LayoutPathNormalization,
}

/// <summary>
/// What one shipped package declares, in the form a compile needs it. Category 1 of #3530:
/// every field is READ from the package, never inferred and never configured per app.
/// </summary>
/// <param name="AppId">The manifest's <c>App/@Id</c>.</param>
/// <param name="Name">The manifest's <c>App/@Name</c>.</param>
/// <param name="Publisher">The manifest's <c>App/@Publisher</c>.</param>
/// <param name="Version">The manifest's <c>App/@Version</c>.</param>
/// <param name="Features">Raw <c>&lt;Feature&gt;</c> strings, upper-cased as the manifest spells them.</param>
/// <param name="PreprocessorSymbols">Raw <c>&lt;PreprocessorSymbol&gt;</c> strings.</param>
/// <param name="ContextSensitiveHelpUrl">The manifest's <c>App/@ContextSensitiveHelpUrl</c>, or "".</param>
/// <param name="Target">The manifest's <c>App/@Target</c>, or "".</param>
public readonly record struct AppPackageDeclaration(
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> PreprocessorSymbols,
    string ContextSensitiveHelpUrl,
    string Target)
{
    /// <summary>
    /// True when the manifest declares NOIMPLICITWITH. Named because it is the one feature whose
    /// absence is silent at declaration and fatal at emit — see this file's header.
    /// </summary>
    public bool NoImplicitWith =>
        Features.Any(f => string.Equals(f, "NOIMPLICITWITH", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads a shipped package's own declaration, and normalizes its entry names and layout paths.
/// Every member here is a pure function of package bytes: no BC engine, no artifacts, no I/O
/// beyond the archive the caller already opened.
/// </summary>
public static class AppPackageCompileSetup
{
    /// <summary>
    /// Category 1 — read what the package states. <paramref name="navxManifestXml"/> is the
    /// package's own <c>NavxManifest.xml</c>.
    ///
    /// Every field falls back to BC's own "unset" default when the manifest omits it, so a
    /// package that genuinely declares nothing still produces the diagnostics real BC would
    /// (AL0543 for a missing help URL, implicit <c>with</c> for an app that never opted into
    /// NOIMPLICITWITH). Silence is never manufactured here.
    /// </summary>
    /// <exception cref="ArgumentException">The manifest has no <c>App</c> element — a package
    /// whose identity cannot be read is unmeasurable, not empty. Returning a blank identity
    /// would reproduce #3530's AL0161 cluster (up to 442 errors, 0 objects) with nothing saying
    /// why, which is the third-state shape .claude/rules/guards-need-a-third-state.md refuses.</exception>
    public static AppPackageDeclaration ReadDeclaration(string navxManifestXml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(navxManifestXml); }
        catch (System.Xml.XmlException ex)
        {
            throw new ArgumentException(
                "NavxManifest.xml did not parse, so the package's declaration could not be read: "
                + ex.Message, nameof(navxManifestXml), ex);
        }

        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var app = doc.Root?.Element(ns + "App")
            ?? throw new ArgumentException(
                "NavxManifest.xml has no <App> element, so the package declares no identity. "
                + "Compiling without the app's real Name/Publisher/Version/AppId makes a "
                + "dependency's InternalsVisibleTo grants fail to match (#3530: AL0161, up to "
                + "442 at emit with 0 objects).", nameof(navxManifestXml));

        string Attr(string name) => app.Attribute(name)?.Value ?? "";

        var id = Guid.TryParse(Attr("Id"), out var parsed) ? parsed : Guid.Empty;

        var features = doc.Root!.Element(ns + "Features")?.Elements(ns + "Feature")
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList()
            ?? new List<string>();

        var symbols = doc.Root!.Element(ns + "PreprocessorSymbols")?.Elements(ns + "PreprocessorSymbol")
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList()
            ?? new List<string>();

        return new AppPackageDeclaration(
            id, Attr("Name"), Attr("Publisher"), Attr("Version"),
            features, symbols, Attr("ContextSensitiveHelpUrl"), Attr("Target"));
    }

    /// <summary>
    /// Category 3 — URL-decode a package entry name, unconditionally.
    ///
    /// A package built on Windows can ship an entry name percent-encoded while the AL that
    /// references it spells the character literally: OPplus ships
    /// <c>layout/layout/OPP%20Print%20Application.rdlc</c> against AL saying
    /// <c>OPP Print Application.rdlc</c> — 61 × AL1081 on that app, 39 on another (#3530).
    /// Base Application 28.1 ships 87 DOUBLE-encoded entries (<c>%2520</c>, i.e. an encoded
    /// <c>%</c> followed by <c>20</c>), which is why this decodes repeatedly rather than once.
    ///
    /// Free when there is nothing to decode: a name with no <c>%</c> is returned unchanged, so
    /// conditioning this on detection would buy nothing and add a way to be wrong.
    ///
    /// TRAP for a later editor: the loop is bounded and MUST stay bounded. A name that decodes
    /// to itself (there is no such percent-escape, but a future encoding could) would otherwise
    /// spin forever inside a compile.
    /// </summary>
    public static string NormalizeEntryName(string entryName)
    {
        if (entryName.IndexOf('%') < 0) return entryName;

        var current = entryName;
        for (var i = 0; i < 4; i++)
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(current); }
            catch (UriFormatException) { return current; }  // not an escape sequence after all
            if (decoded == current) return current;
            current = decoded;
            if (current.IndexOf('%') < 0) return current;
        }
        return current;
    }

    /// <summary>
    /// Category 3 — normalize a layout property's path separator, unconditionally.
    ///
    /// Six Base Application report files ship <c>LayoutFile = '.\Inventory\Reports\x.rdlc'</c>
    /// with Windows separators; emit throws on them and produces ZERO objects (#3530). Measured
    /// on 28.1: 18 such properties across those 6 files. There are ZERO instances across all 76
    /// third-party apps surveyed — this is a defect in specific Microsoft files rather than a
    /// convention, and it stays only because it is free.
    ///
    /// TRAP: a backslash is a legal filename character on Linux, so this is not universally
    /// safe — it is safe HERE because the value is a package-relative layout path, which BC's
    /// own tooling writes with the build host's separator. Do not lift it to a general path
    /// helper.
    /// </summary>
    public static string NormalizeLayoutPath(string layoutPath) => layoutPath.Replace('\\', '/');

    /// <summary>
    /// Category 3 — resolve a declared resource name against the package's entries, case-folding
    /// and URL-decoding both sides.
    ///
    /// OPplus declares <c>images/loader.gif</c> and ships <c>images/Loader.gif</c>: built on
    /// Windows where the two are one file, compiled on Linux where they are not — AL0327
    /// (#3530). Returns the entry name AS THE PACKAGE SPELLS IT, because that is the key the
    /// archive reader needs; null when nothing matches, which is a real absence the caller must
    /// report rather than paper over.
    ///
    /// An exact match wins over a case-insensitive one, so a package genuinely shipping two
    /// entries differing only in case still resolves to the declared one.
    /// </summary>
    public static string? ResolveResourceEntry(string declared, IEnumerable<string> entryNames)
    {
        var want = NormalizeLayoutPath(NormalizeEntryName(declared)).TrimStart('.', '/');
        string? fold = null;
        foreach (var entry in entryNames)
        {
            var normalized = NormalizeLayoutPath(NormalizeEntryName(entry));
            if (string.Equals(normalized, want, StringComparison.Ordinal)) return entry;
            if (fold == null && string.Equals(normalized, want, StringComparison.OrdinalIgnoreCase))
                fold = entry;
            // A declared path is package-relative while an entry may carry a root prefix
            // (layout/, src/, addin/src/): match on the tail so a LAYERED root resolves without
            // the caller having to flatten them into one directory, which is what loses the
            // overlay (#3530: removing it costs 1-6 × AL0327 on EVERY app).
            if (fold == null && normalized.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase))
                fold = entry;
        }
        return fold;
    }
}

/// <summary>
/// Maps a compile's diagnostic codes back onto the <see cref="AppPackageSetupPart"/> that would
/// have prevented them. Point 2 of #3530, and the thing that replaces a list of ingredients:
/// the next vendor quirk is one nobody has seen, so what has to survive is the ability to name
/// the responsible part from the failure, not a catalogue of known causes.
/// </summary>
public static class AppPackageSetupDiagnosis
{
    // Each code is attributed to the part whose ABSENCE produces it, measured in the #3530
    // ablation across eight third-party apps plus Base Application 28.1. The citation for each
    // row is that ablation; the counts are on the issue rather than repeated here, because a
    // count in a comment goes stale the first time the thing it measures improves.
    private static readonly IReadOnlyDictionary<string, AppPackageSetupPart> ByCode =
        new Dictionary<string, AppPackageSetupPart>(StringComparer.OrdinalIgnoreCase)
        {
            // Implicit `with Rec` puts a dependency's method into unqualified scope and beats
            // the object's own local one. Silent on six of eight apps, fatal at emit on two.
            ["AL0133"] = AppPackageSetupPart.ManifestFeatures,
            ["AL0135"] = AppPackageSetupPart.ManifestFeatures,
            // A page/report naming ContextSensitiveHelpPage with no URL declared.
            ["AL0543"] = AppPackageSetupPart.ManifestHelpUrl,
            // A dependency's InternalsVisibleTo grant does not match, because the compilation
            // was not created under the app's real identity.
            ["AL0161"] = AppPackageSetupPart.AppIdentity,
            // A declared resource that the package does ship, under a name differing by case
            // or by percent-encoding.
            ["AL0327"] = AppPackageSetupPart.EntryNameNormalization,
            ["AL1081"] = AppPackageSetupPart.EntryNameNormalization,
        };

    /// <summary>
    /// The part implicated by one diagnostic code, or <see cref="AppPackageSetupPart.None"/>
    /// when the code is not one this setup can cause.
    ///
    /// None is a real answer and deliberately NOT a failure: most AL diagnostics are the AL's
    /// own, and reporting a setup part for one of those would send the reader to re-examine a
    /// setup that is working. What makes the third state distinguishable is
    /// <see cref="Explain"/>'s message, which says which of the two it found.
    /// </summary>
    public static AppPackageSetupPart PartFor(string diagnosticCode) =>
        ByCode.TryGetValue(diagnosticCode, out var part) ? part : AppPackageSetupPart.None;

    /// <summary>
    /// A human-readable diagnosis for a compile that produced <paramref name="diagnosticCodes"/>
    /// and <paramref name="objectCount"/> objects.
    ///
    /// The object count is not decoration. Emit is ATOMIC PER MODULE, so a broken setup yields
    /// exactly zero objects — and a caller checking only "did it throw" passes a run that
    /// produced nothing (#3530). A zero here is always reported, even when every diagnostic is
    /// attributable to the AL rather than to the setup.
    /// </summary>
    public static string Explain(IEnumerable<string> diagnosticCodes, int objectCount)
    {
        var parts = new List<AppPackageSetupPart>();
        var byPart = new Dictionary<AppPackageSetupPart, List<string>>();
        foreach (var code in diagnosticCodes)
        {
            var part = PartFor(code);
            if (part == AppPackageSetupPart.None) continue;
            if (!byPart.TryGetValue(part, out var codes))
            {
                byPart[part] = codes = new List<string>();
                parts.Add(part);
            }
            if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) codes.Add(code);
        }

        var lines = new List<string>
        {
            $"app-package compile produced {objectCount} object(s)."
        };

        if (objectCount == 0)
            lines.Add(
                "ZERO objects: emit is atomic per module, so this is a broken setup rather than "
                + "a partially-compiled app. Do not read the absence of an exception as success.");

        if (parts.Count == 0)
            lines.Add(
                "No diagnostic is attributable to the package compile setup. Either the AL "
                + "itself does not compile, or this is a quirk the setup does not yet model — "
                + "if the latter, the part to extend is the one whose category the failing "
                + "resource/identity/feature belongs to (see AppPackageCompileSetup).");
        else
            foreach (var part in parts)
                lines.Add($"{part}: implicated by {string.Join(", ", byPart[part])}.");

        return string.Join(Environment.NewLine, lines);
    }
}
