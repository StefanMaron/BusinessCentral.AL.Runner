// BackupReaderServe — the long-lived half of the process boundary to `bcbak`.
//
// WHY (issue 2263, and the two hangs it caused: issues 2304 and 2336)
//   `bcdb read` re-opens the backup AND re-parses the whole `--symbols` closure on every
//   invocation. Measured on this machine against BC 28.1's W1 demo backup with the 108 .app
//   files a normal run resolves:
//
//     bcdb read "Payment Terms" --symbols <108 apps>   1.85 s   (and 1.85 s again, every time)
//     bcdb serve --symbols <108 apps>, 5 tables        2.49 s   total
//
//   The symbol parse is the whole cost and it is paid once in serve mode. Under the
//   on-demand hydration policy a table is read the first time anything touches it, so a test
//   that walks 63 tables paid ~63 x 1.85 s INSIDE the test body — which is what made
//   "API Setup UT".TestSalesInvoicesAreCreatedOnAPISetup look like a non-terminating
//   repeat/until loop rather than the same read repeated 63 times. It is not a loop bug: the
//   captured frame just happened to be whatever the test was doing when the per-test timeout
//   fired.
//
// SCOPE OF THIS SLICE
//   Only `read --format json` is switched, because that is the command issued once per table.
//   `tables`, `companies`, `describe` and the merge probe run ONCE per run, so routing them
//   through serve buys nothing and would mean translating three more output shapes. Issue
//   2263 stays open for those.
//
// THE ANSWER MUST BE IDENTICAL, NOT MERELY SIMILAR
//   Serve answers `{"headers":[...],"rows":[[...]]}`; the CLI prints an array of objects. This
//   file rebuilds the CLI's exact shape from the serve answer, so
//   TestDataProvisioner.ParseRows stays the ONE projection both transports go through and
//   cannot drift between them. BackupReaderServeTests pins that equivalence directly.
//
// FALLBACK IS A TRANSPORT FALLBACK, NOT A SILENT DEFAULT
//   If the reader on PATH has no serve mode, or the pipe breaks, this warns once naming the
//   reason and every subsequent read goes back to one process per command — the same rows,
//   more slowly. A request the reader REFUSES (`{"ok":false}`) is not a transport problem and
//   is raised as BackupReaderException carrying the reader's own text, exactly as a non-zero
//   exit would be. See .claude/rules/loud-failures.md: the thing that must never happen is an
//   empty result presented as an answer, and neither branch here can produce one.
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AlRunner.Infrastructure;

internal static class BackupReaderServe
{
    private static readonly object _gate = new();

    private static Process? _proc;
    private static StreamWriter? _stdin;
    private static StreamReader? _stdout;
    private static string? _sessionKey;
    private static int _nextId;
    private static bool _disabled;
    private static bool _warned;
    private static bool _exitHookInstalled;

    /// <summary>How many read requests were answered over the live session. Test/diagnostic
    /// seam and the number the PR's claim rests on — one process, N answers.</summary>
    internal static int ServedReads { get; private set; }

    private static bool? _enabledByEnv;

    /// <summary>Serve mode is on unless AL_RUNNER_BCBAK_SERVE=0. Read once: this is consulted
    /// per table read.</summary>
    internal static bool EnabledByEnv
        => _enabledByEnv ??= Environment.GetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE") != "0";

    internal static void ResetForTests()
    {
        lock (_gate)
        {
            Shutdown();
            _disabled = false;
            _warned = false;
            _enabledByEnv = null;
            ServedReads = 0;
        }
    }

    // ─────────────────────────────────────────────────── request translation ──

    /// <summary>
    /// The parsed form of a `read` command line. Null <see cref="Symbols"/> means the command
    /// carried no --symbols (the reader then answers with SQL column names).
    /// </summary>
    internal sealed record ReadRequest(string Backup, string? Symbols, string Json);

    /// <summary>
    /// Translate a `read ... --format json` argument vector into a serve request. Returns
    /// false for anything else — another command, a non-json format, or an option this slice
    /// does not model — so the caller falls back to one process per command rather than
    /// guessing at a request the reader would answer differently.
    /// </summary>
    internal static bool TryBuildReadRequest(IReadOnlyList<string> args, int id, out ReadRequest? request)
    {
        request = null;
        if (args.Count < 2 || !string.Equals(args[0], "read", StringComparison.Ordinal)) return false;

        var backup = args[1];
        string? table = null, company = null, app = null, select = null, symbols = null, format = null;
        int? top = null;
        var mergeExtensions = false;

        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--merge-extensions": mergeExtensions = true; continue;
                case "--table": if (++i >= args.Count) return false; table = args[i]; continue;
                case "--company": if (++i >= args.Count) return false; company = args[i]; continue;
                case "--app": if (++i >= args.Count) return false; app = args[i]; continue;
                case "--select": if (++i >= args.Count) return false; select = args[i]; continue;
                case "--symbols": if (++i >= args.Count) return false; symbols = args[i]; continue;
                case "--format": if (++i >= args.Count) return false; format = args[i]; continue;
                case "--top":
                    if (++i >= args.Count) return false;
                    if (!int.TryParse(args[i], out var n)) return false;
                    top = n;
                    continue;
                default: return false;   // an option this slice does not model
            }
        }

        if (table == null) return false;
        if (format != null && !string.Equals(format, "json", StringComparison.Ordinal)) return false;

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("cmd", "read");
            w.WriteString("table", table);
            if (company != null) w.WriteString("company", company);
            if (app != null) w.WriteString("app", app);
            if (select != null) w.WriteString("select", select);
            if (top != null) w.WriteNumber("top", top.Value);
            // HYPHENATED, and only written when true: "a key the command does not accept fails
            // the request instead of being ignored", so an unnecessary key is a hard error.
            if (mergeExtensions) w.WriteBoolean("merge-extensions", true);
            w.WriteEndObject();
        }
        request = new ReadRequest(backup, symbols, Encoding.UTF8.GetString(buffer.WrittenSpan));
        return true;
    }

    // ────────────────────────────────────────────────── response translation ──

    /// <summary>
    /// Rebuild the CLI's `[{column: value, ...}, ...]` text from a serve `read` answer, so both
    /// transports feed TestDataProvisioner.ParseRows the same thing. Throws
    /// <see cref="BackupReaderException"/> carrying the reader's own text when the reader
    /// refused the request.
    /// </summary>
    internal static string TranslateReadResponse(string responseLine, string describeRequest)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseLine); }
        catch (JsonException ex)
        {
            throw new BackupReaderException(
                $"the backup reader's serve answer could not be parsed ({ex.Message}) for: {describeRequest}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                throw new BackupReaderException(
                    $"the backup reader refused: {describeRequest}\n  {error ?? responseLine}");
            }
            if (!root.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
                throw new BackupReaderException(
                    $"the backup reader's serve answer carries no 'headers' array for: {describeRequest}");

            var names = new List<string>();
            foreach (var h in headers.EnumerateArray()) names.Add(h.GetString() ?? "");

            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartArray();
                if (root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        w.WriteStartObject();
                        var i = 0;
                        foreach (var cell in row.EnumerateArray())
                        {
                            // A row longer than the header list would silently drop columns;
                            // that is a reader contract break, not something to absorb.
                            if (i >= names.Count)
                                throw new BackupReaderException(
                                    $"the backup reader returned a row with more cells than headers "
                                    + $"({names.Count}) for: {describeRequest}");
                            w.WritePropertyName(names[i++]);
                            cell.WriteTo(w);
                        }
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    // ───────────────────────────────────────────────────────────── transport ──

    /// <summary>
    /// Answer <paramref name="args"/> over the shared serve process. False means "not handled
    /// here" — the caller must spawn a one-shot process. A refusal BY the reader throws.
    /// </summary>
    internal static bool TryRun(IReadOnlyList<string> args, out string output)
    {
        output = "";
        if (!EnabledByEnv) return false;

        lock (_gate)
        {
            if (_disabled) return false;
            if (!TryBuildReadRequest(args, _nextId + 1, out var request) || request == null) return false;

            var key = request.Backup + " " + (request.Symbols ?? "");
            try
            {
                if (_sessionKey != key) Start(request.Backup, request.Symbols, key);
                _nextId++;
                _stdin!.WriteLine(request.Json);
                _stdin.Flush();
                var line = _stdout!.ReadLine();
                if (line == null)
                    throw new IOException("the backup reader's serve process closed its output");
                output = TranslateReadResponse(line, DescribeRead(args));
                ServedReads++;
                return true;
            }
            catch (BackupReaderException)
            {
                // The reader answered and said no. That is an answer, not a broken transport:
                // keep the session and let the caller's per-table tolerance handle it.
                throw;
            }
            catch (Exception ex)
            {
                Disable($"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    private static string DescribeRead(IReadOnlyList<string> args)
        => "bcbak " + string.Join(' ', args.Take(Math.Min(args.Count, 8)));

    private static void Start(string backup, string? symbols, string key)
    {
        Shutdown();

        var exe = BackupReaderTool.Resolve();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add(backup);
        if (!string.IsNullOrEmpty(symbols))
        {
            psi.ArgumentList.Add("--symbols");
            psi.ArgumentList.Add(symbols);
        }

        var proc = Process.Start(psi)
            ?? throw new IOException($"failed to start the backup reader's serve mode: {exe}");

        // stderr MUST be drained or the child blocks once its pipe fills, which would look
        // like a hang in the middle of hydration — the very failure mode this file removes.
        _ = proc.StandardError.ReadToEndAsync();

        _proc = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
        _sessionKey = key;
        InstallExitHook();
    }

    private static void InstallExitHook()
    {
        if (_exitHookInstalled) return;
        _exitHookInstalled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Shutdown(); } catch { } };
    }

    /// <summary>Stop the serve process. Idempotent, and never throws — it runs from
    /// ProcessExit as well as from Start().</summary>
    internal static void Shutdown()
    {
        var proc = _proc;
        _proc = null;
        _sessionKey = null;
        var stdin = _stdin;
        _stdin = null;
        _stdout = null;
        if (proc == null) return;

        try
        {
            if (stdin != null && !proc.HasExited)
            {
                stdin.WriteLine("{\"id\":0,\"cmd\":\"quit\"}");
                stdin.Flush();
                stdin.Close();
            }
        }
        catch { }
        try
        {
            if (!proc.WaitForExit(3000)) proc.Kill(entireProcessTree: true);
        }
        catch { }
        try { proc.Dispose(); } catch { }
    }

    private static void Disable(string reason)
    {
        Shutdown();
        _disabled = true;
        if (_warned) return;
        _warned = true;
        // `[warn]` is exempt from Log's component filter, so this reaches the terminal: the
        // run is about to get much slower and the user is entitled to know why.
        Console.Error.WriteLine(
            $"[warn] --test-data: the backup reader's serve mode is unavailable ({reason}); "
            + "falling back to one reader process per table, which is correct but far slower. "
            + "Upgrade the reader on AL_RUNNER_BCBAK, or set AL_RUNNER_BCBAK_SERVE=0 to silence this.");
    }
}
