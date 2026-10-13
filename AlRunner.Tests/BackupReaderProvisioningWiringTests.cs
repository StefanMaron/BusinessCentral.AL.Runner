// BackupReaderProvisioningWiringTests — #4925: the reader install is wired into the entry point
// the run and `provision --test-data` both call (ProgramSupport.ProvisionTestDataBackup), driven
// with a fake release. An explicit backup path keeps the 1 GB backup step out of it; the reader is
// still needed for one. Its own class because it writes the --test-data statics (#4220).
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(TestDataStaticsSerialCollection.Name)]
public sealed class BackupReaderProvisioningWiringTests : IDisposable
{
    private readonly BackupReaderProvisioningTests _fixture = new();

    public BackupReaderProvisioningWiringTests()
    {
        TestDataOptions.ResetForTests();
        TestDataOptions.TryParseArg("--test-data=/nowhere/BusinessCentral-W1.bak");
    }

    public void Dispose()
    {
        TestDataOptions.ResetForTests();
        _fixture.Dispose();
    }

    [Fact]
    public void ProvisionTestDataBackup_InstallsTheReader_WithAnExplicitBackupPathToo()
    {
        _fixture.Publish(BackupReaderProvisioningTests.PinnedBytes);
        var err = new StringWriter();

        var rc = ProgramSupport.ProvisionTestDataBackup("28.5.0.0", autoProvision: true, verbose: false,
            _fixture.Inputs(BackupReaderProvisioningTests.PinFor(BackupReaderProvisioningTests.PinnedBytes)), err);

        Assert.Equal(0, rc);
        Assert.Equal(BackupReaderProvisioningTests.PinnedBytes, File.ReadAllBytes(_fixture.Managed));
        Assert.Contains("installed backup reader", err.ToString(), StringComparison.Ordinal);
    }

    // #5543: the entry point the run calls, with --no-auto-provision.
    [Fact]
    public void ProvisionTestDataBackup_NoAutoProvision_NotesADifferingSlotReader_AndStillSucceeds()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Managed)!);
        File.WriteAllBytes(_fixture.Managed, BackupReaderProvisioningTests.OtherBytes);
        var err = new StringWriter();

        var rc = ProgramSupport.ProvisionTestDataBackup("28.5.0.0", autoProvision: false, verbose: false,
            _fixture.Inputs(BackupReaderProvisioningTests.PinFor(BackupReaderProvisioningTests.PinnedBytes)), err);

        Assert.Equal(0, rc);
        var line = Assert.Single(err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Contains(_fixture.Managed, line, StringComparison.Ordinal);
        Assert.Contains("is not the pinned", line, StringComparison.Ordinal);
        Assert.Equal(BackupReaderProvisioningTests.OtherBytes, File.ReadAllBytes(_fixture.Managed));
    }

    [Fact]
    public void ProvisionTestDataBackup_NoAutoProvision_SilentWhenTheSlotReaderIsThePinned()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Managed)!);
        File.WriteAllBytes(_fixture.Managed, BackupReaderProvisioningTests.PinnedBytes);
        var err = new StringWriter();

        var rc = ProgramSupport.ProvisionTestDataBackup("28.5.0.0", autoProvision: false, verbose: false,
            _fixture.Inputs(BackupReaderProvisioningTests.PinFor(BackupReaderProvisioningTests.PinnedBytes)), err);

        Assert.Equal(0, rc);
        Assert.Equal(string.Empty, err.ToString());
    }

    /// <summary>The --jobs parent printed the note before the fan-out; each worker re-runs this
    /// entry point with the same slot and must not print it again.</summary>
    [Fact]
    public void ProvisionTestDataBackup_NoAutoProvision_AJobsWorkerDoesNotRepeatTheNote()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Managed)!);
        File.WriteAllBytes(_fixture.Managed, BackupReaderProvisioningTests.OtherBytes);
        var err = new StringWriter();

        var rc = ProgramSupport.ProvisionTestDataBackup("28.5.0.0", autoProvision: false, verbose: false,
            _fixture.Inputs(BackupReaderProvisioningTests.PinFor(BackupReaderProvisioningTests.PinnedBytes)), err,
            jobsWorker: true);

        Assert.Equal(0, rc);
        Assert.Equal(string.Empty, err.ToString());
    }

    [Fact]
    public void ChecksumMismatch_StopsTheRun_WithExitTwo_AndTheReason()
    {
        _fixture.Publish(BackupReaderProvisioningTests.OtherBytes);
        var err = new StringWriter();

        var rc = ProgramSupport.ProvisionTestDataBackup("28.5.0.0", autoProvision: true, verbose: false,
            _fixture.Inputs(BackupReaderProvisioningTests.PinFor(BackupReaderProvisioningTests.PinnedBytes)), err);

        Assert.Equal(2, rc);
        Assert.Contains("Refusing to install", err.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(_fixture.Managed));
    }
}
