using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1641 (`cancel` command slice): the wire-shape unit tests for
/// <see cref="ServerProtocol.Ack"/> and the <c>cancelled</c> field on
/// <see cref="ServerProtocol.Summary"/>. These drive the serializers directly (no
/// BC runtime, no subprocess), so they run everywhere and pin the exact JSON the
/// end-to-end <c>ServerCancelTests</c> then proves is actually reachable through a
/// live <c>--server</c> cancel round trip.
///
/// Wire shapes match v1 verbatim (PRs #1613/#1614): <c>{"type":"ack",
/// "command":"cancel","noop":bool}</c> and <c>cancelled</c> present-and-true (never
/// present-and-false) on the summary.
/// </summary>
public class ServerProtocolAckSummaryTests
{
    private static readonly TestResult PassResult =
        new("Codeunit60320", "Test01", TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(5));

    // ── Ack ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Ack_NoopTrue_SerializesV1Shape()
    {
        var json = ServerProtocol.Ack("cancel", noop: true);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.Equal("ack", e.GetProperty("type").GetString());
        Assert.Equal("cancel", e.GetProperty("command").GetString());
        Assert.True(e.GetProperty("noop").GetBoolean());
    }

    [Fact]
    public void Ack_NoopFalse_SerializesV1Shape()
    {
        var json = ServerProtocol.Ack("cancel", noop: false);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.Equal("ack", e.GetProperty("type").GetString());
        Assert.Equal("cancel", e.GetProperty("command").GetString());
        Assert.False(e.GetProperty("noop").GetBoolean());
    }

    // ── Summary.cancelled ────────────────────────────────────────────────────

    [Fact]
    public void Summary_CancelledFalseArgument_OmitsFieldEntirely()
    {
        // The default (cancelled: false, matching every existing non-cancel call
        // site that doesn't pass the parameter) must NOT put `cancelled:false` on
        // the wire — every other optional field on this line follows "absent means
        // not applicable", and a literal false here would read as "we checked and
        // it wasn't cancelled" instead of "cancellation was never asked about".
        var json = ServerProtocol.Summary(new[] { PassResult }, exitCode: 0, cached: false);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.False(e.TryGetProperty("cancelled", out _));
    }

    [Fact]
    public void Summary_CancelledTrueArgument_EmitsLiteralTrue()
    {
        var json = ServerProtocol.Summary(new[] { PassResult }, exitCode: 0, cached: false, cancelled: true);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.True(e.TryGetProperty("cancelled", out var cancelledProp));
        Assert.True(cancelledProp.GetBoolean());
    }

    [Fact]
    public void Summary_CancelledTrue_OtherFieldsUnaffected()
    {
        // cancelled:true must not disturb the rest of the summary contract —
        // passed/failed/total/protocolVersion read exactly as they would for an
        // uncancelled run over the same result list.
        var results = new[] { PassResult, PassResult };
        var json = ServerProtocol.Summary(results, exitCode: 0, cached: false, cancelled: true);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.Equal(2, e.GetProperty("passed").GetInt32());
        Assert.Equal(0, e.GetProperty("failed").GetInt32());
        Assert.Equal(2, e.GetProperty("total").GetInt32());
        Assert.Equal(2, e.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("summary", e.GetProperty("type").GetString());
    }

    [Fact]
    public void Summary_Selection_EmitsAffectedSelectionShape()
    {
        var selection = new ServerSelection(
            "affected", Ran: 1, Skipped: 4,
            ChangedObjects: new[] { "Codeunit 60205 Test Query Object" },
            ForcedFull: false, Reason: null);
        var json = ServerProtocol.Summary(new[] { PassResult }, exitCode: 0, cached: false, selection: selection);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.True(e.TryGetProperty("selection", out var sel), json);
        Assert.Equal("affected", sel.GetProperty("mode").GetString());
        Assert.Equal(1, sel.GetProperty("ran").GetInt32());
        Assert.Equal(4, sel.GetProperty("skipped").GetInt32());
        Assert.False(sel.GetProperty("forcedFull").GetBoolean());
        Assert.False(sel.TryGetProperty("reason", out _));
    }

    [Fact]
    public void Execute_Selection_EmitsForcedFullReason()
    {
        var selection = new ServerSelection(
            "affected", Ran: 2, Skipped: 0,
            ChangedObjects: Array.Empty<string>(),
            ForcedFull: true, Reason: "change model unavailable");
        var json = ServerProtocol.Execute(new[] { PassResult, PassResult }, exitCode: 0, selection: selection);
        var e = JsonDocument.Parse(json).RootElement;

        Assert.True(e.TryGetProperty("selection", out var sel), json);
        Assert.True(sel.GetProperty("forcedFull").GetBoolean());
        Assert.Equal("change model unavailable", sel.GetProperty("reason").GetString());
    }

    // ── sourceScanFailures (#3847/#3884) ────────────────────────────────────────────────

    /// <summary>
    /// #3884 Copilot review: the wire field had no direct test, and this class is where the
    /// Summary/Execute field contracts are pinned. A regression that renamed the field, or
    /// dropped `kind`, would have passed everything else in the PR.
    /// </summary>
    [Fact]
    public void Summary_WithScanFailures_CarriesPathReasonAndKind()
    {
        var failures = new[]
        {
            new AlRunner.Infrastructure.SourceScanFailure(
                "/src/gone", "the source root does not exist",
                AlRunner.Infrastructure.SourceScanFailureKind.Root),
            new AlRunner.Infrastructure.SourceScanFailure(
                "/src/app/Locked.al", "the file could not be read (IOException: locked)",
                AlRunner.Infrastructure.SourceScanFailureKind.File),
        };

        var json = ServerProtocol.Summary(
            new[] { PassResult }, exitCode: 2, cached: false, sourceScanFailures: failures);

        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("sourceScanFailures");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("/src/gone", arr[0].GetProperty("path").GetString());
        Assert.Equal("the source root does not exist", arr[0].GetProperty("reason").GetString());
        Assert.Equal("Root", arr[0].GetProperty("kind").GetString());
        // The kind a client must NOT treat as a prefix.
        Assert.Equal("File", arr[1].GetProperty("kind").GetString());
    }

    /// <summary>
    /// Absent, never an empty array — the convention `coverage` and `companyInitFailures`
    /// already use, so a client that does not know the field sees no change at all. An empty
    /// array would read as "the scan reported something", which is the opposite of the truth.
    /// </summary>
    [Theory]
    [InlineData(false)]  // null
    [InlineData(true)]   // empty
    public void Summary_WithNoScanFailures_OmitsTheFieldEntirely(bool empty)
    {
        var json = ServerProtocol.Summary(
            new[] { PassResult }, exitCode: 0, cached: false,
            sourceScanFailures: empty
                ? Array.Empty<AlRunner.Infrastructure.SourceScanFailure>()
                : null);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("sourceScanFailures", out _));
    }

    /// <summary>The execute response carries the same field, with the same omission rule —
    /// it is a separate serializer and the two have drifted before.</summary>
    [Fact]
    public void Execute_CarriesScanFailures_AndOmitsThemWhenThereAreNone()
    {
        var failures = new[]
        {
            new AlRunner.Infrastructure.SourceScanFailure(
                "/src/sub", "the directory could not be read",
                AlRunner.Infrastructure.SourceScanFailureKind.Directory),
        };

        using (var doc = JsonDocument.Parse(
            ServerProtocol.Execute(new[] { PassResult }, exitCode: 2, sourceScanFailures: failures)))
        {
            var arr = doc.RootElement.GetProperty("sourceScanFailures");
            Assert.Equal(1, arr.GetArrayLength());
            Assert.Equal("Directory", arr[0].GetProperty("kind").GetString());
        }

        using (var doc = JsonDocument.Parse(
            ServerProtocol.Execute(new[] { PassResult }, exitCode: 0)))
            Assert.False(doc.RootElement.TryGetProperty("sourceScanFailures", out _));
    }
}
