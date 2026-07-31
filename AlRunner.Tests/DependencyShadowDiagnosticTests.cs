// DependencyShadowDiagnosticTests — the runner must SAY when the package it picked
// cannot possibly execute.
//
// Root cause being tested
// -----------------------
// Resolution picks the highest version of each package across every scanned dir.
// A stray higher-versioned SYMBOLS-ONLY .app therefore outranks the code-bearing
// copy of the same package. Compilation succeeds (symbols are all the compiler
// needs) and the run dies much later inside BC with
//   NavNCLMissingMethodException: ... The object with ID 0 does not have a member ...
// which names neither the shadowed package nor the directory it came from.
//
// The resolver already holds every candidate and can tell code-bearing from
// symbols-only, so it can state the problem at the moment it happens instead of
// leaving a human to reconstruct it from a hundred .app files. Measured 2026-07-29:
// an agent given only the exception hashed one .app, concluded shadowing was ruled
// out, and filed it as a missing-native-method runner gap. It was neither.
//
// Deliberately a WARNING, not a throw: symbols-only is CORRECT for Microsoft
// platform apps (Base/System Application, …), whose runtime comes from the
// service-tier DLLs. Erroring would break the working configuration.

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
    /// The exact production shape: a higher-versioned symbols-only copy beats the
    /// code-bearing one. The resolver must emit a diagnostic naming BOTH paths.
    /// </summary>
    [Fact]
    public void SymbolsOnlyWinnerOverCodeBearingLoser_IsReported()
    {
        WriteApp(_dir, "Lib_code_1.0.app", ContosoId, "Test Library", "Contoso", "1.0.0.0", carriesCode: true);
        WriteApp(_dir, "Lib_symbols_2.0.app", ContosoId, "Test Library", "Contoso", "2.0.0.0", carriesCode: false);

        var resolver = new DependencyResolver(new[] { _dir });
        var result = resolver.Resolve(new[]
        {
            new DependencyRef(Guid.Parse(ContosoId), "Test Library", "Contoso", new Version(1, 0, 0, 0)),
        });

        // The winner is still the highest version — resolution semantics are unchanged.
        Assert.Single(result);
        Assert.Equal("Lib_symbols_2.0.app", Path.GetFileName(result[0].AppPath));

        var diag = string.Join("\n", resolver.Diagnostics);
        Assert.Contains("Test Library", diag);
        Assert.Contains("Lib_symbols_2.0.app", diag);   // what won
        Assert.Contains("Lib_code_1.0.app", diag);      // what it shadowed
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
