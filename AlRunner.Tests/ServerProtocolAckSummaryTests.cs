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
}
