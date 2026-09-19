// XrmProxyRegistration — register the Xrm proxy assemblies Microsoft ships, the way BC's own
// service tier does, so BaseApp's CRM/CDS surfaces see a populated proxy-version registry.
//
// Observably equivalent (loud-failures.md): this calls Microsoft's own
// XrmServiceProvider.RegisterXrmService with a path to Microsoft's own proxy assembly, which is
// exactly what BC's NavEnvironment ctor does via
// ExternalDataServiceManager.InitializeExternalDataServiceProviders("Xrm", ...). Nothing here
// re-implements a BC component or invents a value: the id comes from the shipped
// Microsoft.Xrm.Sdk.dll's own file version, and the assembly comes off disk. The registry is the
// same static dictionary either way, so every reader — CrmHelper.GetProxyIdList(),
// XrmServiceProvider.GetService — sees what it would see on a service tier.
//
// Citation: the chain is Page5330.InitializeDefaultProxyVersion -> Codeunit5330.GetLastProxyVersionItem
// -> InitializeProxyVersionList -> DotNet CrmHelper.GetProxyIdList() -> XrmServiceProvider.ProxyIds,
// measured on 28.1.49838.53910 (issue #3515). Proven by tests/runner-extras/crm-proxy-version.
//
// Trap: BC asserts id == SdkVersion.Major when a second registration arrives for one id
// (XrmServiceProvider.RegisterXrmService, the `value.SdkVersion.Major != version` branch), so the
// id is NOT the V-number in the file name. Measured on 28.1.49838.53910: V100 reports SdkVersion
// 9.2.49.6443, so its id is 9, not 100; V91 cannot report one at all, because its getter reads
// Host/Microsoft.Xrm.Sdk.dll, a Windows out-of-process host that does not ship in the artifact
// directory. See docs/limitations.md#crm-proxy-versions.
using System.Diagnostics;
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

internal static class XrmProxyRegistration
{
    // The proxy assemblies BC ships, newest first. Each is registered only if it is present AND
    // its SDK major can be read, so a BC build that ships a different set registers what it has
    // rather than what this list expects.
    private static readonly string[] ProxyAssemblies =
    {
        "Microsoft.Dynamics.Nav.Xrm.V100.dll",
        "Microsoft.Dynamics.Nav.Xrm.V91.dll",
    };

    /// <summary>
    /// Registers every shipped Xrm proxy assembly with BC's own <c>XrmServiceProvider</c>, so
    /// <c>CrmHelper.GetProxyIdList()</c> answers as it does on a service tier. Returns the number
    /// of proxies registered; zero means the registry is empty, which is never a faithful state.
    /// </summary>
    /// <remarks>
    /// BC's own loop swallows a missing proxy assembly and carries on
    /// (<c>ExternalDataServiceManager.InitializeExternalDataServiceProviders</c> catches
    /// <see cref="FileNotFoundException"/> per item), so a partial set is a faithful outcome and
    /// not a failure. What is NOT faithful is an empty registry where BC would have a populated
    /// one, which is why the caller reports a total of zero.
    /// </remarks>
    public static int Apply()
    {
        // NOT Path.GetDirectoryName(navNcl.Location): the loaded Ncl.dll is the Cecil-rewritten
        // copy in the ncl-shadow cache, whose directory holds none of the sibling BC assemblies.
        // ServiceTierDir is the artifact directory this process actually selected.
        if (!BcArtifacts.IsSelected) return 0;
        var artifactDir = BcArtifacts.ServiceTierDir;
        if (string.IsNullOrEmpty(artifactDir)) return 0;

        var xrmPath = Path.Combine(artifactDir, "Microsoft.Dynamics.Nav.Xrm.dll");
        if (!File.Exists(xrmPath)) return 0;

        Type? providerType;
        try
        {
            providerType = Assembly.LoadFrom(xrmPath)
                .GetType("Microsoft.Dynamics.Nav.Xrm.XrmServiceProvider");
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            return 0;
        }

        var register = providerType?.GetMethod(
            "RegisterXrmService",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(int), typeof(string) },
            modifiers: null);
        if (register == null) return 0;

        var registered = 0;
        foreach (var assemblyName in ProxyAssemblies)
        {
            var proxyPath = Path.Combine(artifactDir, assemblyName);
            if (!File.Exists(proxyPath)) continue;

            var version = ReadProxySdkMajor(proxyPath);
            if (version is not > 0) continue;

            try
            {
                register.Invoke(null, new object[] { version.Value, proxyPath });
                registered++;
            }
            catch (TargetInvocationException)
            {
                // BC's own registration guards: a missing file, or an id already registered
                // against a different SDK major. Both are states BC itself tolerates by logging
                // and moving to the next proxy, so this does too.
            }
        }

        return registered;
    }

    /// <summary>
    /// The id a proxy assembly must be registered under: the major of the CRM SDK it binds to.
    /// </summary>
    /// <remarks>
    /// BC derives this at runtime through <c>XrmService.SdkVersion</c>, which reads the file
    /// version of the <c>Microsoft.Xrm.Sdk.dll</c> that proxy resolves against — and BC enforces
    /// <c>SdkVersion.Major == version</c> on re-registration. Reading the same file, from the same
    /// place that proxy reads it, keeps the two definitions the same one.
    ///
    /// Each proxy resolves its SDK from its own location, so this must not probe a fallback: V91
    /// reads <c>Host/Microsoft.Xrm.Sdk.dll</c> and V100 reads the copy beside itself. Falling back
    /// to the artifact root for V91 would hand it V100's SDK major and register it under an id
    /// whose <c>SdkVersion</c> it cannot itself report. A proxy whose own SDK is absent answers
    /// null and is skipped, which is also what it is on this platform — V91's Host is a Windows
    /// out-of-process host that does not ship here (<c>guards-need-a-third-state.md</c>: "could
    /// not tell" must not be spelled as the success state).
    /// </remarks>
    private static int? ReadProxySdkMajor(string proxyPath)
    {
        var sdkPath = SdkPathFor(proxyPath);
        if (!File.Exists(sdkPath)) return null;

        var fileVersion = FileVersionInfo.GetVersionInfo(sdkPath).FileVersion;
        if (string.IsNullOrEmpty(fileVersion)) return null;
        if (!Version.TryParse(fileVersion, out var parsed)) return null;
        if (parsed.Major <= 0) return null;

        return parsed.Major;
    }

    /// <summary>
    /// Where a given proxy assembly resolves <c>Microsoft.Xrm.Sdk.dll</c> from, mirroring that
    /// proxy's own <c>SdkVersion</c> getter.
    /// </summary>
    private static string SdkPathFor(string proxyPath)
    {
        var dir = Path.GetDirectoryName(proxyPath)!;
        return Path.GetFileName(proxyPath).Contains(".V91.", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(dir, "Host", "Microsoft.Xrm.Sdk.dll")
            : Path.Combine(dir, "Microsoft.Xrm.Sdk.dll");
    }
}
