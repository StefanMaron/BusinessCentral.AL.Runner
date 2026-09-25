// BackupReaderServeSessionTests — issue #2263: the serve transport as a PROCESS, not as two
// pure translations (those are BackupReaderServeTests).
//
// A stand-in reader speaks the serve protocol on the AL_RUNNER_BCBAK seam and logs every
// process it is started as, so "one reader process per run" is a count on that log. Its
// failure modes are the three the transport must tell apart: no serve mode (fall back, with a
// reason), a session that dies or hangs after working (loud), and an answer that is out of
// protocol (loud). The real reader against the real W1 backup is the differential at the end;
// it needs a ~1 GB backup no CI leg provisions, so it skips there, visibly.
using System.Diagnostics;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;
using Xunit.Abstractions;

namespace AlRunner.Tests;

public static class ServeStubReader
{
    public enum Mode { Normal, NoServe, DieOnSecondRequest, Hang, ForeignId, Garbage }

    internal const string DefaultTablesJson =
        """[{"company":"CRONUS","name":"Payment Terms","rows":12,"compression":"page","al":{"id":3,"name":"Payment Terms","app":"Base Application"}},{"company":null,"name":"$ndo$x","rows":0,"compression":"none"}]""";

    internal const string DefaultTablesText =
        "      12  page  CRONUS\\tPayment Terms\\t3 \"Payment Terms\" (Base Application)\\n"
        + "       0  none  -\\t$ndo$x\\t-\\n";

    /// <summary>Writes an executable stand-in to <paramref name="path"/>. Every start appends
    /// `serve|&lt;symbols&gt;` or `spawn|&lt;command&gt;` to <paramref name="log"/>.</summary>
    internal static void Write(string path, string log, Mode mode,
        string tablesJson = DefaultTablesJson, string tablesText = DefaultTablesText)
    {
        var onServeStart = mode == Mode.NoServe
            ? "echo \"error: unknown command 'serve'\" >&2; exit 2"
            : "";
        var perRequest = mode switch
        {
            Mode.DieOnSecondRequest => "if [ $n -ge 2 ]; then echo 'fatal: reader crashed on request 2' >&2; exit 3; fi",
            Mode.Hang => "sleep 30",
            Mode.ForeignId => "printf '{\"id\":999,\"ok\":true,\"companies\":[]}\\n'; continue",
            Mode.Garbage => "printf 'panic: not json\\n'; continue",
            _ => "",
        };
        File.WriteAllText(path, $$"""
            #!/bin/sh
            log='{{log}}'
            if [ "$1" != "serve" ]; then
              echo "spawn|$1" >> "$log"
              case "$1" in
                companies) printf 'CRONUS\nMy Company\n' ;;
                tables) printf '{{tablesText}}' ;;
              esac
              exit 0
            fi
            echo "serve|$4" >> "$log"
            echo "$$" > "$log.pid"
            {{onServeStart}}
            n=0
            while IFS= read -r line; do
              n=$((n+1))
              id=$(printf '%s' "$line" | sed -n 's/^{"id":\([0-9]*\),.*/\1/p')
              case "$line" in *'"cmd":"quit"'*) exit 0 ;; esac
              {{perRequest}}
              case "$line" in
                *'"cmd":"companies"'*) printf '{"id":%s,"ok":true,"companies":["CRONUS","My Company"]}\n' "$id" ;;
                *'"cmd":"tables"'*) printf '{"id":%s,"ok":true,"tables":{{tablesJson}}}\n' "$id" ;;
                *'"merge-extensions":true'*) printf '{"id":%s,"ok":true,"headers":["A","B"],"rows":[[1,2]]}\n' "$id" ;;
                *'"top":1'*) printf '{"id":%s,"ok":true,"headers":["A"],"rows":[[1]]}\n' "$id" ;;
                *'"cmd":"read"'*) printf '{"id":%s,"ok":true,"headers":["A"],"rows":[]}\n' "$id" ;;
              esac
            done
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static string[] Log(string log)
        => File.Exists(log) ? File.ReadAllLines(log).Where(l => l.Length > 0).ToArray() : Array.Empty<string>();
}

[Collection(TestDataStaticsSerialCollection.Name)]
public sealed class BackupReaderServeSessionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _log;
    private readonly string _reader;
    private readonly string _backup;
    private readonly string? _previousReader;
    private readonly string? _previousServe;
    private const string Symbols = "/apps/Base.app,/apps/System.app";

    public BackupReaderServeSessionTests()
    {
        _dir = Directory.CreateDirectory(TestScratch.Dir("al-runner-2263-serve-session")).FullName;
        _log = Path.Combine(_dir, "reader.log");
        _reader = Path.Combine(_dir, "bcbak");
        _backup = Path.Combine(_dir, "BusinessCentral-W1.bak");
        _previousReader = Environment.GetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar);
        _previousServe = Environment.GetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE");
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, _reader);
        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", null);
        BackupReaderTool.ResetForTests();
        BackupReaderServe.ResetForTests();
    }

    public void Dispose()
    {
        BackupReaderServe.ResetForTests();
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, _previousReader);
        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", _previousServe);
        BackupReaderTool.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Stub(ServeStubReader.Mode mode)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the stand-in reader is a shell script");
        ServeStubReader.Write(_reader, _log, mode);
    }

    private string[] Tables() => new[] { "tables", _backup, "--symbols", Symbols };
    private string[] Companies() => new[] { "companies", _backup };
    private string[] Log() => ServeStubReader.Log(_log);

    [SkippableFact]
    public void TablesThenCompanies_OneReaderProcess_SameEntriesAsOneProcessPerCommand()
    {
        Stub(ServeStubReader.Mode.Normal);

        var servedTables = BackupReaderTool.Run(Tables());
        var servedCompanies = BackupReaderTool.Run(Companies());

        Assert.Equal(new[] { "serve|" + Symbols }, Log());
        Assert.Equal(1, BackupReaderServe.SessionsStarted);
        Assert.Equal(2, BackupReaderServe.ServedRequests);

        // The same two commands, one process each.
        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", "0");
        BackupReaderServe.ResetForTests();
        var spawnedTables = BackupReaderTool.Run(Tables());
        var spawnedCompanies = BackupReaderTool.Run(Companies());
        Assert.Equal(new[] { "spawn|tables", "spawn|companies" }, Log().Skip(1).ToArray());

        Assert.Equal(spawnedTables, servedTables);
        Assert.Equal(BackupCatalog.ParseTables(spawnedTables), BackupCatalog.ParseTables(servedTables));
        Assert.Equal(spawnedCompanies, servedCompanies);
        Assert.Equal(new[] { "CRONUS", "My Company" }, BackupCatalog.ParseCompanies(servedCompanies));
        Assert.Equal(new BackupTableEntry(12, "page", "CRONUS", "Payment Terms", 3, "Base Application"),
            BackupCatalog.ParseTables(servedTables)[0]);
    }

    /// <summary>`companies` does not depend on the schema, so it opens a session without
    /// symbols; `tables` DOES, and must not be answered by that session. This is why
    /// TestDataProvisioner.Arm asks for `tables` first.</summary>
    [SkippableFact]
    public void ASessionWithoutSymbols_DoesNotAnswerATablesRequestThatCarriesThem()
    {
        Stub(ServeStubReader.Mode.Normal);

        BackupReaderTool.Run(Companies());
        BackupReaderTool.Run(Tables());

        Assert.Equal(new[] { "serve|", "serve|" + Symbols }, Log());
        Assert.Equal(2, BackupReaderServe.SessionsStarted);
    }

    [SkippableFact]
    public void AReaderWithoutServe_FallsBackToOneProcessPerCommand_AndSaysWhy()
    {
        Stub(ServeStubReader.Mode.NoServe);

        var tables = BackupReaderTool.Run(Tables());

        Assert.Equal(2, BackupCatalog.ParseTables(tables).Count);
        Assert.Equal(new[] { "serve|" + Symbols, "spawn|tables" }, Log());
        Assert.Equal(0, BackupReaderServe.ServedRequests);
        Assert.NotNull(BackupReaderServe.FallbackReason);
        Assert.Contains("unknown command 'serve'", BackupReaderServe.FallbackReason!, StringComparison.Ordinal);

        // Once, not per command.
        BackupReaderTool.Run(Companies());
        Assert.Equal(new[] { "serve|" + Symbols, "spawn|tables", "spawn|companies" }, Log());
    }

    [SkippableFact]
    public void ASessionThatDiesAfterAnswering_IsReportedWithItsOwnReason_NotFallenBackFrom()
    {
        Stub(ServeStubReader.Mode.DieOnSecondRequest);

        Assert.Equal(2, BackupCatalog.ParseTables(BackupReaderTool.Run(Tables())).Count);
        var ex = Assert.Throws<BackupReaderException>(() => BackupReaderTool.Run(Companies()));

        Assert.Contains("died mid-run", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reader crashed on request 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exit 3", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Log(), l => l.StartsWith("spawn|", StringComparison.Ordinal));
        Assert.Null(BackupReaderServe.FallbackReason);
    }

    [SkippableFact]
    public void AHungSession_IsKilledAndReported_NotWaitedOnForever()
    {
        Stub(ServeStubReader.Mode.Hang);

        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<BackupReaderException>(() => BackupReaderTool.Run(Tables(), timeoutMs: 1500));
        clock.Stop();

        Assert.Contains("did not answer within 1500ms", ex.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");

        // Killed by the timeout itself, not later by Dispose: the child is gone before this
        // class tears anything down (the stand-in would otherwise sleep for 30 s).
        var pid = int.Parse(File.ReadAllText(_log + ".pid").Trim());
        var gone = SpinWait.SpinUntil(() => !IsRunning(pid), TimeSpan.FromSeconds(3));
        Assert.True(gone, $"the hung serve child (pid {pid}) is still running after the timeout");
        Assert.DoesNotContain(Log(), l => l.StartsWith("spawn|", StringComparison.Ordinal));
        Assert.Null(BackupReaderServe.FallbackReason);
    }

    // Absent, or a zombie awaiting reaping: either way it no longer runs.
    private static bool IsRunning(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException) { return false; }
    }

    [SkippableTheory]
    [InlineData(ServeStubReader.Mode.ForeignId, "\"id\":999")]
    [InlineData(ServeStubReader.Mode.Garbage, "panic: not json")]
    public void AnOutOfProtocolAnswer_StopsTheSessionLoudly(ServeStubReader.Mode mode, string quoted)
    {
        Stub(mode);

        var ex = Assert.Throws<BackupReaderException>(() => BackupReaderTool.Run(Companies()));

        Assert.Contains("out of protocol (expected id 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(quoted, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Log(), l => l.StartsWith("spawn|", StringComparison.Ordinal));
    }
}

/// <summary>
/// The per-run claim through the real provisioner: arming reads the catalog, the companies
/// and the merge probe over ONE reader process. Its own class because SetResolvedDeps writes
/// BcCompiler statics, which BcCompilerSharedReferenceCollection serialises.
/// </summary>
[Collection(BcCompilerSharedReferenceCollection.Name)]
public sealed class TestDataArmServeSessionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _log;
    private readonly string? _previousReader;
    private readonly string? _previousServe;

    public TestDataArmServeSessionTests()
    {
        _dir = Directory.CreateDirectory(TestScratch.Dir("al-runner-2263-arm-session")).FullName;
        _log = Path.Combine(_dir, "reader.log");
        _previousReader = Environment.GetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar);
        _previousServe = Environment.GetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE");
    }

    public void Dispose()
    {
        BackupReaderServe.ResetForTests();
        TestDataProvisioner.ResetForTests();
        TestDataOptions.ResetForTests();
        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, _previousReader);
        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", _previousServe);
        BackupReaderTool.ResetForTests();
        BcCompiler.SetResolvedDeps(Array.Empty<(AppManifest, string)>(), Array.Empty<string>());
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [SkippableFact]
    public void Arm_AsksTheCatalogCompaniesAndMergeProbe_OfOneReaderProcess()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the stand-in reader is a shell script");

        var reader = Path.Combine(_dir, "bcbak");
        ServeStubReader.Write(reader, _log, ServeStubReader.Mode.Normal,
            tablesJson: """[{"company":"CRONUS","name":"Touched","rows":7,"compression":"Table","al":{"id":61020,"name":"Touched","app":"Fake App"}},{"company":"CRONUS","name":"Touched$ext","rows":3,"compression":"Table"}]""",
            tablesText: "   7  Table  CRONUS\\tTouched\\t61020 \"Touched\" (Fake App)\\n   3  Table  CRONUS\\tTouched$ext\\t-\\n");
        var backup = Path.Combine(_dir, "BusinessCentral-W1.bak");
        File.WriteAllBytes(backup, new byte[256]);
        var app = Path.Combine(_dir, "Fake_App_1_0_0_0.app");
        File.WriteAllBytes(app, new byte[8]);

        Environment.SetEnvironmentVariable(BackupReaderTool.ExecutableEnvVar, reader);
        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", null);
        BackupReaderTool.ResetForTests();
        BackupReaderServe.ResetForTests();
        TestDataOptions.ResetForTests();
        TestDataProvisioner.ResetForTests();
        BcCompiler.SetResolvedDeps(
            new[]
            {
                (new AppManifest("Fake", "App", new Version(1, 0, 0, 0), Guid.NewGuid(),
                    Array.Empty<DependencyRef>()), app),
            },
            new[] { _dir });
        TestDataOptions.Enabled = true;
        TestDataOptions.ExplicitBackupPath = backup;
        TestDataOptions.CompanyOverride = "CRONUS";

        TestDataProvisioner.Arm();

        // The plan really was built from the served catalog, and the probe really ran.
        Assert.Contains(61020, TestDataProvisioner.ArmedTableIds);
        Assert.Equal(new[] { "serve|" + app }, ServeStubReader.Log(_log));
        Assert.Equal(1, BackupReaderServe.SessionsStarted);
        // tables, companies, and the merge probe's plain and merged reads.
        Assert.Equal(4, BackupReaderServe.ServedRequests);
        Assert.NotNull(RecordPatches.TestDataOnDemandLoader);
    }
}

/// <summary>
/// The real reader, both transports, the real backup: the proof that serve answers what the
/// CLI answers. Skips — visibly — wherever the reader or the 28.1 W1 backup is absent, which is
/// every CI leg by design (the backup is ~1 GB and only the ms-bucket workflows fetch it).
/// </summary>
[Collection(TestDataStaticsSerialCollection.Name)]
public sealed class BackupReaderServeRealBackupTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string? _previousServe;

    public BackupReaderServeRealBackupTests(ITestOutputHelper output)
    {
        _out = output;
        _previousServe = Environment.GetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", _previousServe);
        BackupReaderServe.ResetForTests();
    }

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Backup = Path.Combine(Home, ".al-runner", "test-data", "28.1.49838.53910", "BusinessCentral-W1.bak");
    private static readonly string AppsDir = Path.Combine(Home, ".al-runner", "platform-apps");

    [SkippableFact]
    public void ServeAndOneProcessPerCommand_AnswerIdentically_OnTheW1Backup()
    {
        string? reader = null;
        try { reader = BackupReaderTool.Resolve(); } catch (BackupReaderException) { }
        Skip.If(reader == null, "no backup reader installed");
        Skip.IfNot(File.Exists(Backup), $"no backup at {Backup}");
        var apps = new[] { "Base Application", "System Application", "Business Foundation" }
            .Select(n => Path.Combine(AppsDir, $"Microsoft_{n}_28.1.49838.53910.app")).ToArray();
        Skip.IfNot(apps.All(File.Exists), $"28.1 symbol apps missing under {AppsDir}");
        var symbols = string.Join(',', apps);

        string[][] commands =
        {
            new[] { "tables", Backup, "--symbols", symbols },
            new[] { "companies", Backup },
            Read("Payment Terms", symbols),
            Read("Currency", symbols),
            Read("Customer", symbols),
            Read("G/L Account", symbols),
        };

        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", null);
        BackupReaderServe.ResetForTests();
        var clock = Stopwatch.StartNew();
        var served = commands.Select(c => BackupReaderTool.Run(c)).ToArray();
        var serveTime = clock.Elapsed;
        Assert.Equal(1, BackupReaderServe.SessionsStarted);
        Assert.Equal(commands.Length, BackupReaderServe.ServedRequests);

        Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", "0");
        BackupReaderServe.ResetForTests();
        clock.Restart();
        var spawned = commands.Select(c => BackupReaderTool.Run(c)).ToArray();
        var spawnTime = clock.Elapsed;
        Assert.Equal(0, BackupReaderServe.ServedRequests);

        Assert.Equal(spawned[0], served[0]);
        Assert.Equal(spawned[1], served[1]);
        Assert.True(BackupCatalog.ParseTables(served[0]).Count > 1000);
        for (var i = 2; i < commands.Length; i++)
        {
            var a = TestDataProvisioner.ParseRows(spawned[i]);
            var b = TestDataProvisioner.ParseRows(served[i]);
            Assert.True(a.Count > 0, $"{commands[i][3]} read no rows");
            Assert.Equal(a.Count, b.Count);
            for (var r = 0; r < a.Count; r++)
            {
                Assert.Equal(a[r].Keys.OrderBy(k => k, StringComparer.Ordinal), b[r].Keys.OrderBy(k => k, StringComparer.Ordinal));
                foreach (var k in a[r].Keys) Assert.Equal(a[r][k].GetRawText(), b[r][k].GetRawText());
            }
            _out.WriteLine($"{commands[i][3]}: {a.Count} rows identical");
        }
        _out.WriteLine($"serve: {serveTime.TotalMilliseconds:F0} ms; one process per command: {spawnTime.TotalMilliseconds:F0} ms");
    }

    private static string[] Read(string table, string symbols) => new[]
    {
        "read", Backup, "--table", table, "--company", "CRONUS International Ltd_",
        "--format", "json", "--merge-extensions", "--symbols", symbols,
    };
}
