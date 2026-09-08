// ExcludedObjectTriage — issue #3476: decide, PER EXCLUDED OBJECT, whether dropping it
// leaves the surviving module honest.
//
// BcCompiler's emit-retry loop drops an object that cannot be emitted and recompiles the
// rest. Program.cs then refused to run the recovered module at all, with one carve-out for
// profiles. That refusal is what costs Tests-Misc (3,215 tests) and Tests-Integration (340)
// their entire result set over three test codeunits that reference a DotNet type the runner
// does not have. This type answers the narrower question the refusal was standing in for.
//
// Two facts decide it, and the first is the compiler's, not ours:
//
//   1. The retry loop CASCADES. A surviving object that names a dropped object no longer
//      binds, so the next round excludes the referrer too. Measured on a two-codeunit
//      fixture: making the healthy codeunit declare `Broken: Codeunit "Emit Excl Broken"`
//      moved it from 1 excluded object to 2. So the survivor set is already free of
//      unresolved COMPILE-TIME references to anything dropped.
//   2. An object-ID reference is NOT compile-checked and survives that cascade. Same
//      fixture with `Codeunit.Run(60620)` in the healthy codeunit: still 1 excluded object,
//      and the survivor would call a codeunit that is not in the module. That residue is
//      what the scan below exists to catch.
//
// See docs/emit-exclusion-triage.md for the measurements and the decision table.
using System.Text.RegularExpressions;

namespace AlRunner;

// Members of the ProgramSupport partial, following the #2665 split convention every other
// file in this directory uses.
internal static partial class ProgramSupport
{
    /// <summary>One excluded object's verdict. <paramref name="Droppable"/> false always carries
    /// a <paramref name="Reason"/> naming what stopped it, so the run can say WHICH refusal it
    /// made rather than only that it refused.</summary>
    internal sealed record ExcludedObjectVerdict(
        string Label,
        string FilePath,
        bool Droppable,
        string Reason,
        IReadOnlyList<string> ReferencedBy);

    internal static class ExcludedObjectTriage
    {
        // An AL object declaration's header line: `codeunit 135209 "Azure Key Vault Module Test"`.
        // Same shape Program.cs's PARTIAL-EMIT-DROP guard already scans for, plus the id and the
        // kind, which that one discards.
        private static readonly Regex ObjectDecl = new(
            @"^\s*(?<kind>table|codeunit|page|report|query|enum|xmlport|profile|tableextension|pageextension|enumextension|reportextension|permissionset|permissionsetextension|interface|controladdin|entitlement|dotnet)\s+(?<id>\d+)\s+(""(?<qname>[^""\r\n]+)""|(?<name>[A-Za-z_][A-Za-z0-9_]*))",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SubtypeTest = new(
            @"^\s*Subtype\s*=\s*Test\s*;", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed record DeclaredObject(string Kind, int Id, string Name);

        /// <summary>
        /// Verdict per excluded object, in the order given. An object the file system or the
        /// declaration regex cannot account for is NOT droppable — an unreadable excluded object
        /// is exactly the case where guessing is forbidden (.claude/rules/loud-failures.md).
        /// </summary>
        /// <param name="excluded">One entry per excluded object, carrying its own source file.</param>
        /// <param name="moduleAlFiles">Every .al file the module was compiled from, excluded ones
        /// included — this method filters them out itself, so a caller cannot get the survivor set
        /// subtly wrong.</param>
        internal static IReadOnlyList<ExcludedObjectVerdict> Triage(
            IReadOnlyList<TddExcludedObjectDetail> excluded,
            IReadOnlyList<string> moduleAlFiles)
        {
            var excludedPaths = new HashSet<string>(
                excluded.Select(e => NormalizePath(e.FilePath)), StringComparer.Ordinal);
            var survivors = moduleAlFiles
                .Where(f => !excludedPaths.Contains(NormalizePath(f)))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // Read each survivor once: a module can hold thousands of files and every excluded
            // object is scanned against all of them.
            var survivorText = new List<(string Path, string Text)>();
            foreach (var f in survivors)
            {
                try { survivorText.Add((f, File.ReadAllText(f))); }
                catch
                {
                    // A survivor we cannot read cannot be cleared of referencing anything, so it
                    // is recorded as a reference to EVERY excluded object below rather than
                    // skipped. Signalled by a null text.
                    survivorText.Add((f, null!));
                }
            }

            var verdicts = new List<ExcludedObjectVerdict>();
            foreach (var e in excluded)
            {
                string src;
                try { src = File.ReadAllText(e.FilePath); }
                catch (Exception ex)
                {
                    verdicts.Add(new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, false,
                        $"its source file could not be re-read to identify it ({ex.GetType().Name}: {ex.Message})",
                        Array.Empty<string>()));
                    continue;
                }

                var declared = ObjectDecl.Matches(src)
                    .Select(m => new DeclaredObject(
                        m.Groups["kind"].Value.ToLowerInvariant(),
                        int.TryParse(m.Groups["id"].Value, out var id) ? id : -1,
                        m.Groups["qname"].Success ? m.Groups["qname"].Value : m.Groups["name"].Value))
                    .ToList();
                if (declared.Count == 0)
                {
                    verdicts.Add(new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, false,
                        "no AL object declaration could be found in its source file, so what was "
                        + "dropped cannot be identified",
                        Array.Empty<string>()));
                    continue;
                }

                // A profile carries no executable AL and no [Test] procedures — #2238's carve-out,
                // unchanged and still the only one that needs no reference scan at all.
                if (declared.All(d => d.Kind == "profile"))
                {
                    verdicts.Add(new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, true,
                        "a profile declares no executable AL and no [Test] procedures",
                        Array.Empty<string>()));
                    continue;
                }

                // Narrow on purpose, and narrower than "unreferenced". A test codeunit's only
                // caller is the test runner; anything else — a table other objects hold rows in, a
                // library codeunit, a page — can be depended on in ways no textual scan sees.
                var notTest = declared.FirstOrDefault(d => d.Kind != "codeunit");
                if (notTest != null)
                {
                    verdicts.Add(new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, false,
                        $"it declares {notTest.Kind} {notTest.Id} \"{notTest.Name}\", which is not a "
                        + "test codeunit — a surviving object may depend on it",
                        Array.Empty<string>()));
                    continue;
                }
                if (!SubtypeTest.IsMatch(src))
                {
                    verdicts.Add(new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, false,
                        "it is a codeunit without Subtype = Test, so a surviving object may call it",
                        Array.Empty<string>()));
                    continue;
                }

                var referencedBy = ReferencesTo(declared, survivorText);
                verdicts.Add(referencedBy.Count > 0
                    ? new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, false,
                        $"{referencedBy.Count} surviving source file(s) name it or its object id",
                        referencedBy)
                    : new ExcludedObjectVerdict(
                        e.ObjectDisplayName, e.FilePath, true,
                        "a test codeunit no surviving source in this module names, by name or by object id",
                        Array.Empty<string>()));
            }
            return verdicts;
        }

        /// <summary>
        /// Survivor files that mention any of <paramref name="declared"/>. Deliberately textual and
        /// deliberately over-broad — a hit inside a comment or a string literal counts. A false
        /// positive costs only the refusal that is already today's behaviour; a false negative
        /// would run a module missing something a survivor calls, which is the failure this whole
        /// path exists to prevent.
        /// </summary>
        private static IReadOnlyList<string> ReferencesTo(
            IReadOnlyList<DeclaredObject> declared, IReadOnlyList<(string Path, string Text)> survivors)
        {
            var patterns = new List<Regex>();
            foreach (var d in declared)
            {
                // AL identifiers are case-insensitive, and a name with spaces or punctuation can
                // only ever appear quoted. A bare-identifier name can appear either way.
                patterns.Add(new Regex("\"" + Regex.Escape(d.Name) + "\"", RegexOptions.IgnoreCase));
                if (Regex.IsMatch(d.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    patterns.Add(new Regex(@"\b" + Regex.Escape(d.Name) + @"\b", RegexOptions.IgnoreCase));
                // The object id as a standalone integer — `Codeunit.Run(60620)`, `Codeunit::60620`,
                // a row in a test-suite table. This is the one AL spelling the compiler does not
                // check, so it is the only way a survivor can reach a dropped object and still
                // compile (measured; see this file's header).
                if (d.Id >= 0)
                    patterns.Add(new Regex(@"(?<![0-9A-Za-z_.])" + d.Id + @"(?![0-9A-Za-z_.])"));
            }

            var hits = new List<string>();
            foreach (var (path, text) in survivors)
            {
                if (text == null) { hits.Add(path); continue; }
                if (patterns.Any(p => p.IsMatch(text))) hits.Add(path);
            }
            return hits;
        }

        private static string NormalizePath(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return p; }
        }
    }
}
