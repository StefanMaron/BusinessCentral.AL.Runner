// RecordHotPathCachingTests — issue #4487.
//
// Codeunit136106.ResponseTimeValidOnHolidays calls "Library - Service".IsWorking millions of times,
// and every call builds record and codeunit variables. Profiling that loop found work repeated on
// each materialisation although its answer can never change: a ConditionalWeakTable re-written for a
// key it already held, Assembly.GetName() per staleness check, and an environment read per
// NavRecordHandle constructor. These pin that each is done once.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class RecordHotPathCachingTests
{
    private sealed class FakeProvider { }

    private sealed class FakeDataAccess
    {
        public FakeDataAccess(object? provider) => DataProvider = provider;
        public object? DataProvider { get; }
    }

    [Fact]
    public void MarkDatabaseBacked_RegistersAProviderOnce_AndLeavesItRegistered()
    {
        var provider = new FakeProvider();
        var dataAccess = new FakeDataAccess(provider);

        Assert.False(BlobStoreIsolationPatches.IsDatabaseBacked(provider));
        Assert.True(BlobStoreIsolationPatches.MarkDatabaseBacked(dataAccess));
        Assert.True(BlobStoreIsolationPatches.IsDatabaseBacked(provider));

        // A second DataAccess hand-out for the same table is the per-record-variable case.
        Assert.False(BlobStoreIsolationPatches.MarkDatabaseBacked(dataAccess));
        Assert.False(BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(provider)));
        Assert.True(BlobStoreIsolationPatches.IsDatabaseBacked(provider));
    }

    [Fact]
    public void MarkDatabaseBacked_ADistinctProviderIsRegistered_AndNoProviderIsNot()
    {
        var first = new FakeProvider();
        var second = new FakeProvider();
        Assert.True(BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(first)));
        Assert.True(BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(second)));
        Assert.True(BlobStoreIsolationPatches.IsDatabaseBacked(second));

        Assert.False(BlobStoreIsolationPatches.MarkDatabaseBacked(new FakeDataAccess(null)));
        Assert.False(BlobStoreIsolationPatches.MarkDatabaseBacked(null));
        Assert.False(BlobStoreIsolationPatches.IsDatabaseBacked(new FakeProvider()));
    }

    [Fact]
    public void SimpleName_IsReadOncePerAssembly_AndMatchesGetName()
    {
        var asm = typeof(RecordHotPathCachingTests).Assembly;
        var first = BcRuntime.SimpleName(asm);
        var second = BcRuntime.SimpleName(asm);

        Assert.Equal(asm.GetName().Name, first);
        // GetName() builds a new AssemblyName, and with it a new Name string, on every call.
        Assert.NotSame(asm.GetName().Name, asm.GetName().Name);
        Assert.Same(first, second);
    }

    [Fact]
    public void Diag_ReadsItsSwitchOnce_NotOnEveryCall()
    {
        Assert.False(AlRunner.NavReportSync.DiagIcEnabled);
        var marker = "hot-path-4487-" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_IC");
        var originalError = Console.Error;
        var captured = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_DIAG_IC", "1");
            Console.SetError(captured);
            AlRunner.NavReportSync.Diag(marker);
        }
        finally
        {
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("AL_RUNNER_DIAG_IC", previous);
        }
        Assert.DoesNotContain(marker, captured.ToString());
    }
}
