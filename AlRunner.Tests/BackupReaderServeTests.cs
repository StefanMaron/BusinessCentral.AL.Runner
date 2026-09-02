// BackupReaderServeTests — the two pure halves of the serve transport (issue 2263), pinned
// without a 900 MB backup.
//
// The claim the transport rests on is EQUIVALENCE: a table read over the serve process must
// hand TestDataProvisioner.ParseRows exactly what the one-process-per-command CLI hands it.
// If the two shapes ever diverge, --test-data hydrates different values depending on which
// reader happened to be on PATH, and nothing in a green run would say so. So the central test
// here builds the same table both ways and asserts ParseRows agrees, key for key and value
// for value.
//
// The other half is the request: a `read` argument vector has to become the request the
// reader actually answers, with `merge-extensions` HYPHENATED (the camelCase spelling was
// accepted, ignored and exited 0 on older reader builds — see TestDataProvisioner's header),
// and anything this slice does not model has to decline rather than guess.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BackupReaderServeTests
{
    private static readonly string[] HydrateArgs =
    {
        "read", "/backups/BusinessCentral-W1.bak",
        "--table", "Payment Terms",
        "--company", "CRONUS International Ltd_",
        "--format", "json",
        "--merge-extensions",
        "--symbols", "/apps/Base.app,/apps/System.app",
    };

    // ── request translation ────────────────────────────────────────────────────

    [Fact]
    public void ReadRequest_CarriesTableCompanyAndTheHyphenatedMergeKey()
    {
        Assert.True(BackupReaderServe.TryBuildReadRequest(HydrateArgs, 7, out var request));
        Assert.NotNull(request);
        Assert.Equal("/backups/BusinessCentral-W1.bak", request!.Backup);
        Assert.Equal("/apps/Base.app,/apps/System.app", request.Symbols);

        using var doc = JsonDocument.Parse(request.Json);
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.Equal("read", root.GetProperty("cmd").GetString());
        Assert.Equal("Payment Terms", root.GetProperty("table").GetString());
        Assert.Equal("CRONUS International Ltd_", root.GetProperty("company").GetString());
        Assert.True(root.GetProperty("merge-extensions").GetBoolean());

        // The transport-level options are NOT request keys: a key the command does not accept
        // fails the request outright on current reader builds.
        Assert.False(root.TryGetProperty("mergeExtensions", out _));
        Assert.False(root.TryGetProperty("format", out _));
        Assert.False(root.TryGetProperty("symbols", out _));
    }

    [Fact]
    public void ReadRequest_OmitsTheMergeKeyWhenTheFlagIsAbsent()
    {
        var plain = HydrateArgs.Where(a => a != "--merge-extensions").ToArray();
        Assert.True(BackupReaderServe.TryBuildReadRequest(plain, 1, out var request));
        using var doc = JsonDocument.Parse(request!.Json);
        Assert.False(doc.RootElement.TryGetProperty("merge-extensions", out _));
    }

    [Fact]
    public void ReadRequest_CarriesTop()
    {
        var withTop = HydrateArgs.Concat(new[] { "--top", "1" }).ToArray();
        Assert.True(BackupReaderServe.TryBuildReadRequest(withTop, 1, out var request));
        using var doc = JsonDocument.Parse(request!.Json);
        Assert.Equal(1, doc.RootElement.GetProperty("top").GetInt32());
    }

    [Theory]
    // A different command entirely.
    [InlineData("tables", "/backups/x.bak", "--symbols", "/apps/Base.app")]
    // A format the CLI shape is not JSON for.
    [InlineData("read", "/backups/x.bak", "--table", "T", "--format", "tsv")]
    // An option this slice does not model — decline rather than send a request that means
    // something else.
    [InlineData("read", "/backups/x.bak", "--table", "T", "--unknown-option", "v")]
    // A --top that is not a number.
    [InlineData("read", "/backups/x.bak", "--table", "T", "--top", "many")]
    // No table at all.
    [InlineData("read", "/backups/x.bak", "--company", "C")]
    public void ReadRequest_DeclinesWhatItCannotExpress(params string[] args)
    {
        Assert.False(BackupReaderServe.TryBuildReadRequest(args, 1, out var request));
        Assert.Null(request);
    }

    // ── response translation, and equivalence with the CLI shape ───────────────

    [Fact]
    public void ServeAndCliShapesProjectToTheSameRows()
    {
        // The same two rows in both wire shapes, including a null, a number, a string with a
        // control character (the reader escapes U+0002 in date formulas) and a system column
        // ParseRows is required to drop.
        const string serve = """
            {"id":3,"ok":true,
             "headers":["Code","Description","Discount _","Blocked","Due Date Calculation","$systemId"],
             "rows":[["10 DAYS","Net 10 days",0.5,null,"10\u0002","BF49D1DB-D953-F111-8E26-7CED8D9E4094"],
                     ["14 DAYS","Net 14 days",0.0,1,"14\u0002","C049D1DB-D953-F111-8E26-7CED8D9E4094"]]}
            """;
        const string cli = """
            [
              {"Code":"10 DAYS","Description":"Net 10 days","Discount _":0.5,"Blocked":null,"Due Date Calculation":"10\u0002","$systemId":"BF49D1DB-D953-F111-8E26-7CED8D9E4094"},
              {"Code":"14 DAYS","Description":"Net 14 days","Discount _":0.0,"Blocked":1,"Due Date Calculation":"14\u0002","$systemId":"C049D1DB-D953-F111-8E26-7CED8D9E4094"}
            ]
            """;

        var fromServe = TestDataProvisioner.ParseRows(
            BackupReaderServe.TranslateReadResponse(serve, "read Payment Terms"));
        var fromCli = TestDataProvisioner.ParseRows(cli);

        Assert.Equal(2, fromServe.Count);
        Assert.Equal(fromCli.Count, fromServe.Count);
        for (var i = 0; i < fromCli.Count; i++)
        {
            Assert.Equal(
                fromCli[i].Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                fromServe[i].Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            foreach (var key in fromCli[i].Keys)
                Assert.Equal(fromCli[i][key].GetRawText(), fromServe[i][key].GetRawText());
        }

        // Concrete values, so this cannot pass against an empty projection.
        Assert.Equal("10 DAYS", fromServe[0]["Code"].GetString());
        Assert.Equal(0.5, fromServe[0]["Discount _"].GetDouble());
        Assert.Equal(JsonValueKind.Null, fromServe[0]["Blocked"].ValueKind);
        Assert.Equal("10\u0002", fromServe[0]["Due Date Calculation"].GetString());
        Assert.Equal(1, fromServe[1]["Blocked"].GetInt32());
        // The system column is dropped by ParseRows on both paths.
        Assert.False(fromServe[0].ContainsKey("$systemId"));
    }

    [Fact]
    public void EmptyTableTranslatesToAnEmptyArray()
    {
        var text = BackupReaderServe.TranslateReadResponse(
            """{"id":1,"ok":true,"headers":["Code"],"rows":[]}""", "read T");
        Assert.Empty(TestDataProvisioner.ParseRows(text));
    }

    [Fact]
    public void ARefusalRaisesTheReadersOwnText()
    {
        var ex = Assert.Throws<BackupReaderException>(() => BackupReaderServe.TranslateReadResponse(
            """{"id":2,"ok":false,"error":"no table matches 'No Such Table'"}""",
            "read No Such Table"));
        Assert.Contains("no table matches 'No Such Table'", ex.Message);
        Assert.Contains("read No Such Table", ex.Message);
    }

    [Fact]
    public void AnAnswerWithoutHeadersIsRefusedRatherThanReadAsEmpty()
    {
        // The dangerous failure is an empty result presented as an answer: the table would
        // hydrate to nothing and the run would report success.
        var ex = Assert.Throws<BackupReaderException>(
            () => BackupReaderServe.TranslateReadResponse("""{"id":1,"ok":true}""", "read T"));
        Assert.Contains("headers", ex.Message);
    }

    [Fact]
    public void ARowLongerThanTheHeaderListIsRefused()
    {
        var ex = Assert.Throws<BackupReaderException>(() => BackupReaderServe.TranslateReadResponse(
            """{"id":1,"ok":true,"headers":["Code"],"rows":[["A","B"]]}""", "read T"));
        Assert.Contains("more cells than headers", ex.Message);
    }

    [Fact]
    public void UnparseableOutputIsRefusedRatherThanReadAsEmpty()
    {
        var ex = Assert.Throws<BackupReaderException>(
            () => BackupReaderServe.TranslateReadResponse("not json at all", "read T"));
        Assert.Contains("could not be parsed", ex.Message);
    }

    // ── the kill switch ────────────────────────────────────────────────────────

    [Fact]
    public void ServeCanBeTurnedOffByEnvironment_AndTheAnswerIsReadOnce()
    {
        var saved = Environment.GetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE");
        try
        {
            BackupReaderServe.ResetForTests();
            Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", "0");
            Assert.False(BackupReaderServe.EnabledByEnv);
            // TryRun must decline outright, without touching the reader executable.
            Assert.False(BackupReaderServe.TryRun(HydrateArgs, out var output));
            Assert.Equal("", output);

            // Memoised: flipping it afterwards is not observed.
            Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", "1");
            Assert.False(BackupReaderServe.EnabledByEnv);

            BackupReaderServe.ResetForTests();
            Assert.True(BackupReaderServe.EnabledByEnv);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AL_RUNNER_BCBAK_SERVE", saved);
            BackupReaderServe.ResetForTests();
        }
    }
}
