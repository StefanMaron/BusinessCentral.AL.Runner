// CodeCoverageSiblingLayout — the #5222 / #5250 / #5259 / #5260 layout the Code Coverage
// (2000000049) row tests share: bundle/ and src/ carry ONE app id with different text, and a tests
// app depends on that app id and reads Record "Code Coverage" after CodeCoverageLog. Which folder's
// code runs differs by invocation, so the tests app says which results it accepts: Reached(20)
// returns 21 from bundle/ and 120 from src/, and the rows it then expects are that folder's own.

using System.Text.Json;

namespace AlRunner.Tests;

internal static class CodeCoverageSiblingLayout
{
    internal static readonly Guid AppId = Guid.Parse("6c779974-6ae1-42b9-9842-ca25979bae35");
    internal static readonly Guid TestAppId = Guid.Parse("3f1c8d52-7b04-4e9a-a6d3-9e2b5c7a1d52");
    internal static readonly Guid SecondTestAppId = Guid.Parse("8a2e4b16-5c93-4d07-b1f8-6e0d3a9c7b41");

    // The original source: Multi A is short, so Multi B starts early in the file. Reached returns
    // X + 100, which is how a test tells which folder's code ran.
    internal const string SourceText = """
        codeunit 79800 "Multi A"
        {
            procedure Never(X: Integer): Integer
            begin
                exit(X + 7);
            end;
        }

        codeunit 79801 "Multi B"
        {
            procedure Reached(X: Integer): Integer
            begin
                if X > 10 then
                    exit(X + 100);
                exit(X);
            end;

            procedure Unreached(X: Integer): Integer
            begin
                exit(X * 3);
            end;
        }
        """;

    // What bundle/ compiles: Multi A is longer, so every line of Multi B sits further down than it
    // does in SourceText, and the file is longer than src/'s.
    internal const string BundleText = """
        codeunit 79800 "Multi A"
        {
            procedure Never(X: Integer): Integer
            var
                Y: Integer;
            begin
                Y := Y + 1;
                Y := Y + 2;
                Y := Y + 3;
                Y := Y + 4;
                Y := Y + 5;
                Y := Y + 6;
                Y := Y + 7;
                Y := Y + 8;
                exit(X + 7);
            end;
        }

        codeunit 79801 "Multi B"
        {
            procedure Reached(X: Integer): Integer
            var
                Z: Integer;
            begin
                Z := 1;
                if X > 10 then
                    exit(X + 1);
                exit(X);
            end;

            procedure Unreached(X: Integer): Integer
            begin
                exit(X * 3);
            end;
        }
        """;

    internal const string BundleRows = "[Z := 1;:1][if X > 10 then:1][exit(X + 1);:1][exit(X);:0][exit(X * 3);:0]";
    internal const string SourceRows = "[if X > 10 then:1][exit(X + 100);:1][exit(X);:0][exit(X * 3);:0]";

    /// <summary>A test codeunit that calls Reached(20), reads the Code Coverage code rows of Multi B
    /// and fails unless they are exactly the rows of the folder whose code returned that result.
    /// <paramref name="fromBundle"/> and <paramref name="fromSource"/> say which results it accepts;
    /// any other result, and any rows other than the accepted folder's own, fail.</summary>
    internal static string TestsText(int codeunitId, bool fromBundle, bool fromSource)
    {
        var arms = new System.Text.StringBuilder();
        if (fromBundle) arms.AppendLine($"            21: Expected := '{BundleRows}';");
        if (fromSource) arms.AppendLine($"            120: Expected := '{SourceRows}';");
        return $$"""
            codeunit {{codeunitId}} "Multi Tests {{codeunitId}}"
            {
                Subtype = Test;

                [Test]
                procedure CodeCoverageRowsFollowTheRunText()
                var
                    B: Codeunit "Multi B";
                    CC: Record "Code Coverage";
                    Seen: Text;
                    Expected: Text;
                    Result: Integer;
                begin
                    CodeCoverageLog(true, false);
                    Result := B.Reached(20);
                    CodeCoverageLog(false, false);
                    CC.SetRange("Object Type", CC."Object Type"::Codeunit);
                    CC.SetRange("Object ID", 79801);
                    CC.SetRange("Line Type", CC."Line Type"::Code);
                    if CC.FindSet() then
                        repeat
                            Seen += StrSubstNo('[%1:%2]', DelChr(CC.Line, '<', ' '), CC."No. of Hits");
                        until CC.Next() = 0;
                    case Result of
            {{arms}}            else
                            Error('Reached(20) returned %1, not the result of a folder this test accepts', Result);
                    end;
                    if Seen <> Expected then
                        Error('Code Coverage rows were %1, expected %2', Seen, Expected);
                end;
            }
            """;
    }

    internal static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    internal static void WriteManifest(string dir, Guid id, string name, bool dependsOnApp, bool platform = false)
    {
        Directory.CreateDirectory(dir);
        var manifest = new Dictionary<string, object>
        {
            ["id"] = id, ["name"] = name, ["publisher"] = "AL Runner", ["version"] = "1.0.0.0",
            ["runtime"] = "14.0",
            ["idRanges"] = new[] { new { from = 79800, to = 79830 } },
            ["dependencies"] = dependsOnApp
                ? new[] { new { id = AppId, name = "Multi", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        };
        // Record "Code Coverage" is a system table, so the test app needs the platform symbols.
        if (platform) manifest["platform"] = "27.0.0.0";
        File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(manifest));
    }

    /// <summary>bundle/ (BundleText), optionally src/ (SourceText, same app id), and a tests app
    /// <paramref name="testsDir"/> accepting the given results.</summary>
    internal static void Write(string root, bool withSibling, string testsDir = "tests", int testsCodeunit = 79811,
        Guid? testsAppId = null, bool fromBundle = true, bool fromSource = false)
    {
        WriteManifest(Path.Combine(root, "bundle"), AppId, "Multi", dependsOnApp: false);
        Write(Path.Combine(root, "bundle", "MultiPair.Codeunit.al"), BundleText);
        if (withSibling)
        {
            WriteManifest(Path.Combine(root, "src"), AppId, "Multi", dependsOnApp: false);
            Write(Path.Combine(root, "src", "MultiPair.Codeunit.al"), SourceText);
        }
        WriteManifest(Path.Combine(root, testsDir), testsAppId ?? TestAppId, "Multi Tests " + testsCodeunit,
            dependsOnApp: true, platform: true);
        Write(Path.Combine(root, testsDir, "src", "MultiTests.Codeunit.al"),
            TestsText(testsCodeunit, fromBundle, fromSource));
    }
}
