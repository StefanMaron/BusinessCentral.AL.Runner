// BackupReaderProvisioningTests — #4925: auto-provision installs the pinned backup reader that
// --test-data needs. Runner-specific (provisioning), so these stay here rather than in the corpus.
// Every fetch goes to a local fake release directory; nothing here goes online.
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BackupReaderProvisioningTests : IDisposable
{
    private const string Repo = "Example/Reader";
    private const string Tag = "v9.8.7";
    private const string Platform = "linux-x64";
    private const string AssetFile = "bcdb-linux-x64";

    private static readonly byte[] PinnedBytes = "the pinned reader build"u8.ToArray();
    private static readonly byte[] OtherBytes = "an older reader build"u8.ToArray();

    private readonly string _root;
    private readonly string _release;
    private readonly string _cacheRoot;
    private readonly List<string> _fetched = new();
    private readonly List<string> _reported = new();

    public BackupReaderProvisioningTests()
    {
        _root = TestScratch.Dir("al-runner-4925");
        _release = Path.Combine(_root, "release");
        _cacheRoot = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_release);
        Directory.CreateDirectory(_cacheRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static BackupReaderPin PinFor(byte[] bytes, string? sha = "computed")
        => new(Repo, Tag, new Dictionary<string, BackupReaderAsset>
        {
            [Platform] = new(AssetFile, sha == "computed" ? Sha(bytes) : sha),
        });

    /// <summary>A fake release: serves files under <see cref="_release"/> by asset name, and
    /// answers NotFound for anything the directory does not hold.</summary>
    private BackupReaderProvisioning.FetchStatus FakeFetch(string url, string destination)
    {
        _fetched.Add(url);
        var source = Path.Combine(_release, url[(url.LastIndexOf('/') + 1)..]);
        if (!File.Exists(source)) return BackupReaderProvisioning.FetchStatus.NotFound;
        File.Copy(source, destination, overwrite: true);
        return BackupReaderProvisioning.FetchStatus.Ok;
    }

    private void Publish(byte[] bytes) => File.WriteAllBytes(Path.Combine(_release, AssetFile), bytes);

    private string Managed => BackupReaderTool.ManagedInstallPath(_cacheRoot)!;

    private void PreInstall(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Managed)!);
        File.WriteAllBytes(Managed, bytes);
    }

    private BackupReaderProvisioning.Outcome Ensure(BackupReaderPin pin, bool testData = true,
        bool autoProvision = true, string? env = null, string? onPath = null, string? platform = Platform,
        BackupReaderProvisioning.Fetcher? fetch = null)
        => BackupReaderProvisioning.EnsureReader(testData, autoProvision, env, _cacheRoot, () => onPath,
            pin, platform, fetch ?? FakeFetch, _reported.Add);

    // ───────────────────────────────────────────── installs, and leaves alone ──

    [Fact]
    public void MissingReader_FetchesThePinnedTagForThisPlatform_AndInstallsWhereResolutionProbes()
    {
        Publish(PinnedBytes);

        var outcome = Ensure(PinFor(PinnedBytes));

        Assert.Equal(BackupReaderProvisioning.Outcome.Installed, outcome);
        Assert.Equal($"https://github.com/{Repo}/releases/download/{Tag}/{AssetFile}", Assert.Single(_fetched));
        Assert.Equal(PinnedBytes, File.ReadAllBytes(Managed));
        // The slot is one BackupReaderTool.Resolve probes, so the run that follows finds it.
        Assert.Contains(Managed, BackupReaderTool.CandidateExecutables(null, _cacheRoot));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Managed).HasFlag(UnixFileMode.UserExecute));
        // No temp file left beside it.
        Assert.Equal(new[] { Managed }, Directory.GetFiles(Path.GetDirectoryName(Managed)!));
        Assert.Contains(_reported, l => l.Contains(Tag, StringComparison.Ordinal)
                                        && l.Contains("installed", StringComparison.Ordinal));
    }

    [Fact]
    public void CorrectReaderAlreadyInstalled_IsLeftAlone()
    {
        Publish(PinnedBytes);
        PreInstall(PinnedBytes);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Managed, stamp);

        Assert.Equal(BackupReaderProvisioning.Outcome.AlreadyPresent, Ensure(PinFor(PinnedBytes)));
        Assert.Empty(_fetched);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Managed));
    }

    [Fact]
    public void ReaderAtAnotherVersion_IsReplacedWithThePinnedOne()
    {
        Publish(PinnedBytes);
        PreInstall(OtherBytes);

        Assert.Equal(BackupReaderProvisioning.Outcome.Replaced, Ensure(PinFor(PinnedBytes)));
        Assert.Single(_fetched);
        Assert.Equal(PinnedBytes, File.ReadAllBytes(Managed));
        Assert.Contains(_reported, l => l.Contains("replac", StringComparison.OrdinalIgnoreCase)
                                        && l.Contains(Tag, StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────── never fetches ──

    [Fact]
    public void NoAutoProvision_NeverFetches()
    {
        Publish(PinnedBytes);

        Assert.Equal(BackupReaderProvisioning.Outcome.NotRequested, Ensure(PinFor(PinnedBytes), autoProvision: false));
        Assert.Empty(_fetched);
        Assert.False(File.Exists(Managed));
    }

    [Fact]
    public void WithoutTestData_NeverFetches()
    {
        Publish(PinnedBytes);

        Assert.Equal(BackupReaderProvisioning.Outcome.NotRequested, Ensure(PinFor(PinnedBytes), testData: false));
        Assert.Empty(_fetched);
        Assert.False(File.Exists(Managed));
    }

    [Fact]
    public void ReaderNamedByTheEnvVar_IsTheUsers_AndTheCacheSlotIsNotTouched()
    {
        Publish(PinnedBytes);
        PreInstall(OtherBytes);
        var own = Path.Combine(_root, "own-reader");
        File.WriteAllBytes(own, OtherBytes);

        Assert.Equal(BackupReaderProvisioning.Outcome.UserSupplied, Ensure(PinFor(PinnedBytes), env: own));
        Assert.Empty(_fetched);
        Assert.Equal(OtherBytes, File.ReadAllBytes(Managed));
    }

    [Fact]
    public void EnvVarNamingNothing_FallsThroughToTheCacheSlot_AsResolutionDoes()
    {
        Publish(PinnedBytes);

        Assert.Equal(BackupReaderProvisioning.Outcome.Installed,
            Ensure(PinFor(PinnedBytes), env: Path.Combine(_root, "no-such-reader")));
        Assert.Equal(PinnedBytes, File.ReadAllBytes(Managed));
    }

    [Fact]
    public void ReaderOnPath_WithAnEmptyCacheSlot_IsTheUsers_AndIsNotShadowed()
    {
        Publish(PinnedBytes);

        Assert.Equal(BackupReaderProvisioning.Outcome.UserSupplied,
            Ensure(PinFor(PinnedBytes), onPath: "/usr/local/bin/bcbak"));
        Assert.Empty(_fetched);
        Assert.False(File.Exists(Managed));
    }

    // ───────────────────────────────────────── refuses, each by its own name ──

    [Fact]
    public void ChecksumMismatch_RefusesLoudly_AndInstallsNothing()
    {
        Publish(OtherBytes);

        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(PinFor(PinnedBytes)));

        Assert.Equal(BackupReaderProvisioningFailure.ChecksumMismatch, ex.Failure);
        Assert.Contains(Sha(PinnedBytes), ex.Message, StringComparison.Ordinal);
        Assert.Contains(Sha(OtherBytes), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.False(File.Exists(Managed));
        Assert.Empty(Directory.Exists(Path.GetDirectoryName(Managed)!)
            ? Directory.GetFiles(Path.GetDirectoryName(Managed)!) : Array.Empty<string>());
    }

    [Fact]
    public void ChecksumMismatch_LeavesAnAlreadyInstalledReaderAsItWas()
    {
        Publish(OtherBytes);
        var previous = "a third build"u8.ToArray();
        PreInstall(previous);

        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(PinFor(PinnedBytes)));

        Assert.Equal(BackupReaderProvisioningFailure.ChecksumMismatch, ex.Failure);
        Assert.Equal(previous, File.ReadAllBytes(Managed));
        Assert.Equal(new[] { Managed }, Directory.GetFiles(Path.GetDirectoryName(Managed)!));
    }

    [Fact]
    public void AssetTheReleaseDoesNotPublish_IsAssetMissing_NamingTheUrl()
    {
        // Nothing published.
        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(PinFor(PinnedBytes)));

        Assert.Equal(BackupReaderProvisioningFailure.AssetMissing, ex.Failure);
        Assert.Contains($"https://github.com/{Repo}/releases/download/{Tag}/{AssetFile}", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Managed));
    }

    [Fact]
    public void UnreachableNetwork_IsUnreachable_NotAMismatchOrAMissingAsset()
    {
        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(PinFor(PinnedBytes),
            fetch: (url, dest) => throw new HttpRequestException("Name or service not known")));

        Assert.Equal(BackupReaderProvisioningFailure.Unreachable, ex.Failure);
        Assert.Contains("Name or service not known", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Managed));
    }

    [Theory]
    [InlineData("linux-riscv64")]
    [InlineData(null)]
    public void PlatformTheReleaseDoesNotPublish_RefusesWithoutFetching(string? platform)
    {
        Publish(PinnedBytes);

        var ex = Assert.Throws<BackupReaderProvisioningException>(
            () => Ensure(PinFor(PinnedBytes), platform: platform));

        Assert.Equal(BackupReaderProvisioningFailure.UnsupportedPlatform, ex.Failure);
        Assert.Contains(Platform, ex.Message, StringComparison.Ordinal);       // names what IS published
        Assert.Contains(BackupReaderTool.ExecutableEnvVar, ex.Message, StringComparison.Ordinal);
        Assert.Empty(_fetched);
    }

    [Fact]
    public void PinWithoutAChecksumForThisPlatform_RefusesWithoutFetching()
    {
        Publish(PinnedBytes);

        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(PinFor(PinnedBytes, sha: null)));

        Assert.Equal(BackupReaderProvisioningFailure.ChecksumMissing, ex.Failure);
        Assert.Empty(_fetched);
        Assert.False(File.Exists(Managed));
    }

    [Fact]
    public void PinWithAMalformedChecksum_RefusesWithoutFetching()
    {
        Publish(PinnedBytes);

        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(PinFor(PinnedBytes, sha: "abc")));

        Assert.Equal(BackupReaderProvisioningFailure.ChecksumMissing, ex.Failure);
        Assert.Empty(_fetched);
    }

    // ────────────────────────────────────────────────────── the one pin ──

    [Fact]
    public void EmbeddedPin_IsTheCheckedInFile_AndRequestsItsOwnTag()
    {
        var file = BackupReaderPin.Parse(File.ReadAllText(Path.Combine(RepoRoot, ".github", "backup-reader.json")));
        var embedded = BackupReaderPin.Embedded;

        Assert.Equal(file.Repository, embedded.Repository);
        Assert.Equal(file.Tag, embedded.Tag);
        Assert.Equal(file.Assets.OrderBy(a => a.Key), embedded.Assets.OrderBy(a => a.Key));
        Assert.Equal("StefanMaron/BusinessCentral.DbReader", embedded.Repository);

        // The fetch the runner makes names the embedded tag and this platform's asset; the fake
        // serves bytes that do not match, so it must refuse rather than install.
        Publish(OtherBytes);
        var ex = Assert.Throws<BackupReaderProvisioningException>(() => Ensure(embedded,
            fetch: (url, dest) => { _fetched.Add(url); File.WriteAllBytes(dest, OtherBytes); return BackupReaderProvisioning.FetchStatus.Ok; }));
        Assert.Equal(BackupReaderProvisioningFailure.ChecksumMismatch, ex.Failure);
        Assert.Equal(
            $"https://github.com/StefanMaron/BusinessCentral.DbReader/releases/download/{file.Tag}/{file.Assets[Platform].File}",
            Assert.Single(_fetched));
    }

    /// <summary>Every platform the reader release publishes a binary for (linux, osx: x64 and
    /// arm64; win: x64 — read off the v0.1.2 release) carries a full SHA-256 in the pin.</summary>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    public void EmbeddedPin_CarriesAChecksumForEveryPublishedPlatform(string platform)
    {
        var asset = BackupReaderPin.Embedded.Assets[platform];
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), asset.Sha256);
        Assert.StartsWith("bcdb-" + platform, asset.File, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisProcess_RunsOnAPlatformThePinCovers()
    {
        var platform = BackupReaderProvisioning.CurrentPlatform();
        Assert.NotNull(platform);
        Assert.Contains(platform!, BackupReaderPin.Embedded.Assets.Keys);
    }

    [Fact]
    public void ReaderNotFoundMessage_TellsHowToProvision_OnOneLine()
    {
        var message = BackupReaderTool.NotFoundMessage(BackupReaderTool.CandidateExecutables(null, _cacheRoot));

        Assert.DoesNotContain('\n', message);
        Assert.Contains(Managed, message, StringComparison.Ordinal);
        Assert.Contains("al-runner provision --test-data", message, StringComparison.Ordinal);
        Assert.Contains("--no-auto-provision", message, StringComparison.Ordinal);
    }

    private static string RepoRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
