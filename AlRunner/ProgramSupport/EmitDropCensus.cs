// EmitDropCensus — the object count behind Program.cs's PARTIAL-EMIT-DROP guard (#5552).
// The count comes from BC's syntax tree under the compile's own ParseOptions.
using System.Text.RegularExpressions;

namespace AlRunner;

internal static partial class ProgramSupport
{
    internal static class EmitDropCensus
    {
        // An UPPER BOUND on declarations: every declaration contains one of these keywords, so
        // this over-counts and never under-counts. Deliberately no id after the keyword
        // (`codeunit /* c */ 65521 "X"` is a declaration) and case-insensitive (AL keywords are).
        // On the corpus it exceeds the accounted-for count, so every cold compile parses; that
        // costs well under 1% of a run (#5551 review).
        private static readonly Regex PossibleDeclaration = new(
            @"\b(?:table|codeunit|page|report|query|enum|xmlport|tableextension|pageextension|permissionset)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal delegate IReadOnlyList<string>? DeclaredNamesParser(string source, string filePath, string? manifestAppJsonPath);

        /// <summary>The .al files a module was compiled from: the files named in <paramref name="allPaths"/> and every
        /// .al file below a directory named there.</summary>
        internal static IReadOnlyList<string> SourceFiles(IEnumerable<string> allPaths)
        {
            var paths = allPaths as IReadOnlyList<string> ?? allPaths.ToList();
            return paths.Where(File.Exists)
                .Concat(paths.Where(Directory.Exists)
                    .SelectMany(d => AlRunner.Infrastructure.SafeDirectoryScan.Files(d, "*.al")))
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Test seam: BC's silent drop cannot be provoked on demand, and the census tests need a declared object the
        /// compile "did not emit". Removing one emitted source by name is exactly the state the guard exists to catch.
        /// </summary>
        internal static IReadOnlyList<EmittedSource> WithoutTestSeamObject(IReadOnlyList<EmittedSource> sources) =>
            Environment.GetEnvironmentVariable("AL_RUNNER_TEST_DROP_EMITTED_OBJECT") is { Length: > 0 } dropped
                ? sources.Where(s => !string.Equals(s.Name, dropped, StringComparison.Ordinal)).ToList()
                : sources;

        /// <summary>An object the sources declare that the compile neither emitted nor accounted for.</summary>
        internal sealed record PartialDrop(IReadOnlyList<string> Declared, IReadOnlyList<string> Emitted)
        {
            /// <summary>The suite error: <paramref name="prefix"/> is the caller's own lead (`&lt;bundled&gt;: `, or none).</summary>
            internal string Error(string prefix, string moduleName) =>
                $"{prefix}PARTIAL-EMIT-DROP for {moduleName}: {Declared.Count} object(s) declared, " +
                $"only {Emitted.Count} emitted, 0 AL diagnostics explain the gap. Declared: " +
                $"[{string.Join(", ", Declared)}]. Emitted: [{string.Join(", ", Emitted)}].";

            internal string ConsoleLine(string lead, string moduleName) =>
                $"{lead}PARTIAL-EMIT-DROP — {moduleName}: {Declared.Count} declared vs " +
                $"{Emitted.Count} emitted, no diagnostics. Declared: [{string.Join(", ", Declared)}]. " +
                $"Emitted: [{string.Join(", ", Emitted)}].";
        }

        /// <summary>
        /// The one PARTIAL-EMIT-DROP rule the CLI's bundled loop and --server's request path both ask (#5559):
        /// the sources declare more objects than the compile emitted plus <paramref name="accountedFor"/>, the
        /// objects it reported dropping by name. Null when they do not.
        /// </summary>
        internal static PartialDrop? FindPartialDrop(
            IEnumerable<string> allPaths, string? manifestAppJsonPath, IReadOnlyList<string> emittedNames,
            int accountedFor, TextWriter log, DeclaredNamesParser? parse = null)
        {
            var declared = DeclaredBeyond(SourceFiles(allPaths), manifestAppJsonPath, emittedNames.Count + accountedFor, log, parse);
            return declared.Count > emittedNames.Count + accountedFor ? new PartialDrop(declared, emittedNames) : null;
        }

        /// <summary>
        /// The names of the objects <paramref name="files"/> declare, when more than
        /// <paramref name="accountedFor"/> could be; an empty list when the bound says they cannot
        /// exceed it. A file <paramref name="parse"/> cannot read FAILS CLOSED: its upper-bound
        /// matches count as declared, so it can only make the guard fire, never hide a drop, and
        /// it is named on <paramref name="log"/>.
        /// </summary>
        internal static IReadOnlyList<string> DeclaredBeyond(
            IReadOnlyList<string> files, string? manifestAppJsonPath, int accountedFor, TextWriter log,
            DeclaredNamesParser? parse = null)
        {
            parse ??= AlRunner.Infrastructure.AlMemberSyntaxIndex.DeclaredObjectNames;
            var texts = new List<(string File, string Text, int Bound)>(files.Count);
            var upperBound = 0;
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                var bound = PossibleDeclaration.Matches(text).Count;
                texts.Add((f, text, bound));
                upperBound += bound;
            }
            if (upperBound <= accountedFor) return Array.Empty<string>();

            var names = new List<string>();
            foreach (var (file, text, bound) in texts)
            {
                var declared = parse(text, file, manifestAppJsonPath);
                if (declared is null)
                {
                    log.WriteLine($"[emit-census] {file}: BC's parser failed on it, so the PARTIAL-EMIT-DROP guard "
                        + $"counts it conservatively ({bound} possible declaration(s)) and may fire on it.");
                    for (var i = 0; i < bound; i++) names.Add($"<unparsed {Path.GetFileName(file)} #{i + 1}>");
                    continue;
                }
                names.AddRange(declared);
            }
            return names;
        }
    }
}
