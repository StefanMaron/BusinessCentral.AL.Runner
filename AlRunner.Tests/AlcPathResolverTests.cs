using Xunit;

namespace AlRunner.Tests;

public class AlcPathResolverTests
{
    [Fact]
    public void ResolveAlcPath_Windows_UsesWin32Compiler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-alcpath-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var extensionsDir = Path.Combine(dir, "extensions");
            var expected = CreateFakeAlc(extensionsDir, "ms-dynamics-smb.al-18.0.0", "win32", "alc.exe");

            var actual = AlcPathResolver.ResolveAlcPath(null, extensionsDir, "Windows");

            Assert.Equal(expected, actual);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAlcPath_Windows_IgnoresLinuxCompiler()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-alcpath-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var extensionsDir = Path.Combine(dir, "extensions");
            CreateFakeAlc(extensionsDir, "ms-dynamics-smb.al-18.0.0", "linux", "alc");

            var actual = AlcPathResolver.ResolveAlcPath(null, extensionsDir, "Windows");

            Assert.Null(actual);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveAlcPath_ExistingEnvPathWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-alcpath-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var envPath = Path.Combine(dir, "custom-alc.exe");
            File.WriteAllText(envPath, "");
            var extensionsDir = Path.Combine(dir, "extensions");
            CreateFakeAlc(extensionsDir, "ms-dynamics-smb.al-18.0.0", "win32", "alc.exe");

            var actual = AlcPathResolver.ResolveAlcPath(envPath, extensionsDir, "Windows");

            Assert.Equal(envPath, actual);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("OSX")]
    public void ResolveAlcPath_Unix_UsesLinuxCompiler(string osPlatform)
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-alcpath-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var extensionsDir = Path.Combine(dir, "extensions");
            var expected = CreateFakeAlc(extensionsDir, "ms-dynamics-smb.al-18.0.0", "linux", "alc");

            var actual = AlcPathResolver.ResolveAlcPath(null, extensionsDir, osPlatform);

            Assert.Equal(expected, actual);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static string CreateFakeAlc(string extensionsDir, string extensionName, string platformDir, string fileName)
    {
        var alcPath = Path.Combine(extensionsDir, extensionName, "bin", platformDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(alcPath)!);
        File.WriteAllText(alcPath, "");
        return alcPath;
    }
}
