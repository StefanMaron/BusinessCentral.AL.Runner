// #5263: which cached dependency compiles --tdd refuses to be served. The predicate reads the cached
// emit-exclusion report next to the DLL; these cases run in milliseconds, with no runner subprocess.
using Xunit;

namespace AlRunner.Tests;

public sealed class CachedDropWasAMissingMemberTests : IDisposable
{
    private readonly string _dir = TestScratch.Dir("al-runner-cached-drop-missing-member");

    public CachedDropWasAMissingMemberTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir))
                try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    private string Report(string text)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".emit-excluded.txt");
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("error AL0132: 'Codeunit \"X\"' does not contain a definition for 'Missing'")]
    [InlineData("error AL0126: No overload for method 'Missing' takes 1 arguments")]
    [InlineData("error AL0185: Codeunit 'Y' is missing | error AL0132: 'Z' does not contain a definition for 'W'")]
    public void AReportNamingAMissingMember_IsOneToRecompile(string report)
        => Assert.True(DependencyLoader.CachedDropWasAMissingMember(Report(report)));

    [Theory]
    [InlineData("error AL0118: The name 'X' does not exist in the current context")]
    [InlineData("error AL0185: Codeunit 'Y' is missing")]
    [InlineData("")]
    public void AReportNamingOnlyOtherDiagnostics_IsServedAsBefore(string report)
        => Assert.False(DependencyLoader.CachedDropWasAMissingMember(Report(report)));

    [Fact]
    public void NoReport_MeansACompleteCompile()
        => Assert.False(DependencyLoader.CachedDropWasAMissingMember(Path.Combine(_dir, "absent.emit-excluded.txt")));

    /// <summary>
    /// A report that exists and cannot be read is not classified as complete. On Linux a file with no
    /// read permission throws UnauthorizedAccessException, which is not an IOException.
    /// </summary>
    [SkippableFact]
    public void AnUnreadableReport_IsOneToRecompile()
    {
        var path = Report("error AL0132: x");
        try { File.SetUnixFileMode(path, UnixFileMode.None); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException) { }
        bool Readable() { try { File.ReadAllText(path); return true; } catch (Exception) { return false; } }
        Skip.If(Readable(), "permission bits do not bite here (running as root?)");

        Assert.True(DependencyLoader.CachedDropWasAMissingMember(path));
    }
}
