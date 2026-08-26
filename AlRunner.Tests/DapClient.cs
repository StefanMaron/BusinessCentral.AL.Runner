using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

/// <summary>
/// Spawns al-runner in <c>--dap</c> mode and drives it over the real DAP TCP wire
/// format (AlRunner.Infrastructure.DapTransport — the exact same class the runner
/// itself uses on the server side of this connection), for issue #1642. Unlike
/// CliServer/SharedCliServer, a DAP session is inherently single-shot (one client,
/// one bundle, one run) so there is no shared-process variant of this helper.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly Process _process;
    private readonly TcpClient _tcp;
    private readonly DapTransport _transport;
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _stdout = new();

    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }
    public string StdOut { get { lock (_stdout) return _stdout.ToString(); } }

    private DapClient(Process process, TcpClient tcp, DapTransport transport)
    {
        _process = process;
        _tcp = tcp;
        _transport = transport;
    }

    /// <summary>Starts al-runner --dap on a free loopback port and connects to it,
    /// retrying the connect until the runner's own "[dap] listening on" line has
    /// appeared on stdout (readiness is signalled there — --dap does NOT redirect
    /// Console.Out to stderr the way --server does, see Program.cs's --dap block)
    /// or <paramref name="readyTimeout"/> elapses.</summary>
    public static async Task<DapClient> StartAsync(string bundleDir, TimeSpan? readyTimeout = null)
    {
        var port = GetFreeLoopbackPort();
        var argList = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg +
            $" --dap {port} \"{bundleDir}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argList.ToString(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        var proc = Process.Start(psi)!;
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        var listeningTcs = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                lock (stderr) stderr.AppendLine(line);
        });
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                lock (stdout) stdout.AppendLine(line);
                if (line.Contains("[dap] listening on")) listeningTcs.TrySetResult();
            }
        });

        var timeout = readyTimeout ?? TimeSpan.FromSeconds(120);
        var completed = await Task.WhenAny(listeningTcs.Task, Task.Delay(timeout));
        if (completed != listeningTcs.Task)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException(
                $"al-runner --dap did not report listening within {timeout.TotalSeconds:F0}s.\n" +
                $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }

        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var transport = new DapTransport(tcp.GetStream(), tcp.GetStream());

        var client = new DapClient(proc, tcp, transport);
        lock (stderr) client._stderr.Append(stderr);
        lock (stdout) client._stdout.Append(stdout);
        // Keep draining after handoff too, so later output (e.g. [dap] PASS/FAIL
        // lines) is visible in a failure message.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                lock (client._stderr) client._stderr.AppendLine(line);
        });
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                lock (client._stdout) client._stdout.AppendLine(line);
        });

        return client;
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Sends a DAP request and returns its seq (for matching against the
    /// eventual response's request_seq via <see cref="ReadUntilResponseAsync"/>).</summary>
    public int SendRequest(string command, object? arguments = null) => _transport.WriteRequest(command, arguments);

    /// <summary>Reads messages until the response to <paramref name="requestSeq"/>
    /// arrives, returning it. Any events seen along the way are appended to
    /// <paramref name="events"/> if given (so a caller can inspect e.g. an
    /// `initialized` event that arrives between the `initialize` request and its
    /// response, without a second read loop).</summary>
    public async Task<JsonElement> ReadUntilResponseAsync(int requestSeq, List<JsonElement>? events = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(timeout ?? TimeSpan.FromSeconds(30));
            var root = msg.Raw.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "response" && root.TryGetProperty("request_seq", out var rs) && rs.GetInt32() == requestSeq)
                return root;
            if (type == "event") events?.Add(root);
        }
        throw new TimeoutException($"no response to request seq {requestSeq} within timeout.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
    }

    /// <summary>Reads messages until an event named <paramref name="eventName"/>
    /// arrives, returning its body. Used to wait for e.g. "stopped". Every event
    /// seen along the way (including the terminal one) is appended to <paramref
    /// name="allEvents"/> if given, so a caller can assert on what did NOT arrive
    /// (e.g. "no 'stopped' event fired") without a second read loop.</summary>
    public async Task<JsonElement> ReadUntilEventAsync(string eventName, TimeSpan? timeout = null, List<JsonElement>? allEvents = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(60);
        var deadline = DateTime.UtcNow + t;
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReadOneAsync(t);
            var root = msg.Raw.RootElement;
            if (root.GetProperty("type").GetString() != "event") continue;
            allEvents?.Add(root);
            if (root.TryGetProperty("event", out var ev) && ev.GetString() == eventName)
                return root;
        }
        throw new TimeoutException($"event '{eventName}' did not arrive within {t.TotalSeconds:F0}s.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
    }

    private async Task<DapIncomingMessage> ReadOneAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var msg = await _transport.ReadMessageAsync(cts.Token);
        if (msg == null)
            throw new Exception($"--dap connection closed unexpectedly.\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}");
        return msg;
    }

    public async ValueTask DisposeAsync()
    {
        try { _transport.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
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
