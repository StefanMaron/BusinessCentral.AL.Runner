// TestDataBackupProvisioningTests — #4923: auto-provision fetches the backup --test-data needs.
//
// Runner-specific (provisioning), so these stay here rather than in the corpus. The network
// download is replaced through TestDataBackupProvisioning.Download, so nothing here goes online.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(TestDataStaticsSerialCollection.Name)]
public sealed class TestDataBackupProvisioningTests : IDisposable
{
    private const string Version = "28.5.54151.55132";
    private readonly Func<string, string, string, Action<string>, int> _realDownload;
    private readonly string _root;
    private readonly string _home;
    private readonly string _artifacts;
    private readonly List<(string Version, string OutputDir, string Country)> _calls = new();
    private readonly List<string> _reported = new();

    public TestDataBackupProvisioningTests()
    {
        TestDataOptions.ResetForTests();
        _realDownload = TestDataBackupProvisioning.Download;
        _root = TestScratch.Dir("al-runner-4923");
        _home = Path.Combine(_root, "home");
        _artifacts = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_artifacts);
    }

    public void Dispose()
    {
        TestDataBackupProvisioning.Download = _realDownload;
        TestDataOptions.ResetForTests();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A fake that records the call and writes the file the real downloader would.</summary>
    private void FakeSucceeds() =>
        TestDataBackupProvisioning.Download = (version, outputDir, country, log) =>
        {
            _calls.Add((version, outputDir, country));
            Directory.CreateDirectory(outputDir);
            File.WriteAllBytes(Path.Combine(outputDir, TestDataOptions.BackupFileName(country)), new byte[16]);
            log("Written backup");
            return 0;
        };

    private TestDataBackupProvisioning.Outcome Ensure(bool autoProvision, string country = "w1")
        => TestDataBackupProvisioning.EnsureBackup(autoProvision, Version, country, _home, _artifacts,
            _reported.Add, verbose: false);

    [Theory]
    [InlineData("w1", "BusinessCentral-W1.bak")]
    [InlineData("us", "BusinessCentral-US.bak")]
    public void MissingBackup_UnderAutoProvision_DownloadsIntoRunnerArtifactsRoot_WhereResolutionProbes(
        string country, string fileName)
    {
        FakeSucceeds();
        TestDataOptions.TryParseArg("--test-data");

        var outcome = Ensure(autoProvision: true, country);

        Assert.Equal(TestDataBackupProvisioning.Outcome.Downloaded, outcome);
        var call = Assert.Single(_calls);
        Assert.Equal(Version, call.Version);
        Assert.Equal(country, call.Country);
        Assert.Equal(Path.Combine(_artifacts, Version, country), call.OutputDir);

        // The file landed on a path ResolveBackupPath probes, so the run that follows finds it.
        var candidates = TestDataOptions.CandidateBackupPaths(_home, _artifacts, Version, country);
        var expected = Path.Combine(_artifacts, Version, country, fileName);
        Assert.Contains(expected, candidates);
        Assert.True(File.Exists(expected));
        // A ~1 GB download is announced, never silent.
        Assert.Contains(_reported, l => l.Contains("downloading", StringComparison.Ordinal)
                                        && l.Contains(fileName, StringComparison.Ordinal));
    }

    [Fact]
    public void BackupAlreadyInRunnerArtifactsRoot_IsNotDownloadedAgain()
    {
        FakeSucceeds();
        TestDataOptions.TryParseArg("--test-data");
        var existing = Path.Combine(_artifacts, Version, "w1", "BusinessCentral-W1.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, new byte[4]);

        Assert.Equal(TestDataBackupProvisioning.Outcome.AlreadyPresent, Ensure(autoProvision: true));
        Assert.Empty(_calls);
    }

    [Fact]
    public void BackupAlreadyInBcContainerHelperSandboxCache_IsNotDownloaded()
    {
        FakeSucceeds();
        TestDataOptions.TryParseArg("--test-data");
        // The first probed path, the BcContainerHelper sandbox cache under the home directory.
        var existing = TestDataOptions.CandidateBackupPaths(_home, _artifacts, Version, "w1")[0];
        Assert.StartsWith(_home, existing, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, new byte[4]);

        Assert.Equal(TestDataBackupProvisioning.Outcome.AlreadyPresent, Ensure(autoProvision: true));
        Assert.Empty(_calls);
    }

    [Fact]
    public void NoAutoProvision_NeverDownloads()
    {
        FakeSucceeds();
        TestDataOptions.TryParseArg("--test-data");

        Assert.Equal(TestDataBackupProvisioning.Outcome.NotRequested, Ensure(autoProvision: false));
        Assert.Empty(_calls);
    }

    [Fact]
    public void ExplicitBackupPath_NeverDownloads()
    {
        FakeSucceeds();
        TestDataOptions.TryParseArg("--test-data=/nowhere/BusinessCentral-W1.bak");

        Assert.Equal(TestDataBackupProvisioning.Outcome.NotRequested, Ensure(autoProvision: true));
        Assert.Empty(_calls);
    }

    [Fact]
    public void WithoutTestData_NeverDownloads()
    {
        FakeSucceeds();

        Assert.Equal(TestDataBackupProvisioning.Outcome.NotRequested, Ensure(autoProvision: true));
        Assert.Empty(_calls);
    }

    [Fact]
    public void DownloaderFailure_ThrowsNamingTheUrlAndTheReason_OnTheFirstLine()
    {
        TestDataBackupProvisioning.Download = (version, outputDir, country, log) =>
        {
            _calls.Add((version, outputDir, country));
            log("Resolving artifact size...");
            log("Error: no BC artifact published for 28.5.54151.55132 (w1): https://example.invalid/x.");
            log("       Check the version, or resolve the latest for a prefix:");
            return 1;
        };
        TestDataOptions.TryParseArg("--test-data");

        var ex = Assert.Throws<TestDataUnavailableException>(() => Ensure(autoProvision: true));

        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains($"{AlRunner.Provisioning.ArtifactDownloader.CdnBase}/{Version}/w1", firstLine, StringComparison.Ordinal);
        Assert.Contains("Error: no BC artifact published", firstLine, StringComparison.Ordinal);
        Assert.DoesNotContain("..", firstLine, StringComparison.Ordinal);
        Assert.Single(_calls);
        Assert.False(File.Exists(Path.Combine(_artifacts, Version, "w1", "BusinessCentral-W1.bak")));
    }

    [Fact]
    public void DownloaderReportingSuccessWithoutAFile_IsStillAFailure()
    {
        TestDataBackupProvisioning.Download = (version, outputDir, country, log) => 0;
        TestDataOptions.TryParseArg("--test-data");

        var ex = Assert.Throws<TestDataUnavailableException>(() => Ensure(autoProvision: true));
        Assert.Contains("BusinessCentral-W1.bak", ex.Message.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public void DownloaderThrowing_IsWrappedWithTheUrl()
    {
        TestDataBackupProvisioning.Download = (version, outputDir, country, log)
            => throw new HttpRequestException("connection reset");
        TestDataOptions.TryParseArg("--test-data");

        var ex = Assert.Throws<TestDataUnavailableException>(() => Ensure(autoProvision: true));
        var firstLine = ex.Message.Split('\n')[0];
        Assert.Contains($"{AlRunner.Provisioning.ArtifactDownloader.CdnBase}/{Version}/w1", firstLine, StringComparison.Ordinal);
        Assert.Contains("connection reset", firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBackupMessage_TellsHowToProvision()
    {
        var candidates = TestDataOptions.CandidateBackupPaths(_home, _artifacts, Version, "w1");
        var message = TestDataOptions.MissingBackupMessage(Version, "w1", candidates);

        Assert.DoesNotContain('\n', message);
        Assert.Contains("al-runner provision --test-data", message, StringComparison.Ordinal);
        Assert.Contains("--no-auto-provision", message, StringComparison.Ordinal);
    }
}
