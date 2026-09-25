// BackupReaderServe — the long-lived half of the process boundary to `bcbak`: one `serve`
// child per (backup, symbol set), answering `read`, `tables` and `companies` for the whole run.
// Why, and what it measured: docs/test-data-reader-transport.md.
//
// THE ANSWER MUST BE IDENTICAL, NOT MERELY SIMILAR
//   Serve answers JSON; the CLI prints text. Every Translate* below rebuilds the CLI's exact
//   text, so BackupCatalog / TestDataProvisioner.ParseRows stay the ONE parser both transports
//   go through. BackupReaderServeTests pins each shape; BackupReaderServeSessionTests pins the
//   real reader's two transports against each other.
//
// THREE WAYS A REQUEST CAN GO WRONG, AND ONLY ONE FALLS BACK
//   - the reader REFUSED (`"ok": false`): an answer. BackupReaderException with its own text;
//     the session stays up.
//   - the session never answered anything (a reader with no `serve`, or one that cannot start):
//     warn once and fall back to one process per command — the same rows, more slowly.
//   - the session died, hung, or answered out of protocol AFTER it had worked: loud
//     BackupReaderException, session torn down. Never a fallback — a reader that crashed on
//     this request would be asked the same thing again, and a hang has no answer to fall to.
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AlRunner.Infrastructure;

internal static class BackupReaderServe
{
    private static readonly object _gate = new();

    private static Process? _proc;
    private static StreamWriter? _stdin;
    private static StreamReader? _stdout;
    private static Task<string>? _stderr;
    private static string? _sessionBackup;
    private static string? _sessionKey;
    private static bool _sessionAnswered;
    private static int _nextId;
    private static bool _disabled;
    private static bool _warned;
    private static bool _exitHookInstalled;

    /// <summary>Requests answered over a live session. Test/diagnostic seam.</summary>
    internal static int ServedRequests { get; private set; }

    /// <summary>Serve children started. The number the "one reader process per run" claim
    /// rests on.</summary>
    internal static int SessionsStarted { get; private set; }

    /// <summary>Why serve mode was abandoned for this process, or null while it is in use.</summary>
    internal static string? FallbackReason { get; private set; }

    private static bool? _enabledByEnv;

    /// <summary>Serve mode is on unless AL_RUNNER_BCBAK_SERVE=0. Read once: this is consulted
    /// per request.</summary>
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
            ServedRequests = 0;
            SessionsStarted = 0;
            FallbackReason = null;
        }
    }

    // ─────────────────────────────────────────────────── request translation ──

    /// <summary>
    /// A command line translated into one serve request. <see cref="SymbolsMatter"/> is false
    /// for a command whose answer does not depend on the schema (`companies`), which any live
    /// session on the same backup may answer.
    /// </summary>
    internal sealed record ServeRequest(string Command, string Backup, string? Symbols, bool SymbolsMatter, string Json);

    /// <summary>
    /// Translate a CLI argument vector into a serve request. False for anything this does not
    /// model — `describe`, a non-json `read`, an unknown option — so the caller spawns rather
    /// than sends a request the reader would answer differently.
    /// </summary>
    internal static bool TryBuildRequest(IReadOnlyList<string> args, int id, out ServeRequest? request)
    {
        request = null;
        if (args.Count < 2) return false;
        return args[0] switch
        {
            "read" => TryBuildRead(args, id, out request),
            "tables" => TryBuildTables(args, id, out request),
            "companies" => TryBuildCompanies(args, id, out request),
            _ => false,
        };
    }

    private static bool TryBuildRead(IReadOnlyList<string> args, int id, out ServeRequest? request)
    {
        request = null;
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
                default: return false;
            }
        }

        if (table == null) return false;
        if (format != null && !string.Equals(format, "json", StringComparison.Ordinal)) return false;

        var json = WriteRequest(id, "read", w =>
        {
            w.WriteString("table", table);
            if (company != null) w.WriteString("company", company);
            if (app != null) w.WriteString("app", app);
            if (select != null) w.WriteString("select", select);
            if (top != null) w.WriteNumber("top", top.Value);
            // HYPHENATED, and only written when true: the reader fails a request carrying a key
            // the command does not accept, so an unnecessary key is a hard error.
            if (mergeExtensions) w.WriteBoolean("merge-extensions", true);
        });
        request = new ServeRequest("read", backup, symbols, SymbolsMatter: true, json);
        return true;
    }

    // `tables` accepts only `id` and `cmd` as request keys; the symbol set is the session's.
    private static bool TryBuildTables(IReadOnlyList<string> args, int id, out ServeRequest? request)
    {
        request = null;
        string? symbols = null;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] != "--symbols" || ++i >= args.Count) return false;
            symbols = args[i];
        }
        request = new ServeRequest("tables", args[1], symbols, SymbolsMatter: true, WriteRequest(id, "tables", null));
        return true;
    }

    private static bool TryBuildCompanies(IReadOnlyList<string> args, int id, out ServeRequest? request)
    {
        request = null;
        if (args.Count != 2) return false;
        request = new ServeRequest("companies", args[1], null, SymbolsMatter: false, WriteRequest(id, "companies", null));
        return true;
    }

    private static string WriteRequest(int id, string cmd, Action<Utf8JsonWriter>? body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteString("cmd", cmd);
            body?.Invoke(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ────────────────────────────────────────────────── response translation ──

    /// <summary>Translate one answer line for <paramref name="command"/> into the CLI's text.
    /// Throws <see cref="BackupReaderException"/> on a refusal or an answer missing what the
    /// command must carry — never returns an empty answer for a malformed one.</summary>
    internal static string TranslateResponse(string command, string responseLine, string describeRequest)
    {
        using var doc = Parse(responseLine, describeRequest);
        var root = doc.RootElement;
        ThrowIfRefused(root, responseLine, describeRequest);
        return command switch
        {
            "read" => TranslateRead(root, describeRequest),
            "tables" => TranslateTables(root, describeRequest),
            "companies" => TranslateCompanies(root, describeRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    /// <summary>Rebuild the CLI's `[{column: value, ...}, ...]` text from a serve `read` answer.</summary>
    internal static string TranslateReadResponse(string responseLine, string describeRequest)
        => TranslateResponse("read", responseLine, describeRequest);

    private static JsonDocument Parse(string responseLine, string describeRequest)
    {
        try { return JsonDocument.Parse(responseLine); }
        catch (JsonException ex)
        {
            throw new BackupReaderException(
                $"the backup reader's serve answer could not be parsed ({ex.Message}) for: {describeRequest}");
        }
    }

    private static void ThrowIfRefused(JsonElement root, string responseLine, string describeRequest)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return;
        var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e)
            && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        // #2779: the reader's own text on line 1 — bundle reporters keep only line 1.
        throw new BackupReaderException(
            $"the backup reader refused: {BackupReaderTool.Condense(error ?? responseLine)} "
            + $"— request: {describeRequest}");
    }

    private static string TranslateRead(JsonElement root, string describeRequest)
    {
        if (!root.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
            throw new BackupReaderException(
                $"the backup reader's serve answer carries no 'headers' array for: {describeRequest}");

        var names = new List<string>();
        foreach (var h in headers.EnumerateArray()) names.Add(h.GetString() ?? "");

        var buffer = new ArrayBufferWriter<byte>();
        // Relaxed: a header like "Größe" stays as the CLI prints it rather than \u-escaped.
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
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
                        if (i >= names.Count)
                            throw new BackupReaderException(
                                $"the backup reader returned a row with more cells than headers "
                                + $"({names.Count}) for: {describeRequest}");
                        w.WritePropertyName(names[i++]);
                        // The reader's own bytes: WriteTo re-escapes non-ASCII ("€" -> "\u20AC"),
                        // which the W1 differential caught on Currency (#2263).
                        w.WriteRawValue(cell.GetRawText(), skipInputValidation: true);
                    }
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The CLI's `tables` line: `{rows,8}  {compression}  {company|-}\t{name}\t{resolution}`,
    /// resolution `{id} "{name}" ({app})` or `-`. Byte-identical to `bcdb 0.1.2`'s own output on
    /// all 3,955 lines of the 28.1 W1 backup (docs/test-data-reader-transport.md).
    /// </summary>
    private static string TranslateTables(JsonElement root, string describeRequest)
    {
        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
            throw new BackupReaderException(
                $"the backup reader's serve answer carries no 'tables' array for: {describeRequest}");

        var sb = new StringBuilder();
        foreach (var t in tables.EnumerateArray())
        {
            var name = RequiredString(t, "name", describeRequest);
            var compression = RequiredString(t, "compression", describeRequest);
            if (!t.TryGetProperty("rows", out var rowsEl) || !rowsEl.TryGetInt64(out var rows))
                throw Malformed("rows", describeRequest);
            var company = t.TryGetProperty("company", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()! : "-";

            var resolution = "-";
            if (t.TryGetProperty("al", out var al) && al.ValueKind == JsonValueKind.Object)
            {
                if (!al.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var alId))
                    throw Malformed("al.id", describeRequest);
                resolution = string.Create(CultureInfo.InvariantCulture,
                    $"{alId} \"{RequiredString(al, "name", describeRequest)}\" ({RequiredString(al, "app", describeRequest)})");
            }
            sb.Append(rows.ToString(CultureInfo.InvariantCulture).PadLeft(8))
              .Append("  ").Append(compression).Append("  ").Append(company)
              .Append('\t').Append(name).Append('\t').Append(resolution).Append('\n');
        }
        return sb.ToString();
    }

    private static string TranslateCompanies(JsonElement root, string describeRequest)
    {
        if (!root.TryGetProperty("companies", out var companies) || companies.ValueKind != JsonValueKind.Array)
            throw new BackupReaderException(
                $"the backup reader's serve answer carries no 'companies' array for: {describeRequest}");
        var sb = new StringBuilder();
        foreach (var c in companies.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.String) throw Malformed("companies[]", describeRequest);
            sb.Append(c.GetString()).Append('\n');
        }
        return sb.ToString();
    }

    private static string RequiredString(JsonElement e, string key, string describeRequest)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw Malformed(key, describeRequest);

    private static BackupReaderException Malformed(string key, string describeRequest)
        => new($"the backup reader's serve answer has no usable '{key}' for: {describeRequest}");

    // ───────────────────────────────────────────────────────────── transport ──

    /// <summary>
    /// Answer <paramref name="args"/> over the shared serve process. False means "not handled
    /// here" — the caller spawns a one-shot process. A refusal, a death, a hang or an
    /// out-of-protocol answer throws; see the file header for which falls back.
    /// </summary>
    internal static bool TryRun(IReadOnlyList<string> args, out string output, int timeoutMs = 600_000)
    {
        output = "";
        if (!EnabledByEnv) return false;

        lock (_gate)
        {
            if (_disabled) return false;
            var id = _nextId + 1;
            if (!TryBuildRequest(args, id, out var request) || request == null) return false;
            var describe = Describe(args);

            string? line;
            try
            {
                EnsureSession(request);
                _nextId = id;
                _stdin!.WriteLine(request.Json);
                _stdin.Flush();
                line = ReadAnswer(timeoutMs, describe);
            }
            catch (BackupReaderException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return SessionLost($"{ex.GetType().Name}: {ex.Message}", describe);
            }
            if (line == null) return SessionLost("the serve process closed its output", describe);

            output = Answer(request, line, id, describe);
            ServedRequests++;
            return true;
        }
    }

    private static void EnsureSession(ServeRequest request)
    {
        if (_proc != null && _sessionBackup == request.Backup
            && (!request.SymbolsMatter || _sessionKey == SessionKey(request.Backup, request.Symbols)))
            return;
        Start(request.Backup, request.Symbols);
    }

    // "\0" as an escape, not a literal NUL byte: a literal one makes ripgrep treat this file as
    // binary (AlRunner.Tests/SourceFilesAreSearchableGuardTests).
    private static string SessionKey(string backup, string? symbols) => backup + "\0" + (symbols ?? "");

    private static string? ReadAnswer(int timeoutMs, string describe)
    {
        var pending = _stdout!.ReadLineAsync();
        try
        {
            if (pending.Wait(timeoutMs)) return pending.Result;
        }
        catch (AggregateException ae) when (ae.InnerException is IOException io) { throw io; }
        Shutdown();
        throw new BackupReaderException(
            $"the backup reader's serve process did not answer within {timeoutMs}ms and was killed "
            + $"— request: {describe}");
    }

    // One answer line, checked against the request it must belong to. A malformed or foreign
    // line means the stream is out of step, and every later answer would be attributed to the
    // wrong request — so the session goes, loudly. A refusal is in step and keeps it.
    private static string Answer(ServeRequest request, string line, int id, string describe)
    {
        int? answeredId = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var n))
                answeredId = n;
        }
        catch (JsonException) { }
        if (answeredId != id)
        {
            Shutdown();
            throw new BackupReaderException(
                $"the backup reader's serve process answered out of protocol (expected id {id}, got "
                + $"'{Truncate(line)}'); the session was stopped — request: {describe}");
        }
        // In step: from here a death is mid-run, not "this reader has no serve mode".
        _sessionAnswered = true;
        return TranslateResponse(request.Command, line, describe);
    }

    private static bool SessionLost(string cause, string describe)
    {
        var proc = _proc;
        var stderr = _stderr;
        var answered = _sessionAnswered;
        string exit = "";
        try
        {
            if (proc != null && proc.WaitForExit(3000)) exit = $"exit {proc.ExitCode}, ";
        }
        catch { }
        var said = stderr != null && stderr.Wait(1000) ? stderr.Result : null;
        var reason = $"{cause}; {exit}{BackupReaderTool.Condense(said)}";

        if (!answered)
        {
            Disable(reason);
            return false;
        }
        Shutdown();
        throw new BackupReaderException(
            $"the backup reader's serve process died mid-run ({reason}) — request: {describe}");
    }

    private static string Describe(IReadOnlyList<string> args)
        => "bcbak " + string.Join(' ', args.Take(Math.Min(args.Count, 8)));

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private static void Start(string backup, string? symbols)
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
        SessionsStarted++;

        // Drained, or the child blocks once its stderr pipe fills; kept, because it is the
        // diagnosis when the session dies.
        _stderr = proc.StandardError.ReadToEndAsync();
        _proc = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
        _sessionBackup = backup;
        _sessionKey = SessionKey(backup, symbols);
        _sessionAnswered = false;
        InstallExitHook();
    }

    private static void InstallExitHook()
    {
        if (_exitHookInstalled) return;
        _exitHookInstalled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Shutdown(); } catch { } };
    }

    /// <summary>Stop the serve process: `quit`, then kill after 3 s. Idempotent and never
    /// throws. A runner killed outright closes the child's stdin, and the reader exits on EOF.</summary>
    internal static void Shutdown()
    {
        var proc = _proc;
        _proc = null;
        _sessionKey = null;
        _sessionBackup = null;
        _stderr = null;
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
        FallbackReason = reason;
        if (_warned) return;
        _warned = true;
        // `[warn]` is exempt from Log's component filter, so this reaches the terminal.
        Console.Error.WriteLine(
            $"[warn] --test-data: the backup reader's serve mode is unavailable ({reason}); "
            + "falling back to one reader process per command, which is correct but far slower. "
            + "Upgrade the reader on AL_RUNNER_BCBAK, or set AL_RUNNER_BCBAK_SERVE=0 to silence this.");
    }
}
