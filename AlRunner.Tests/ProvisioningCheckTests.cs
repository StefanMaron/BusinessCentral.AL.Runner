// ProvisioningCheckTests — the engine-artifact completeness gate and its loud, detailed
// "how to fix" report (the runner's "no silent download" policy in action).

using Xunit;
using AlRunnerV2.Infrastructure;

namespace AlRunnerV2.Tests;

public sealed class ProvisioningCheckTests : IDisposable
{
    private readonly string _dir;

    public ProvisioningCheckTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "al-runner-prov", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    private void WriteCompleteClosure()
    {
        foreach (var f in new[]
        {
            "Microsoft.Dynamics.Nav.Ncl.dll",
            "Microsoft.Dynamics.Nav.Types.dll",
            "Microsoft.Dynamics.Nav.Common.dll",
            "Microsoft.Dynamics.Nav.Language.dll",
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
            "Microsoft.Identity.ServiceEssentials.Core.dll",
        }) Touch(f);
    }

    [Fact]
    public void Check_CompleteClosure_IsOk()
    {
        WriteCompleteClosure();
        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir);
        Assert.True(report.Ok);
        Assert.Empty(report.MissingFiles);
    }

    [Fact]
    public void Check_MissingEngineDll_IsReportedByName()
    {
        WriteCompleteClosure();
        File.Delete(Path.Combine(_dir, "Microsoft.Dynamics.Nav.Ncl.dll"));

        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir);
        Assert.False(report.Ok);
        Assert.Contains("Microsoft.Dynamics.Nav.Ncl.dll", report.MissingFiles);
        Assert.DoesNotContain("Microsoft.Dynamics.Nav.Types.dll", report.MissingFiles);
    }

    [Fact]
    public void Check_MissingClosureSentinel_IsReported()
    {
        WriteCompleteClosure();
        File.Delete(Path.Combine(_dir, "Microsoft.Identity.ServiceEssentials.Core.dll"));

        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir);
        Assert.False(report.Ok);
        Assert.Contains("Microsoft.Identity.ServiceEssentials.Core.dll", report.MissingFiles);
    }

    [Fact]
    public void Check_MissingDir_ReportsEverythingMissing()
    {
        var gone = Path.Combine(_dir, "does-not-exist");
        var report = ProvisioningCheck.Check("28.2.50931.52786", gone);
        Assert.False(report.Ok);
        // Names both core engine and the closure sentinel so the message is complete.
        Assert.Contains("Microsoft.Dynamics.Nav.Ncl.dll", report.MissingFiles);
        Assert.Contains("Microsoft.Identity.ServiceEssentials.Core.dll", report.MissingFiles);
    }

    [Fact]
    public void DetailedMessage_NamesPaths_ManualCommand_AndOneCommandFix()
    {
        var report = ProvisioningCheck.Check("28.2.50931.52786", _dir); // empty dir → all missing
        var msg = report.ToDetailedMessage("/some/project");

        // Every missing item's FULL path is named (human/agent can act).
        Assert.Contains(Path.Combine(_dir, "Microsoft.Dynamics.Nav.Ncl.dll"), msg);
        // The exact manual download command, with version and target dir.
        Assert.Contains("service-tier 28.2.50931.52786", msg);
        Assert.Contains(_dir, msg);
        // The one-command auto-resolve, targeting the project.
        Assert.Contains("al-runner provision", msg);
        Assert.Contains("/some/project", msg);
        Assert.Contains("--auto-provision", msg);
        // And it is explicit that the runner will NOT silently download.
        Assert.Contains("will not auto-download", msg);
    }
}
