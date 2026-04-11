using System.Text.RegularExpressions;

namespace AlRunner.Runtime;

/// <summary>
/// Transpile-time registry mapping AL enum object IDs to their declared
/// (ordinal, name) members. Built by walking the raw AL source because
/// the BC compiler inlines pure enums rather than emitting a C# class,
/// so runtime reflection can't recover the member list.
///
/// Populated from <see cref="AlRunner.AlRunnerPipeline"/> after transpile,
/// consumed by <see cref="AlCompat.GetEnumOrdinals"/>.
/// </summary>
public static class EnumRegistry
{
    // enum <objectId> [Extensible]? "<name>" or enum <objectId> <name>
    private static readonly Regex EnumHeader = new(
        @"\benum\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // value(<ordinal>; [quotedName | barename])
    private static readonly Regex ValueMember = new(
        @"\bvalue\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([^\s)]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<int, List<(int Ordinal, string Name)>> _byId = new();

    public static void Clear() => _byId.Clear();

    /// <summary>Parse a single AL source string and register any enum declarations inside it.</summary>
    public static void ParseAndRegister(string alSource)
    {
        var text = alSource;
        var headerMatch = EnumHeader.Match(text);
        while (headerMatch.Success)
        {
            if (!int.TryParse(headerMatch.Groups[1].Value, out var id))
            {
                headerMatch = headerMatch.NextMatch();
                continue;
            }

            // Scan forward from the opening brace for `value(...)` members
            // until the matching close-brace. A full AST parse would be
            // cleaner, but brace-counting is enough for the declaration
            // headers AL uses.
            int depth = 1;
            int i = headerMatch.Index + headerMatch.Length;
            var members = new List<(int, string)>();
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) break; }
                else if (c == 'v' || c == 'V')
                {
                    var slice = text.Substring(i, Math.Min(text.Length - i, 256));
                    var m = ValueMember.Match(slice);
                    if (m.Success && m.Index == 0)
                    {
                        if (int.TryParse(m.Groups[1].Value, out var ord))
                        {
                            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                            members.Add((ord, name));
                        }
                        i += m.Length;
                        continue;
                    }
                }
                i++;
            }

            if (members.Count > 0)
                _byId[id] = members;

            headerMatch = headerMatch.NextMatch();
        }
    }

    public static IReadOnlyList<(int Ordinal, string Name)> GetMembers(int enumObjectId)
    {
        return _byId.TryGetValue(enumObjectId, out var list)
            ? list
            : Array.Empty<(int, string)>();
    }
}
