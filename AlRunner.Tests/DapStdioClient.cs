using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

/// <summary>
/// Spawns al-runner in <c>--dap stdio</c> mode (issue #2058) and drives it over the
/// real DAP wire format on the child process's own stdin/stdout — the same
/// <see cref="DapTransport"/> class <see cref="DapClient"/> uses for the TCP path,
/// just handed the process's piped stdio streams instead of a socket.
///
/// The response/event demultiplexing (queueing, phased drain, the #2070 race this
/// exists to prevent) lives in the shared <see cref="DapClientBase"/> — see its
/// header comment. This class supplies only what's specific to stdio: spawning +
/// readiness detection over stderr (stdout can't be used for that — see below), and
/// <see cref="RawStdoutBytes"/>: every byte the child ever wrote to its OWN stdout,
/// captured independently of DapTransport's own (lenient) header parser — see
/// DapTransport.ReadMessageAsync, which silently skips any header line without a
/// colon rather than failing on it. A test asserting only "the handshake succeeded"
/// would still pass with a stray banner line mixed into the stream; this harness
/// lets a test re-parse the exact bytes with a strict, no-tolerance framer instead
/// (see DapRawFrameValidator).
/// </summary>
public sealed class DapStdioClient : DapClientBase
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly Process _process;
    private readonly TeeReadStream _rawStdout;
    private readonly StringBuilder _stderr = new();
    private int _messagesReceived;

    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

    /// <summary>Every byte read so far from the child's real OS stdout handle, in
    /// the exact order the child wrote it — see the class header.</summary>
    public byte[] RawStdoutBytes => _rawStdout.CapturedBytes;

    /// <summary>Count of DAP messages (responses + events) successfully decoded off
    /// the stdout stream so far, for cross-checking against an independent strict
    /// re-parse of <see cref="RawStdoutBytes"/> — see DapRawFrameValidator.</summary>
    public int MessagesReceived => _messagesReceived;

    protected override string DiagnosticDump() => $"--- stderr ---\n{StdErr}";

    private DapStdioClient(Process process, DapTransport transport, TeeReadStream rawStdout)
        : base(transport)
    {
        _process = process;
        _rawStdout = rawStdout;
    }

    /// <summary>Starts al-runner in stdio DAP mode and waits for its readiness line
    /// on STDERR ("[dap] stdio transport ready ..." — see Program.cs's RunDapLoop).
    /// Unlike DapClient.StartAsync, this never reads stdout for readiness: in stdio
    /// mode stdout IS the DAP channel, so reading it for anything other than framed
    /// DAP messages would itself be the bug this class exists to catch.</summary>
    public static async Task<DapStdioClient> StartAsync(string bundleDir, TimeSpan? readyTimeout = null)
    {
        var argList = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg +
            $" --dap stdio \"{bundleDir}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argList,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var proc = Process.Start(psi)!;
        var stderr = new StringBuilder();
        var readyTcs = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                lock (stderr) stderr.AppendLine(line);
                if (line.Contains("[dap] stdio transport ready")) readyTcs.TrySetResult();
            }
        });

        var timeout = readyTimeout ?? TimeSpan.FromSeconds(120);
        var completed = await Task.WhenAny(readyTcs.Task, Task.Delay(timeout));
        if (completed != readyTcs.Task)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException(
                $"al-runner --dap stdio did not report ready within {timeout.TotalSeconds:F0}s.\n" +
                $"--- stderr ---\n{stderr}");
        }

        var rawStdout = new TeeReadStream(proc.StandardOutput.BaseStream);
        var transport = new DapTransport(rawStdout, proc.StandardInput.BaseStream);

        var client = new DapStdioClient(proc, transport, rawStdout);
        lock (stderr) client._stderr.Append(stderr);
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                lock (client._stderr) client._stderr.AppendLine(line);
        });

        return client;
    }

    protected override async Task<DapIncomingMessage> ReadOneAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        DapIncomingMessage? msg;
        try
        {
            msg = await Transport.ReadMessageAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"--dap stdio read timed out after {timeout.TotalSeconds:F0}s.\n{DiagnosticDump()}");
        }
        if (msg == null)
            throw new Exception($"--dap stdio connection closed unexpectedly.\n{DiagnosticDump()}");
        Interlocked.Increment(ref _messagesReceived);
        return msg;
    }

    /// <summary>Closes the child's stdin (its DAP input) so RunDapLoop's read loop
    /// observes clean EOF and returns on its own — the graceful-shutdown path,
    /// deliberately NOT a Kill(): a killed process can be torn down mid-write,
    /// which would make a "no extra bytes after the last frame" assertion
    /// meaningless (the kill, not the implementation, decided where the stream
    /// stopped). Then drains every remaining byte the child writes until ITS stdout
    /// reaches true EOF, so <see cref="RawStdoutBytes"/> reflects the process's
    /// entire lifetime, not just what this test happened to read before deciding it
    /// was done.</summary>
    public async Task ShutdownAndDrainAsync(TimeSpan? timeout = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(30);
        try { _process.StandardInput.Close(); } catch { }
        var deadline = DateTime.UtcNow + t;
        var buf = new byte[4096];
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(t);
            int n;
            try { n = await _rawStdout.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token); }
            catch (OperationCanceledException) { break; }
            if (n == 0) break; // clean EOF: the child closed stdout on exit
        }
        try { await _process.WaitForExitAsync(new CancellationTokenSource(t).Token); } catch { }
    }

    public override async ValueTask DisposeAsync()
    {
        try { Transport.Dispose(); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        _process.Dispose();
    }
}

/// <summary>Wraps a Stream's read side, appending every byte actually read to an
/// internal buffer — used to capture al-runner's raw child-process stdout
/// independently of whatever parses it, so a strict re-parse can be run against the
/// exact bytes later. Read-only; write/seek are not needed and not supported.</summary>
public sealed class TeeReadStream : Stream
{
    private readonly Stream _inner;
    private readonly object _lock = new();
    private readonly List<byte> _captured = new();

    public TeeReadStream(Stream inner) { _inner = inner; }

    public byte[] CapturedBytes { get { lock (_lock) return _captured.ToArray(); } }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0) lock (_lock) for (var i = 0; i < n; i++) _captured.Add(buffer[offset + i]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (n > 0) lock (_lock) for (var i = 0; i < n; i++) _captured.Add(buffer.Span[i]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
