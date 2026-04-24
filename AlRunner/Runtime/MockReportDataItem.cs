// Copyright © Stefan Maron. Licensed under the MIT License.
// MockReportDataItem — the object returned by ReportExtensionNNNNN.GetDataItem(name).
//
// BC emits calls like:
//   this.CurrReport.GetDataItem("Cust").Record.GetFieldValueSafe(2, NavType.Text)
//   this.CurrReport.GetDataItem("Cust").Record.SetTableView(...)
// inside ReportExtension classes when AL code references dataitems declared
// on the BASE report. After the rewriter strips the NavReportExtension base
// class, the reportextension needs a `GetDataItem` method that returns an
// object with a `.Record` property (backed by MockRecordHandle).
//
// Because the runner does not actually execute report dataset iteration, the
// returned record is an empty MockRecordHandle — calls compile and return
// default field values at runtime. Covers issue #1212.

namespace AlRunner.Runtime;

/// <summary>
/// Minimal stand-in for a report data-item handle exposed via
/// <c>ReportExtensionNNNNN.GetDataItem(name)</c>.
/// </summary>
public sealed class MockReportDataItem
{
    /// <summary>Backing record handle. Empty by default — no rows.</summary>
    public MockRecordHandle Record { get; set; }

    public MockReportDataItem() : this(new MockRecordHandle(0)) { }

    public MockReportDataItem(MockRecordHandle record)
    {
        Record = record;
    }
}
