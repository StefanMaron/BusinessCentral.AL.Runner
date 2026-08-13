using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="BcRuntime.TryDecodeEventPublisherDeclType"/> — the seam that decides which
/// AL object kinds' manually-declared events (IntegrationEvent/BusinessEvent) dispatch at all.
///
/// Issue #1770: CodeunitEventDispatcher.DispatchCore used to recognize ONLY the "Codeunit"
/// declaring-type prefix. A table object's OWN code (triggers, procedures, and any
/// manually-declared event) compiles to a CLR class named "Record&lt;N&gt;", not "Table&lt;N&gt;"
/// (empirically confirmed by reflecting over the emitted test assembly) — so a table-published
/// event's publisher scope was never recognized, its γeventScope sentinel was never seeded, and
/// the AL-compiled publisher's own early-exit guard fired before the event ever reached the
/// dispatcher. No exception, no log — the subscriber simply never ran.
///
/// Issue #1794 (sibling gap #1770 deliberately left open): Page/Report/Query/XmlPort own code
/// compiles to a class literally named "&lt;Kind&gt;&lt;N&gt;" — unlike Table there is no
/// metadata-vs-own-code split — but the decoder recognized only "Codeunit" and "Record", so a
/// manually-declared event published from one of these four object kinds was silently dropped
/// the same way. Empirically confirmed via reflection over an emitted test assembly (see
/// CodeunitEventDispatcher.cs's PublisherKindPage/Report/Query/XmlPort doc comment) before this
/// fix, and now covered below by both directions: the four kinds decode successfully, and the
/// previously-unreachable "Table&lt;N&gt;" metadata-only-class case still correctly returns false.
///
/// NOTE ON COVERAGE: the full dispatch path (registry scan, sentinel seeding, actual subscriber
/// invocation) needs BC's own compiled AL objects and cannot be exercised from a plain unit
/// test — that end-to-end proof is the corpus test added in
/// StefanMaron/BusinessCentral.AL.Language.Tests#33 (Codeunit 60950 "Test Manual TableEvent") for
/// the table/codeunit case, and StefanMaron/BusinessCentral.AL.Language.Tests (Codeunit 60986
/// "Test Manual ObjectEvent") for the page/report/query/xmlport case fixed here. This test pins
/// the specific decode defect at the seam, the same pattern DispatchObserveAsyncResultTests.cs
/// uses for the async-result seam in the same file.
/// </summary>
public class DispatchEventPublisherDeclTypeTests
{
    [Fact]
    public void CodeunitDeclType_DecodesKindAndId()
    {
        var ok = BcRuntime.TryDecodeEventPublisherDeclType("Codeunit50041", out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(BcRuntime.PublisherKindCodeunit, kind);
        Assert.Equal(50041, id);
    }

    [Fact]
    public void RecordDeclType_DecodesKindAndId()
    {
        // "Record<N>" is what a TABLE object's own code (triggers, procedures, manually-declared
        // events) compiles to — this is the exact case that was silently dropped before the fix.
        var ok = BcRuntime.TryDecodeEventPublisherDeclType("Record60976", out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(BcRuntime.PublisherKindTable, kind);
        Assert.Equal(60976, id);
    }

    [Fact]
    public void PageDeclType_DecodesKindAndId()
    {
        // "Page<N>" is what a PAGE object's own code (triggers, procedures, manually-declared
        // events) compiles to — issue #1794's silently-dropped case.
        var ok = BcRuntime.TryDecodeEventPublisherDeclType("Page60977", out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(BcRuntime.PublisherKindPage, kind);
        Assert.Equal(60977, id);
    }

    [Fact]
    public void ReportDeclType_DecodesKindAndId()
    {
        var ok = BcRuntime.TryDecodeEventPublisherDeclType("Report60978", out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(BcRuntime.PublisherKindReport, kind);
        Assert.Equal(60978, id);
    }

    [Fact]
    public void QueryDeclType_DecodesKindAndId()
    {
        var ok = BcRuntime.TryDecodeEventPublisherDeclType("Query60981", out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(BcRuntime.PublisherKindQuery, kind);
        Assert.Equal(60981, id);
    }

    [Fact]
    public void XmlPortDeclType_DecodesKindAndId()
    {
        var ok = BcRuntime.TryDecodeEventPublisherDeclType("XmlPort60982", out var kind, out var id);

        Assert.True(ok);
        Assert.Equal(BcRuntime.PublisherKindXmlPort, kind);
        Assert.Equal(60982, id);
    }

    [Theory]
    [InlineData("Table60976")]   // the metadata-only class — NOT where table trigger code lives
    [InlineData("Enum42")]
    [InlineData("")]
    [InlineData("CodeunitNotANumber")]
    [InlineData("RecordAlsoNotANumber")]
    [InlineData("PageAlsoNotANumber")]
    [InlineData("ReportAlsoNotANumber")]
    [InlineData("QueryAlsoNotANumber")]
    [InlineData("XmlPortAlsoNotANumber")]
    public void UnrecognizedOrMalformedDeclType_ReturnsFalse(string declTypeName)
    {
        var ok = BcRuntime.TryDecodeEventPublisherDeclType(declTypeName, out var kind, out var id);

        Assert.False(ok);
        Assert.Equal("", kind);
        Assert.Equal(0, id);
    }
}
