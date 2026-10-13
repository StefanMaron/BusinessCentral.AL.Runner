// EmitDropCensus — the object count behind Program.cs's PARTIAL-EMIT-DROP guard (#5552).
//
// The guard asks "were more objects declared than the compile accounts for?". The count used to
// be a line-anchored regex over the file text, which counts a header inside a block comment
// and cannot see a capitalised keyword or a header with anything after it on its line. The
// answer now comes from BC's syntax tree, under the options the compile of the file used.
using System.Text.RegularExpressions;

namespace AlRunner;

internal static partial class ProgramSupport
{
    internal static class EmitDropCensus
    {
        // A cheap UPPER BOUND, only to avoid parsing every file of every compile: every object
        // declaration contains `<kind> <digits>`, so this over-counts and never under-counts.
        // Case-insensitive because AL keywords are. It decides nothing about WHICH objects are
        // declared; the parse below does.
        private static readonly Regex PossibleDeclaration = new(
            @"\b(?:table|codeunit|page|report|query|enum|xmlport|tableextension|pageextension|permissionset)\s+\d+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The names of the objects <paramref name="files"/> declare, when more than
        /// <paramref name="accountedFor"/> could be; an empty list when the cheap bound says they
        /// cannot exceed it. A file whose parse throws is reported on stderr and left out, never
        /// counted as declaring nothing in silence.
        /// </summary>
        internal static IReadOnlyList<string> DeclaredBeyond(
            IReadOnlyList<string> files, string? manifestAppJsonPath, int accountedFor, TextWriter log)
        {
            var texts = new List<(string File, string Text)>(files.Count);
            var upperBound = 0;
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                texts.Add((f, text));
                upperBound += PossibleDeclaration.Matches(text).Count;
            }
            if (upperBound <= accountedFor) return Array.Empty<string>();

            var names = new List<string>();
            foreach (var (file, text) in texts)
            {
                var declared = AlRunner.Infrastructure.AlMemberSyntaxIndex.DeclaredObjectNames(text, file, manifestAppJsonPath);
                if (declared is null)
                {
                    log.WriteLine($"[emit-census] {file}: BC's parser failed on it, so the objects it declares are not "
                        + "counted by the PARTIAL-EMIT-DROP guard.");
                    continue;
                }
                names.AddRange(declared);
            }
            return names;
        }
    }
}
