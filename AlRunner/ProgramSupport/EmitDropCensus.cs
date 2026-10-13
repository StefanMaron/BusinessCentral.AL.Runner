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
