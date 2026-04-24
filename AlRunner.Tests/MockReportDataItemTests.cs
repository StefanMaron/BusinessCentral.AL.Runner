using AlRunner.Runtime;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Unit tests for MockReportDataItem — the stand-in returned by
/// <c>ReportExtensionNNNNN.GetDataItem(name)</c> after the rewriter injects
/// the GetDataItem stub on report-extension classes. Covers issue #1212.
/// </summary>
public class MockReportDataItemTests
{
    [Fact]
    public void DefaultCtor_ExposesNonNullEmptyRecord()
    {
        // Positive: the default constructor (used by the injected GetDataItem
        // stub) returns an instance whose Record is a valid MockRecordHandle
        // — not null — so the generated call chain
        // `GetDataItem(name).Record.GetFieldValueSafe(...)` does not NRE.
        var item = new MockReportDataItem();

        Assert.NotNull(item.Record);
        Assert.Equal(0, item.Record.TableId);
    }

    [Fact]
    public void Record_Setter_AcceptsProvidedHandle()
    {
        // Positive: the Record property is settable so callers (or future
        // name-based lookups) can supply a specific table-backed handle.
        var handle = new MockRecordHandle(70700);

        var item = new MockReportDataItem { Record = handle };

        Assert.Same(handle, item.Record);
        Assert.Equal(70700, item.Record.TableId);
    }

    [Fact]
    public void HandleCtor_RespectsExplicitNullRecord()
    {
        // Negative: constructing with an explicitly null record leaves
        // Record null. This pins the contract so a future "helpful" default
        // does not silently mask downstream bugs — callers that rely on
        // Record being non-null will see a NullReferenceException rather
        // than reading from a phantom empty record.
        var item = new MockReportDataItem(null!);

        Assert.Null(item.Record);
    }
}
