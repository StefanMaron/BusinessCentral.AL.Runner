// ApplicationAreaControlRemovalTests — issue #4750.
//
// RUNNER-MECHANISM test: pins ApplicationAreaControlRemoval (the Cecil-redirected removal
// pass in MetadataProvider.GetMasterPage) and LiveNavTestPage.GetField's use of it, end to end
// through a real bundle run. The BC-behaviour claim itself is measured upstream, by corpus
// codeunit 67530 (pageapplicationarea/TestPageApplicationAreaControlRemoval.al).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ApplicationAreaControlRemovalTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public ApplicationAreaControlRemovalTests()
    {
        _root = TestScratch.Dir("al-runner-app-area-control-removal");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" \"--package-cache\" \"{platformApps}\"");
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private void WriteFixture()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "0b4750a1-4750-4750-4750-475047504750",
          "name": "App Area Control Removal Repro",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 90750, "to": 90759 } ],
          "runtime": "17.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Fixture.al"), """
        table 90750 "AACR Record"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Basic Value"; Text[30]) { }
                field(3; "Service Value"; Text[30]) { }
            }
            keys
            {
                key(PK; "Code") { Clustered = true; }
            }
        }

        page 90750 "AACR Card"
        {
            PageType = Card;
            SourceTable = "AACR Record";

            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field(BasicCtl; Rec."Basic Value")
                        {
                            ApplicationArea = Basic;
                        }
                        field(ServiceCtl; Rec."Service Value")
                        {
                            ApplicationArea = Service;
                        }
                    }
                }
            }
        }

        codeunit 90751 "AACR Test"
        {
            Subtype = Test;

            local procedure MakeRecord(var R: Record "AACR Record")
            begin
                R.DeleteAll();
                R.Init();
                R."Code" := 'A';
                R.Insert();
            end;

            local procedure WriteService(Areas: Text): Text
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
                ReadBack: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea(Areas);
                P.OpenEdit();
                P.GoToRecord(R);
                P.ServiceCtl.SetValue('S');
                ReadBack := P.ServiceCtl.Value();
                P.Close();
                ApplicationArea(Prev);
                exit(ReadBack);
            end;

            [Test]
            procedure AreaNotEnabled_ServiceControlIsNotFound()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenEdit();
                P.GoToRecord(R);
                asserterror P.ServiceCtl.SetValue('S');
                ApplicationArea(Prev);
                if StrPos(GetLastErrorText(), 'is not found on the page.') = 0 then
                    Error('expected the #Service control to be not found, got: %1', GetLastErrorText());
            end;

            [Test]
            procedure AreaNotEnabled_BasicControlIsStillFound()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenEdit();
                P.GoToRecord(R);
                P.BasicCtl.SetValue('B');
                P.Close();
                ApplicationArea(Prev);
                R.Get('A');
                if R."Basic Value" <> 'B' then
                    Error('the #Basic control must write its field, got %1', R."Basic Value");
            end;

            [Test]
            procedure AreaEnabled_ServiceControlIsFound()
            begin
                if WriteService('#Basic,#Service') <> 'S' then
                    Error('with #Service enabled the control must read back S');
            end;

            [Test]
            procedure EmptyAreaString_ServiceControlIsFound()
            begin
                if WriteService('') <> 'S' then
                    Error('with every area enabled the control must read back S');
            end;
        }
        """);
    }

    [SkippableFact]
    public void TestPage_ControlWhoseAreaIsNotEnabled_IsNotFound_OthersAre()
    {
        WriteFixture();
        var (output, exit) = RunRunner(_root);
        TestArtifacts.SkipIf(output.Contains("no BC artifact") || output.Contains("[bc] no engines"),
            "no BC engine artifact provisioned in this environment");

        Assert.True(exit == 0, $"expected all four AL tests to pass; exit={exit}\n{output}");
        Assert.Contains("   passed 4 ", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
