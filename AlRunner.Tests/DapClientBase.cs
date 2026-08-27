using System.Text.Json;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

/// <summary>
/// Shared response/event demultiplexing core for the DAP end-to-end test clients —
/// <see cref="DapClient"/> (TCP) and <see cref="DapStdioClient"/> (the child
/// process's own stdin/stdout). Both drive the identical DAP session over
/// AlRunner.Infrastructure.DapTransport; the only difference between them is which
/// pair of Streams the transport is constructed over, and which transport-specific
/// diagnostics are worth attaching to a timeout (DapClient's TCP-socket-available/
/// ThreadPool probe has no stdio equivalent). Everything about turning "a stream of
/// DAP messages" into "the response to seq N" / "the next 'stopped' event" belongs
/// HERE, once — not duplicated per transport.
///
/// That duplication is exactly how issue #2070 recurred: DAP responses and events
/// are two INDEPENDENT streams that interleave by protocol design. Handling
/// "next"/"stepIn"/"stepOut" releases the paused AL thread, which is then free to
/// run, qualify, and write its own "stopped" event — a race against the DAP loop
/// thread writing the command's OWN response. If the AL thread wins, the wire order
/// is "stopped" event FIRST, command response SECOND. A ReadUntilResponseAsync that
/// discards events seen while waiting for a response reads that "stopped" event,
/// throws it away, reads the response, returns — and the caller's very next
/// ReadUntilEventAsync("stopped") then waits the full timeout for a second "stopped"
/// that will never be sent. #2070 fixed this for DapClient (the TCP client); the
/// original PR for #2058 (this stdio client) shipped its OWN, unfixed copy of the
/// same read loop, reproducing the identical hang one transport over. A shared base
/// class is the fix for the CLASS of bug, not just the one instance of it: anything
/// that isn't the message currently being waited for is queued, not discarded, and
/// later reads drain the queue before touching the underlying stream again.
///
/// CAUGHT WHILE BUILDING THE ORIGINAL FIX: a first version had both methods dequeue
/// from <see cref="_pendingEvents"/> and unconditionally re-enqueue a non-matching
/// item back onto the SAME queue, in the SAME loop iteration, with no stream I/O in
/// between. With exactly one item queued (the common case) that dequeue-then-requeue
/// is a net no-op that repeats at CPU-bound spin speed — no real wait, no forward
/// progress. Fixed by splitting each method into two phases: phase 1 drains whatever
/// was ALREADY queued, bounded by a snapshot of the queue's length taken before the
/// scan starts (so a re-queued miss is never reconsidered within the same phase-1
/// pass); only once that snapshot is exhausted does phase 2 fall through to blocking
/// reads via <see cref="ReadOneAsync"/>, the only phase allowed to burn real
/// wall-clock time.
/// </summary>
public abstract class DapClientBase : IAsyncDisposable
{
    protected readonly DapTransport Transport;

    protected DapClientBase(DapTransport transport)
    {
        Transport = transport;
    }

    private readonly Queue<JsonElement> _pendingEvents = new();

    /// <summary>Sends a DAP request and returns its seq (for matching against the
    /// eventual response's request_seq via <see cref="ReadUntilResponseAsync"/>).</summary>
    public int SendRequest(string command, object? arguments = null)
    {
        var seq = Transport.WriteRequest(command, arguments);
        Trace($"SEND {command} seq={seq}");
        return seq;
    }

    /// <summary>Reads messages until the response to <paramref name="requestSeq"/>
    /// arrives, returning it. Any events seen along the way — whether already sitting
    /// in <see cref="_pendingEvents"/> from an earlier read or freshly read off the
    /// stream — are appended to <paramref name="events"/> if given, and (unconditionally)
    /// left in <see cref="_pendingEvents"/> so a later <see cref="ReadUntilEventAsync"/>
    /// still sees them even when no `events` list is given here. A response is never
    /// queued (by construction, only "event"-typed messages are), so phase 1 can only
    /// ever collect for `events`, never satisfy the wait itself — it still has to run
    /// so already-queued events are not skipped when a caller wants to see them.</summary>
    public async Task<JsonElement> ReadUntilResponseAsync(int requestSeq, List<JsonElement>? events = null, TimeSpan? timeout = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(30);

        var alreadyQueued = _pendingEvents.Count;
        for (var i = 0; i < alreadyQueued; i++)
        {
            var queuedRoot = _pendingEvents.Dequeue();
            events?.Add(queuedRoot);
            _pendingEvents.Enqueue(queuedRoot);
        }

        var deadline = DateTime.UtcNow + t;
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(t);
            var root = msg.Raw.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "response" && root.TryGetProperty("request_seq", out var rs) && rs.GetInt32() == requestSeq)
                return root;
            if (type == "event")
            {
                events?.Add(root);
                var evName = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : "?";
                Trace($"QUEUE event={evName} arrived while waiting for response to seq={requestSeq} — not dropped");
                _pendingEvents.Enqueue(root);
            }
        }
        throw new TimeoutException($"no response to request seq {requestSeq} within timeout.\n{DiagnosticDump()}");
    }

    /// <summary>Reads messages until an event named <paramref name="eventName"/>
    /// arrives, returning its body. Used to wait for e.g. "stopped". Every event seen
    /// along the way (including the terminal one) is appended to <paramref
    /// name="allEvents"/> if given, so a caller can assert on what did NOT arrive (e.g.
    /// "no 'stopped' event fired") without a second read loop. Phase 1 scans whatever
    /// is ALREADY in <see cref="_pendingEvents"/> — bounded to a snapshot of its length
    /// so a non-matching item is examined exactly once per call, never spun on — before
    /// phase 2 falls through to blocking reads.</summary>
    public async Task<JsonElement> ReadUntilEventAsync(string eventName, TimeSpan? timeout = null, List<JsonElement>? allEvents = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(60);

        var alreadyQueued = _pendingEvents.Count;
        for (var i = 0; i < alreadyQueued; i++)
        {
            var queuedRoot = _pendingEvents.Dequeue();
            allEvents?.Add(queuedRoot);
            var queuedEventName = queuedRoot.TryGetProperty("event", out var qEvEl) ? qEvEl.GetString() : null;
            if (queuedEventName == eventName) return queuedRoot;
            _pendingEvents.Enqueue(queuedRoot);
        }

        var deadline = DateTime.UtcNow + t;
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(t);
            var root = msg.Raw.RootElement;
            if (root.GetProperty("type").GetString() != "event") continue;
            allEvents?.Add(root);
            var thisEventName = root.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;
            if (thisEventName == eventName)
                return root;
            // A different event than the one being awaited right now — re-queue it
            // rather than drop it, same principle as ReadUntilResponseAsync above (a
            // second step command in a row, each waiting on its own "stopped", is the
            // same shape of race one level up).
            Trace($"QUEUE event={thisEventName ?? "?"} arrived while waiting for event={eventName} — not dropped");
            _pendingEvents.Enqueue(root);
        }
        throw new TimeoutException($"event '{eventName}' did not arrive within {t.TotalSeconds:F0}s.\n{DiagnosticDump()}");
    }

    /// <summary>Reads the next DAP message off the transport, subject to
    /// <paramref name="timeout"/>. Implemented per-subclass because what's worth
    /// capturing at a genuine giveup differs by transport (DapClient's TCP
    /// socket-available/ThreadPool-health probe has no stdio equivalent) — but both
    /// subclasses funnel every read through this one seam, which is what lets the
    /// demux logic above live here exactly once.</summary>
    protected abstract Task<DapIncomingMessage> ReadOneAsync(TimeSpan timeout);

    /// <summary>Optional per-line wall-clock tracing (see DapClient's AL_DAP_STEP_TRACE
    /// support). A no-op by default so a subclass isn't forced to implement it.</summary>
    protected virtual void Trace(string msg) { }

    /// <summary>Extra context appended to every TimeoutException this class throws.
    /// DapClient dumps stdout/stderr/its own trace buffer; DapStdioClient dumps
    /// stderr only — stdout IS the DAP channel there, not diagnostic text.</summary>
    protected abstract string DiagnosticDump();

    public abstract ValueTask DisposeAsync();
}
