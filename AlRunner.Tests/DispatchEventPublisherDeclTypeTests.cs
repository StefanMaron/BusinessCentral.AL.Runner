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
/// NOTE ON COVERAGE: the full dispatch path (registry scan, sentinel seeding, actual subscriber
/// invocation) needs BC's own compiled AL objects and cannot be exercised from a plain unit
/// test — that end-to-end proof is the corpus test added in
/// StefanMaron/BusinessCentral.AL.Language.Tests#33 (Codeunit 60950 "Test Manual TableEvent").
/// This test pins the specific decode defect at the seam, the same pattern
/// DispatchObserveAsyncResultTests.cs uses for the async-result seam in the same file.
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

    [Theory]
    [InlineData("Table60976")]   // the metadata-only class — NOT where table trigger code lives
    [InlineData("Page100")]
    [InlineData("Report50")]
    [InlineData("Query10")]
    [InlineData("XmlPort1")]
    [InlineData("Enum42")]
    [InlineData("")]
    [InlineData("CodeunitNotANumber")]
    [InlineData("RecordAlsoNotANumber")]
    public void UnrecognizedOrMalformedDeclType_ReturnsFalse(string declTypeName)
    {
        var ok = BcRuntime.TryDecodeEventPublisherDeclType(declTypeName, out var kind, out var id);

        Assert.False(ok);
        Assert.Equal("", kind);
        Assert.Equal(0, id);
    }
}
