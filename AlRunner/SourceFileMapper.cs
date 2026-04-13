using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AlRunner;

/// <summary>
/// Maps AL object names to their source file paths.
/// Populated at input-loading time, queried at JSON serialization.
/// Follows the SourceLineMapper pattern: static, built during pipeline setup.
/// </summary>
public static class SourceFileMapper
{
    private static readonly Dictionary<string, string> _objectToFile = new();
    private static readonly Dictionary<string, string> _classToObject = new();

    /// <summary>
    /// Register an AL object name to its source file path.
    /// Called during input loading as each .al file is read.
    /// </summary>
    public static void Register(string objectName, string relativeFilePath)
    {
        _objectToFile[objectName] = relativeFilePath.Replace('\\', '/');
    }

    /// <summary>
    /// Look up the source file for an AL object name.
    /// </summary>
    public static string? GetFile(string objectName)
    {
        return _objectToFile.TryGetValue(objectName, out var file) ? file : null;
    }

    /// <summary>
    /// Resolve a C# scope class name to its AL source file path
    /// via the scope-to-object-to-file chain.
    /// Falls back to prefix matching when the scope name is a method name
    /// without the _Scope suffix (e.g. ValueCapture uses bare method names).
    /// </summary>
    public static string? GetFileForScope(
        string scopeName,
        Dictionary<string, string> scopeToObject)
    {
        // Exact match first
        if (scopeToObject.TryGetValue(scopeName, out var objectName))
            return GetFile(objectName);

        // Prefix match fallback: some capture paths use bare method names (without
        // _Scope_HASH suffix). Find any scopeToObject key that starts with scopeName + "_".
        // Used by iterations (IterationTracker stores scope class names).
        // Captured values bypass this entirely via direct GetFile(objectName).
        var prefix = scopeName + "_";
        foreach (var (key, value) in scopeToObject)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return GetFile(value);
        }

        return null;
    }

    /// <summary>
    /// Register a C# class name to its AL object name.
    /// Called after transpilation to map parent class names back to objects.
    /// </summary>
    public static void RegisterClass(string className, string objectName)
    {
        _classToObject[className] = objectName;
    }

    /// <summary>
    /// Look up the AL object name for a C# class name.
    /// </summary>
    public static string GetObjectForClass(string className)
    {
        return _classToObject.TryGetValue(className, out var obj) ? obj : className;
    }

    /// <summary>
    /// Reset between runs.
    /// </summary>
    public static void Clear()
    {
        _objectToFile.Clear();
        _classToObject.Clear();
    }

    private static readonly Regex ObjectDeclPattern = new(
        @"^(?:codeunit|table|page|report|xmlport|query|enum|enumextension|tableextension|pageextension|interface|permissionset|permissionsetextension|reportextension|profile|controladdin)\s+\d+\s+(?:""([^""]+)""|(\w+))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Parse AL object declarations from source content.
    /// Returns the list of object names found.
    /// Only matches declarations at the start of a line (not in comments or strings).
    /// </summary>
    public static List<string> ParseObjectDeclarations(string content)
    {
        var names = new List<string>();
        foreach (Match m in ObjectDeclPattern.Matches(content))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            names.Add(name);
        }
        return names;
    }
}
