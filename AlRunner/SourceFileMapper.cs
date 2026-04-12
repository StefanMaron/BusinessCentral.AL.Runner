namespace AlRunner;

/// <summary>
/// Maps AL object names to their source file paths.
/// Populated at input-loading time, queried at JSON serialization.
/// Follows the SourceLineMapper pattern: static, built during pipeline setup.
/// </summary>
public static class SourceFileMapper
{
    private static readonly Dictionary<string, string> _objectToFile = new();

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
    /// </summary>
    public static string? GetFileForScope(
        string scopeName,
        Dictionary<string, string> scopeToObject)
    {
        if (!scopeToObject.TryGetValue(scopeName, out var objectName))
            return null;
        return GetFile(objectName);
    }

    /// <summary>
    /// Reset between runs.
    /// </summary>
    public static void Clear()
    {
        _objectToFile.Clear();
    }
}
