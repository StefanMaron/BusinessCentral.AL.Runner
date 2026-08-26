// DapTransport — the Debug Adapter Protocol wire format: `Content-Length: N\r\n\r\n`
// followed by N bytes of UTF-8 JSON, repeated for every request/response/event (DAP
// spec, https://microsoft.github.io/debug-adapter-protocol/overview — the same
// framing every DAP client, including VS Code's built-in debug UI, speaks). Built
// fresh for issue #1642 rather than reusing ServerProtocol's newline-delimited JSON:
// that framing is a --server-specific convention (see ServerProtocol.cs's own doc
// comment), not the DAP spec, and a real DAP client will not speak it.
//
// Deliberately Stream-based, not Socket-based: a real --dap session wraps a
// NetworkStream, but AlRunner.Tests drives the exact same class over an in-memory
// duplex pipe (System.IO.Pipelines or a pair of AnonymousPipeServerStream/
// AnonymousPipeClientStream) with no socket involved — deterministic, no port
// contention between parallel test runs.
using System.Text;
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>One parsed DAP message: request, response, or event. Only the fields this
/// slice's command set actually uses are typed; anything else stays reachable via
/// <see cref="Raw"/>.</summary>
public sealed class DapIncomingMessage
{
    public required int Seq { get; init; }
    public required string Type { get; init; } // "request"
    public string? Command { get; init; }
    public JsonElement? Arguments { get; init; }
    public required JsonDocument Raw { get; init; }
}

public sealed class DapTransport : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private int _seq;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public DapTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Reads one framed message, or null on clean EOF (the peer closed the
    /// connection — a normal end of session, not an error).</summary>
    public async Task<DapIncomingMessage?> ReadMessageAsync(CancellationToken ct = default)
    {
        int contentLength = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(ct).ConfigureAwait(false);
            if (line == null) return null; // EOF before/between headers
            if (line.Length == 0) break; // blank line ends the header block
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out contentLength))
                    throw new InvalidOperationException($"[dap] malformed Content-Length header: '{value}'");
            }
        }
        if (contentLength < 0)
            throw new InvalidOperationException("[dap] message had no Content-Length header — not a DAP frame");

        var buf = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await _input.ReadAsync(buf.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
            if (n == 0) return null; // EOF mid-body — peer closed while sending
            read += n;
        }

        var doc = JsonDocument.Parse(buf);
        var root = doc.RootElement;
        return new DapIncomingMessage
        {
            Seq = root.TryGetProperty("seq", out var seqEl) ? seqEl.GetInt32() : 0,
            Type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "",
            Command = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null,
            Arguments = root.TryGetProperty("arguments", out var argsEl) ? argsEl : null,
            Raw = doc,
        };
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (true)
        {
            var n = await _input.ReadAsync(b.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (b[0] == (byte)'\r') continue;
            if (b[0] == (byte)'\n') return sb.ToString();
            sb.Append((char)b[0]);
        }
    }

    /// <summary>Writes a DAP response for <paramref name="requestSeq"/>. <paramref
    /// name="body"/> is omitted entirely (not emitted as null) when the command has
    /// nothing to report, matching every other optional-field convention in this
    /// codebase (ServerProtocol.cs).</summary>
    public void WriteResponse(int requestSeq, string command, bool success, object? body = null, string? message = null)
        => Write(new
        {
            seq = NextSeq(),
            type = "response",
            request_seq = requestSeq,
            success,
            command,
            body,
            message,
        });

    public void WriteEvent(string eventName, object? body = null)
        => Write(new { seq = NextSeq(), type = "event", @event = eventName, body });

    /// <summary>Writes a DAP request — the CLIENT side of this same class. Only used
    /// by AlRunner.Tests' DAP client harness (a real DAP client, e.g. VS Code, drives
    /// this side too, but al-runner itself only ever plays the adapter/server role).
    /// Returns the seq this request was sent with, so the caller can match it against
    /// the response's request_seq.</summary>
    public int WriteRequest(string command, object? arguments = null)
    {
        var seq = NextSeq();
        Write(new { seq, type = "request", command, arguments });
        return seq;
    }

    private int NextSeq() => System.Threading.Interlocked.Increment(ref _seq);

    private void Write(object payload)
    {
        var json = JsonSerializer.Serialize(payload, WriteOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        lock (_writeLock)
        {
            _output.Write(header, 0, header.Length);
            _output.Write(bytes, 0, bytes.Length);
            _output.Flush();
        }
    }

    public void Dispose()
    {
        try { _input.Dispose(); } catch { /* peer may already have closed */ }
        try { _output.Dispose(); } catch { /* peer may already have closed */ }
    }
}
