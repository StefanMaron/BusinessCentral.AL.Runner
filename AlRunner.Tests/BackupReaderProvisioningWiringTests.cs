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
