// ClientCapabilityProbeTests — issue #2772.
//
// `IsAvailable()` on a [RunOnClient] DotNet variable raised
// NavNCLCallbackNotAllowedException ("Callback functions are not allowed") instead of
// answering false. AL emits that probe as
//     navDotNet.InvokeStaticPropertyGet<bool>("IsAvailable", methodIndex)
// whose first act is NavDotNet.CheckTypeIsLoaded() → Session.ClientCallback.
// CreateDotNetHandle(...), and `NavSession.ClientCallback` is
// `ClientCallbackOrNull ?? throw new NavNCLCallbackNotAllowedException()`. With no client
// attached the probe raised before it could ever produce a value, so page 9042 "Team Member
// Activities".OnOpenPage and page 189 "Incoming Document".OnOpenPage (via System App
// codeunit 1907 "Camera") failed instead of skipping their guarded block.
//
// WHAT THIS SUITE IS FOR
//   The BEHAVIOURAL claim — "real BC answers false rather than raising" — is a statement
//   about Business Central, so it is proven upstream against a live service tier in the
//   corpus repo (StefanMaron/BusinessCentral.AL.Language.Tests), per
//   .claude/rules/bc-behavior-tests-go-upstream.md. Since that PR has not merged and this
//   PR deliberately does not move the submodule pin, the runner-side guard lives here: a
//   MECHANISM suite pinning where the runner draws the line between a client-capability
//   PROBE (answered) and a client-capability USE (still refused, loudly).
//
//   Both halves matter. Answering `false` for everything a [RunOnClient] variable is asked
//   would pass a one-sided test while quietly making genuine client-side calls succeed —
//   exactly the silent fake .claude/rules/loud-failures.md forbids. So the AL fixture below
//   asserts the probe answers false AND that `Create()` and a NON-IsAvailable static
//   property get on the same kind of variable still raise "Callback functions are not
//   allowed".
//
// The fixture declares NO application floor (no-base-app-in-csharp-tests.md): the
// `dotnet` block resolves Microsoft.Dynamics.Nav.ClientExtensions off the service-tier
// probing path, which is independent of the app's dependency closure. Measured cold: ~3s.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class ClientCapabilityProbeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
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

    // PageNotifier and OfficeHost both derive from ClientExtension<T> in the service tier's
    // own Microsoft.Dynamics.Nav.ClientExtensions.dll, and both are declared [RunOnClient]
    // by the shipped Base Application (pages 9060/9062/9068 and the Office host pages), so
    // the assembly and both type names resolve on every supported BC version. No Version /
    // Culture / PublicKeyToken is pinned in the `assembly(...)` block precisely so the
    // fixture is not tied to one BC major.
    private const string Fixture = """
    dotnet
    {
        assembly("Microsoft.Dynamics.Nav.ClientExtensions")
        {
            type("Microsoft.Dynamics.Nav.Client.PageNotifier"; "PageNotifier") { }
            type("Microsoft.Dynamics.Nav.Client.Hosts.OfficeHost"; "OfficeHost") { }
        }
    }

    codeunit 62770 "Client Capability Probe 2772"
    {
        Subtype = Test;

        var
            [RunOnClient]
            PageNotifier: DotNet PageNotifier;
            [RunOnClient]
            OfficeHost: DotNet OfficeHost;

        // The #2772 regression itself: this raised NavNCLCallbackNotAllowedException.
        [Test]
        procedure Probe_IsAvailable_AnswersFalseInsteadOfRaising()
        begin
            if PageNotifier.IsAvailable() then
                Error('PageNotifier.IsAvailable() answered TRUE; expected FALSE.');
        end;

        // Same answer for a second, unrelated client-extension type, so the fix is not
        // pinned to one type name.
        [Test]
        procedure Probe_IsAvailable_AnswersFalseForASecondClientType()
        begin
            if OfficeHost.IsAvailable() then
                Error('OfficeHost.IsAvailable() answered TRUE; expected FALSE.');
        end;

        // The other direction: a genuine client-side USE past the guard must still refuse.
        // A fix that answered every [RunOnClient] member with a default would pass the two
        // tests above and fail this one.
        [Test]
        procedure Use_StaticCreate_StillRefusesLoudly()
        begin
            asserterror PageNotifier := PageNotifier.Create();
            if StrPos(GetLastErrorText(), 'Callback functions are not allowed') = 0 then
                Error('PageNotifier.Create() raised the wrong error: %1', GetLastErrorText());
        end;

        // And the guard is scoped to the availability probe by NAME, not to "any static
        // property get on a client type": OfficeHost.HostName is a static string property
        // on a [RunOnClient] type and must still refuse.
        [Test]
        procedure Use_OtherStaticProperty_StillRefusesLoudly()
        var
            HostName: Text;
        begin
            asserterror HostName := OfficeHost.HostName;
            if StrPos(GetLastErrorText(), 'Callback functions are not allowed') = 0 then
                Error('OfficeHost.HostName raised the wrong error: %1', GetLastErrorText());
        end;
    }
    """;

    private static string WriteBundle()
    {
        var root = TestScratch.Dir("al-runner-client-capability-probe-2772");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2772-4a1b-9c3d-000000002772",
          "name": "Client Capability Probe 2772",
          "publisher": "Repro2772",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62770, "to": 62779 } ],
          "runtime": "14.0",
          "target": "OnPrem"
        }
        """);
        File.WriteAllText(Path.Combine(root, "probe.al"), Fixture);
        return root;
    }

    [SkippableFact]
    public void IsAvailableProbeAnswersFalse_WhileGenuineClientUseStillRefuses()
    {
        TestArtifacts.SkipIfMissing();

        var (output, _) = RunRunner(WriteBundle());

        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // Named individually so a failure log says WHICH half broke rather than only "3P/1F".
        Assert.Contains("PASS  Codeunit62770.Probe_IsAvailable_AnswersFalseInsteadOfRaising", output);
        Assert.Contains("PASS  Codeunit62770.Probe_IsAvailable_AnswersFalseForASecondClientType", output);
        Assert.Contains("PASS  Codeunit62770.Use_StaticCreate_StillRefusesLoudly", output);
        Assert.Contains("PASS  Codeunit62770.Use_OtherStaticProperty_StillRefusesLoudly", output);
        Assert.Contains("4P/0F/0E", output);
    }

    // ── The predicate the Cecil prologue calls, pinned directly ────────────────────────
    // Cheap (no subprocess) and states the scoping as a truth table: every "false" row is a
    // call that must still run BC's own body unchanged.

    [Fact]
    public void Predicate_AnswersOnly_TheBooleanIsAvailableProbe_OnAClientVariable()
    {
        // The one case that is answered.
        Assert.True(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, "IsAvailable", typeof(bool)));

        // A SERVER-side DotNet variable over the very same type is a different mechanism
        // (CreateNavServerHandle against the real assembly) and is deliberately untouched.
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: false, "IsAvailable", typeof(bool)));

        // Any other member on a client variable stays on BC's path — and therefore still
        // raises "Callback functions are not allowed".
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, "HostName", typeof(bool)));
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, null, typeof(bool)));

        // Ordinal, case-sensitive: AL emits the member name exactly as the .NET property is
        // spelled, so a differently-cased name is a DIFFERENT member, not this probe.
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, "isavailable", typeof(bool)));

        // Non-boolean instantiations must never be answered: the Cecil prologue's
        // `unbox.any !!T` of a boxed `false` is only well-typed when T is bool.
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, "IsAvailable", typeof(string)));
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, "IsAvailable", typeof(int)));
        Assert.False(AlRunner.Patches.NavDotNetPatches.IsUnavailableClientCapabilityProbe(
            runOnClient: true, "IsAvailable", null));
    }
}
