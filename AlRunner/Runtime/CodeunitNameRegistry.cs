using System.Text.RegularExpressions;

namespace AlRunner.Runtime;

/// <summary>
/// Transpile-time map of AL <c>codeunit N "Name"</c> declarations so runtime
/// code that only knows the object name (e.g. enum-implements-interface
/// dispatch in <see cref="MockInterfaceHandle"/>) can resolve back to a
/// concrete <see cref="MockCodeunitHandle"/>.
/// </summary>
public static class CodeunitNameRegistry
{
    private static readonly Regex CodeunitHeader = new(
        @"\bcodeunit\s+(\d+)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> _nameToId = new(StringComparer.OrdinalIgnoreCase);

    public static void Clear() => _nameToId.Clear();

    public static void ParseAndRegister(string alSource)
    {
        foreach (Match m in CodeunitHeader.Matches(alSource))
        {
            if (!int.TryParse(m.Groups[1].Value, out var id)) continue;
            var name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            _nameToId[name] = id;
        }
    }

    public static int? GetIdByName(string codeunitName)
    {
        return _nameToId.TryGetValue(codeunitName, out var id) ? id : null;
    }
}
