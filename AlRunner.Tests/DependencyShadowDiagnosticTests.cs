// DependencyShadowDiagnosticTests — the runner must not pick a package that cannot
// execute, and must SAY so when it has no choice.
//
// Root cause being tested
// -----------------------
// A stray higher-versioned SYMBOLS-ONLY .app used to outrank the code-bearing copy of
// the same package, because resolution ranked version above executability. Compilation
// succeeded (symbols are all the compiler needs) and the run died much later inside BC
// with
//   NavNCLMissingMethodException: ... The object with ID 0 does not have a member ...
// which named neither the shadowed package nor the directory it came from.
//
// This file originally locked in a WARNING for that case, on the reading that resolution
// semantics were fixed and only the reporting could improve. That was wrong: the ranking
// itself was the defect, and it cost 17 al-language corpus tests on each of the BC 27.0
// and 27.3 legs (the corpus commits a symbols-only System Application v27.5.46862.48827
// that outranks the provisioned code-bearing app on those minors). Resolution now ranks
// executability above version among candidates that clear the declared minimum, so the
// shadowing case is prevented rather than reported.
//
// The warning survives for the one case ranking cannot fix: every code-bearing copy is
// BELOW the declared minimum, so the symbols-only winner is genuinely the correct answer
// and the run will still fail later. Measured 2026-07-29, on why naming both paths
// matters: an agent given only the exception hashed one .app, concluded shadowing was
// ruled out, and filed it as a missing-native-method runner gap. It was neither.
//
// Deliberately a WARNING, not a throw: symbols-only is CORRECT for Microsoft platform
// apps (Base/System Application, …), whose runtime can come from the service-tier DLLs.
// Erroring would break the working configuration.

using System.IO.Compression;
using System.Text;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class DependencyShadowDiagnosticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "al-runner-shadow-" + Guid.NewGuid().ToString("N"));

    public DependencyShadowDiagnosticTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly string ContosoId = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// The exact production shape: a higher-versioned symbols-only copy alongside a
    /// code-bearing one, both clearing the declared minimum.
    ///
    /// This used to assert that the symbols-only copy WON and that the resolver merely
    /// warned about it. Warning was the right call when the ranking was considered fixed,
    /// but the ranking was the bug: on the BC 27.0 and 27.3 matrix legs the corpus's own
    /// committed .alpackages/System Application.app (v27.5.46862.48827, symbols-only)
    /// outranked the provisioned code-bearing app and cost 17 corpus tests per leg. So the
    /// resolver now prefers the package that can execute, and this asserts the outcome
    /// directly rather than a diagnostic about a bad one.
    /// </summary>
    [Fact]
    public void CodeBearingWins_OverHigherSymbolsOnly_AndNeedsNoWarning()
    {
        WriteApp(_dir, "Lib_code_1.0.app", ContosoId, "Test Library", "Contoso", "1.0.0.0", carriesCode: true);
        WriteApp(_dir, "Lib_symbols_2.0.app", ContosoId, "Test Library", "Contoso", "2.0.0.0", carriesCode: false);

        var resolver = new DependencyResolver(new[] { _dir });
        var result = resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(ContosoId), "Test Library", "Contoso", new Version(1, 0, 0, 0)),
        });

        Assert.Single(result);
        Assert.Equal("Lib_code_1.0.app", Path.GetFileName(result[0].AppPath));

        // Nothing was shadowed, so there is nothing to report. A warning here would be noise
        // about a configuration the resolver just handled correctly.
        Assert.Empty(resolver.Diagnostics);
    }

    /// <summary>
    /// The case the warning still exists for, and the only one that can still end in
    /// object-ID-0: the winner is symbols-only because every code-bearing copy sits BELOW
    /// the declared minimum, so preferring executability could not reach it. Resolution is
    /// correct — minimums are not negotiable — but the run will fail later, so the resolver
    /// must name both the winner and the too-old code-bearing copy.
    /// </summary>
    [Fact]
    public void CodeBearingBelowMinimum_LeavesSymbolsOnlyWinner_AndIsReported()
    {
        WriteApp(_dir, "Lib_code_1.0.app", ContosoId, "Test Library", "Contoso", "1.0.0.0", carriesCode: true);
        WriteApp(_dir, "Lib_symbols_2.0.app", ContosoId, "Test Library", "Contoso", "2.0.0.0", carriesCode: false);

        var resolver = new DependencyResolver(new[] { _dir });
        var result = resolver.Resolve(new[]
        {
            // Minimum 2.0 excludes the code-bearing 1.0 outright.
            new DependencyRef(Guid.Parse(ContosoId), "Test Library", "Contoso", new Version(2, 0, 0, 0)),
        });

        Assert.Single(result);
        Assert.Equal("Lib_symbols_2.0.app", Path.GetFileName(result[0].AppPath));

        var diag = string.Join("\n", resolver.Diagnostics);
        Assert.Contains("Test Library", diag);
        Assert.Contains("Lib_symbols_2.0.app", diag);   // what won
        Assert.Contains("Lib_code_1.0.app", diag);      // what could not be used
        Assert.Contains("2.0.0.0", diag);
        Assert.Contains("1.0.0.0", diag);
    }

    /// <summary>
    /// Negative: when the winner carries code there is nothing wrong — staying quiet
    /// matters as much as warning, or the signal is worthless.
    /// </summary>
    [Fact]
    public void CodeBearingWinner_IsNotReported()
    {
        WriteApp(_dir, "Lib_code_1.0.app", ContosoId, "Test Library", "Contoso", "1.0.0.0", carriesCode: true);
        WriteApp(_dir, "Lib_code_2.0.app", ContosoId, "Test Library", "Contoso", "2.0.0.0", carriesCode: true);

        var resolver = new DependencyResolver(new[] { _dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(ContosoId), "Test Library", "Contoso", new Version(1, 0, 0, 0)),
        });

        Assert.Empty(resolver.Diagnostics);
    }

    /// <summary>
    /// Negative: every candidate symbols-only is the NORMAL case for a source-compiled
    /// dependency — nothing was shadowed, so nothing is wrong.
    /// </summary>
    [Fact]
    public void AllCandidatesSymbolsOnly_IsNotReported()
    {
        WriteApp(_dir, "Lib_symbols_1.0.app", ContosoId, "Test Library", "Contoso", "1.0.0.0", carriesCode: false);
        WriteApp(_dir, "Lib_symbols_2.0.app", ContosoId, "Test Library", "Contoso", "2.0.0.0", carriesCode: false);

        var resolver = new DependencyResolver(new[] { _dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(ContosoId), "Test Library", "Contoso", new Version(1, 0, 0, 0)),
        });

        Assert.Empty(resolver.Diagnostics);
    }

    /// <summary>
    /// Negative and important: Microsoft platform apps are symbols-only BY DESIGN —
    /// their runtime comes from the service-tier DLLs. Warning here would fire on
    /// every correct run and train readers to ignore the message.
    /// </summary>
    [Fact]
    public void MicrosoftPlatformApp_IsNotReported()
    {
        const string sysId = "22222222-2222-2222-2222-222222222222";
        WriteApp(_dir, "SysApp_code_28.1.app", sysId, "System Application", "Microsoft", "28.1.0.0", carriesCode: true);
        WriteApp(_dir, "SysApp_symbols_28.2.app", sysId, "System Application", "Microsoft", "28.2.0.0", carriesCode: false);

        var resolver = new DependencyResolver(new[] { _dir });
        resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(sysId), "System Application", "Microsoft", new Version(28, 0, 0, 0)),
        });

        Assert.Empty(resolver.Diagnostics);
    }

    private static void WriteApp(string dir, string fileName, string appId,
        string name, string publisher, string version, bool carriesCode)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var es = zip.CreateEntry("NavxManifest.xml").Open())
                es.Write(Encoding.UTF8.GetBytes(xml));
            // SymbolReference.json is present in BOTH kinds — it is what makes a
            // symbols-only package compile cleanly, which is the whole trap.
            using (var ss = zip.CreateEntry("SymbolReference.json").Open())
                ss.Write(Encoding.UTF8.GetBytes("{}"));
            if (carriesCode)
            {
                using var ds = zip.CreateEntry("publishedartifacts/" + name + ".dll").Open();
                ds.Write(new byte[] { 0x4D, 0x5A });
            }
        }
        var zipBytes = ms.ToArray();

        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, fileName), result);
    }
}
