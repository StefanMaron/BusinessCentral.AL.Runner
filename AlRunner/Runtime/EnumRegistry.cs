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
    // enum <objectId> <quoted-or-bare-name> [implements <iface-list>]? {
    // Non-greedy `[^{]*?` swallows any `implements "Iface"` / extensible modifier
    // or attributes between the enum name and the opening brace.
    private static readonly Regex EnumHeader = new(
        @"\benum\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))[^{]*?\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // value(<ordinal>; [quotedName | barename])
    private static readonly Regex ValueMember = new(
        @"\bvalue\s*\(\s*(\d+)\s*;\s*(?:""([^""]+)""|([^\s)]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Implementation = "IfaceName" = "CodeunitName"  (quotes optional around each)
    private static readonly Regex ImplementationLine = new(
        @"\bImplementation\s*=\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))\s*=\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Inline Option field members: `OptionMembers = A,B,C;`
    private static readonly Regex OptionMembersLine = new(
        @"\bOptionMembers\s*=\s*([^;]+);",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // `Extensible = false;` inside an enum body
    private static readonly Regex ExtensibleFalse = new(
        @"\bExtensible\s*=\s*false\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<int, List<(int Ordinal, string Name)>> _byId = new();
    // Enum AL name (e.g. "IV Mode" or "Simple Enum") -> list of members.
    // Populated alongside _byId so callers that only know the type name
    // (typically via a field InitValue expression) can resolve members.
    private static readonly Dictionary<string, List<(int Ordinal, string Name)>> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks enum object IDs that are declared with Extensible = false.
    // Only these enums have a closed set of ordinals and can be validated.
    private static readonly HashSet<int> _nonExtensible = new();

    // (enumId, ordinal) -> (interfaceName, codeunitName) from
    // `Implementation = "Iface" = "Codeunit"` blocks inside enum value bodies.
    private static readonly Dictionary<(int EnumId, int Ordinal), List<(string IfaceName, string CodeunitName)>> _implsById = new();
    // Parallel lookup keyed by enum name for callers that only know the name.
    private static readonly Dictionary<(string EnumName, int Ordinal), List<(string IfaceName, string CodeunitName)>> _implsByName =
        new(new EnumNameOrdinalComparer());

    private sealed class EnumNameOrdinalComparer : IEqualityComparer<(string EnumName, int Ordinal)>
    {
        public bool Equals((string EnumName, int Ordinal) a, (string EnumName, int Ordinal) b)
            => string.Equals(a.EnumName, b.EnumName, StringComparison.OrdinalIgnoreCase) && a.Ordinal == b.Ordinal;
        public int GetHashCode((string EnumName, int Ordinal) x)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(x.EnumName ?? ""), x.Ordinal);
    }

    public static void Clear()
    {
        _byId.Clear();
        _byName.Clear();
        _implsById.Clear();
        _implsByName.Clear();
        _inlineOptionMembers.Clear();
        _nonExtensible.Clear();
    }

    /// <summary>
    /// Resolve an <c>Implementation = "Iface" = "Codeunit"</c> mapping for a
    /// given enum object+ordinal. Used by <see cref="MockInterfaceHandle"/>
    /// to turn a NavOption into a callable codeunit when the AL code
    /// assigns an enum value to an interface variable.
    /// </summary>
    public static string? GetImplementationCodeunitName(int enumObjectId, int ordinal, string? interfaceName = null)
    {
        if (!_implsById.TryGetValue((enumObjectId, ordinal), out var list)) return null;
        if (interfaceName is null) return list.FirstOrDefault().CodeunitName;
        foreach (var e in list)
            if (string.Equals(e.IfaceName, interfaceName, StringComparison.OrdinalIgnoreCase))
                return e.CodeunitName;
        return list.FirstOrDefault().CodeunitName;
    }

    /// <summary>
    /// Last-resort lookup used when we only know the ordinal and can't
    /// recover the enum identity — returns the first registered
    /// implementation that matches the ordinal across all enums. This
    /// fires when the rewriter stripped <c>NCLEnumMetadata.Create(N)</c>
    /// and the NavOption's metadata no longer carries the enum object id.
    /// </summary>
    public static string? FindAnyImplementationCodeunit(int ordinal)
    {
        foreach (var kv in _implsById)
        {
            if (kv.Key.Ordinal == ordinal)
                return kv.Value.FirstOrDefault().CodeunitName;
        }
        return null;
    }

    /// <summary>Same as <see cref="GetImplementationCodeunitName"/> keyed by enum name.</summary>
    public static string? GetImplementationCodeunitNameByEnumName(string enumName, int ordinal, string? interfaceName = null)
    {
        if (!_implsByName.TryGetValue((enumName, ordinal), out var list)) return null;
        if (interfaceName is null) return list.FirstOrDefault().CodeunitName;
        foreach (var e in list)
            if (string.Equals(e.IfaceName, interfaceName, StringComparison.OrdinalIgnoreCase))
                return e.CodeunitName;
        return list.FirstOrDefault().CodeunitName;
    }

    /// <summary>Resolve a specific member's ordinal by (enum name, member name).</summary>
    public static int? GetOrdinalByName(string enumName, string memberName)
    {
        if (!_byName.TryGetValue(enumName, out var list)) return null;
        foreach (var (ord, name) in list)
        {
            if (string.Equals(name, memberName, StringComparison.OrdinalIgnoreCase))
                return ord;
        }
        return null;
    }

    // Inline Option fields: register member names into a synthetic
    // "option member pool" keyed by member name so filter normalization
    // can resolve `<>Red` to `<>0` regardless of which field declares it.
    // Multiple Option fields with the same member name at different
    // ordinals is a degenerate case we don't try to disambiguate; the
    // first-parsed wins. Filters on fields that share a namespace still
    // work because BC forbids re-using Option member names within a
    // single field.
    private static readonly Dictionary<string, int> _inlineOptionMembers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve an AL Option member name (e.g. "Red") to its ordinal.
    /// Searches inline <c>OptionMembers = ...</c> first (field-level declarations
    /// take priority), then falls back to the <c>enum</c> object registry.
    /// </summary>
    public static int? FindOrdinalByMemberName(string memberName)
    {
        // Inline OptionMembers (from table field declarations) take priority over
        // named enum objects, because the caller is resolving a filter literal for
        // a specific Option field whose members are the inline ones.
        if (_inlineOptionMembers.TryGetValue(memberName, out var inlineOrd))
            return inlineOrd;
        foreach (var members in _byId.Values)
            foreach (var (ord, name) in members)
                if (string.Equals(name, memberName, StringComparison.OrdinalIgnoreCase))
                    return ord;
        return null;
    }

    /// <summary>Parse a single AL source string and register any enum declarations inside it.</summary>
    public static void ParseAndRegister(string alSource)
    {
        // First harvest inline `OptionMembers = A,B,C;` definitions from
        // field blocks — they aren't enum objects but AL treats them the
        // same way for filter comparisons.
        foreach (Match om in OptionMembersLine.Matches(alSource))
        {
            var list = om.Groups[1].Value;
            var parts = list.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                var name = parts[i].Trim().Trim('"');
                if (name.Length == 0) continue;
                _inlineOptionMembers[name] = i;
            }
        }

        var text = alSource;
        var headerMatch = EnumHeader.Match(text);
        while (headerMatch.Success)
        {
            if (!int.TryParse(headerMatch.Groups[1].Value, out var id))
            {
                headerMatch = headerMatch.NextMatch();
                continue;
            }
            var enumName = headerMatch.Groups[2].Success
                ? headerMatch.Groups[2].Value
                : headerMatch.Groups[3].Value;

            // Parse the enum body. We need both the list of (ordinal, name)
            // members and, for each value block, any `Implementation = ...`
            // attributes. Use a depth-tracking walk so nested `{}` in value
            // bodies don't throw off the end-of-enum detection.
            int depth = 1;
            int i = headerMatch.Index + headerMatch.Length;
            int enumBodyStart = i;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') { depth--; if (depth == 0) break; }
                i++;
            }
            if (depth != 0) { headerMatch = headerMatch.NextMatch(); continue; }
            var enumBody = text.Substring(enumBodyStart, i - enumBodyStart);

            var members = new List<(int, string)>();
            // Iterate value headers explicitly so we can pair each with the
            // body that immediately follows it.
            foreach (Match m in ValueMember.Matches(enumBody))
            {
                if (!int.TryParse(m.Groups[1].Value, out var ord)) continue;
                var memberName = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                members.Add((ord, memberName));

                // Walk forward from the match end to find the value's body
                // `{ ... }`. The regex stops at the member-name capture
                // (before the closing `)` of the value(...) header), so
                // skip any whitespace/closing-paren/comma etc. until either
                // the body brace or the next `value(` header.
                int j = m.Index + m.Length;
                while (j < enumBody.Length)
                {
                    if (enumBody[j] == '{') break;
                    // Stop if we run into the next value(...) header so we
                    // don't misattribute attributes to the wrong enum value.
                    if (j + 6 <= enumBody.Length &&
                        string.Equals(enumBody.Substring(j, 6), "value(",
                            StringComparison.OrdinalIgnoreCase))
                        break;
                    j++;
                }
                if (j >= enumBody.Length || enumBody[j] != '{') continue;
                int bd = 1;
                int bs = j + 1;
                j++;
                while (j < enumBody.Length && bd > 0)
                {
                    if (enumBody[j] == '{') bd++;
                    else if (enumBody[j] == '}') { bd--; if (bd == 0) break; }
                    j++;
                }
                if (bd != 0) break;
                var valueBody = enumBody.Substring(bs, j - bs);

                foreach (Match impl in ImplementationLine.Matches(valueBody))
                {
                    var ifaceName = impl.Groups[1].Success ? impl.Groups[1].Value : impl.Groups[2].Value;
                    var cuName = impl.Groups[3].Success ? impl.Groups[3].Value : impl.Groups[4].Value;

                    if (!_implsById.TryGetValue((id, ord), out var listById))
                        _implsById[(id, ord)] = listById = new List<(string, string)>();
                    listById.Add((ifaceName, cuName));

                    if (!_implsByName.TryGetValue((enumName, ord), out var listByName))
                        _implsByName[(enumName, ord)] = listByName = new List<(string, string)>();
                    listByName.Add((ifaceName, cuName));
                }
            }

            if (members.Count > 0)
            {
                _byId[id] = members;
                _byName[enumName] = members;
            }

            // Track whether this enum is explicitly non-extensible so
            // CreateTaggedOption can validate ordinals only when safe to do so.
            if (ExtensibleFalse.IsMatch(enumBody))
                _nonExtensible.Add(id);

            headerMatch = headerMatch.NextMatch();
        }
    }

    /// <summary>
    /// Returns true when the enum with the given AL object ID was declared
    /// with <c>Extensible = false</c> — meaning all valid ordinals are known
    /// at compile time and runtime validation is safe.
    /// </summary>
    public static bool IsNonExtensible(int enumObjectId) => _nonExtensible.Contains(enumObjectId);

    public static IReadOnlyList<(int Ordinal, string Name)> GetMembers(int enumObjectId)
    {
        return _byId.TryGetValue(enumObjectId, out var list)
            ? list
            : Array.Empty<(int, string)>();
    }

    /// <summary>Look up enum members by AL enum name (case-insensitive).</summary>
    public static IReadOnlyList<(int Ordinal, string Name)> GetMembersByName(string enumName)
    {
        return _byName.TryGetValue(enumName, out var list)
            ? list
            : Array.Empty<(int, string)>();
    }
}
