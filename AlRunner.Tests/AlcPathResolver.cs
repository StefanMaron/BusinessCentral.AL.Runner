namespace AlRunner.Tests;

/// <summary>
/// Resolves the BC AL compiler (alc) binary used by tests that build a real
/// dependency .app fixture. Honors AL_COMPILER_PATH and falls back to the
/// VS Code AL extension layout in ~/.vscode/extensions/ms-dynamics-smb.al-*.
/// macOS is intentionally routed to the linux/alc binary: the AL extension
/// does ship a darwin build, but no contributor on this repo runs macOS, so
/// the darwin path is left unverified and not added speculatively.
/// </summary>
internal static class AlcPathResolver
{
    public static string? Default { get; } = ResolveDefault();

    public static string CurrentOsPlatform()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "OSX";
        return "Linux";
    }

    public static string? ResolveAlcPath(string? alCompilerPath, string vscodeExtensionsPath, string osPlatform)
    {
        if (alCompilerPath != null && File.Exists(alCompilerPath)) return alCompilerPath;
        if (!Directory.Exists(vscodeExtensionsPath)) return null;

        var (platformDir, fileName) = osPlatform switch
        {
            "Windows" => ("win32", "alc.exe"),
            "Linux" or "OSX" => ("linux", "alc"),
            _ => ("", "")
        };
        if (platformDir == "") return null;

        foreach (var extDir in Directory.GetDirectories(vscodeExtensionsPath, "ms-dynamics-smb.al-*"))
        {
            var candidate = Path.Combine(extDir, "bin", platformDir, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolveDefault()
    {
        var vscodePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions");

        return ResolveAlcPath(
            Environment.GetEnvironmentVariable("AL_COMPILER_PATH"),
            vscodePath,
            CurrentOsPlatform());
    }
}
