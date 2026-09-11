namespace AlRunner.Infrastructure;

/// <summary>
/// The files that make a BC artifact directory a usable service tier, and the pure
/// filesystem check over them.
///
/// <para><b>Why this is its own file (#3893).</b> The list used to be private state inside
/// <see cref="ProvisioningCheck"/>, a 1,997-line type whose wider closure reaches
/// <c>AppLoader</c>, <c>SafeDirectoryScan</c> and <c>BcArtifacts</c>. That made the check
/// unreachable from anything that is not the runner — and the entry points #3893 reports are
/// exactly those: <c>tools/metadata-ground-truth</c> is deliberately outside
/// <c>AlRunner.slnx</c> (a reference would put a runner build on every invocation), so it had
/// no way to ask "is this directory usable" and opened it regardless, failing deep inside an
/// assembly load.</para>
///
/// <para>Self-contained on purpose: no project reference, no AL types, nothing but
/// <c>System.IO</c>. That is what lets a standalone tool link this ONE file
/// (<c>&lt;Compile Include="…/EngineClosure.cs" /&gt;</c>) and share the runner's definition
/// of "complete" rather than re-deriving it — which is how <c>tools/preflight.py</c> came to
/// carry a Python re-implementation of the same list.</para>
/// </summary>
public static class EngineClosure
{
    /// <summary>
    /// The engine DLLs the runner binds directly: the ALC resolver and the Cecil rewrite
    /// both load them out of the artifact directory.
    /// </summary>
    public static readonly string[] CoreEngineDlls =
    {
        "Microsoft.Dynamics.Nav.Ncl.dll",
        "Microsoft.Dynamics.Nav.Types.dll",
        "Microsoft.Dynamics.Nav.Common.dll",
        "Microsoft.Dynamics.Nav.Language.dll",
        "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
    };

    /// <summary>
    /// Sentinel of the BC-app external closure the version-agnostic engine relies on being
    /// served from the artifact directory — the exact DLL whose absence produced
    /// FileLoadException 0x80131621. Its presence signals the full /service/ closure landed.
    ///
    /// <para>It is what separates the two broken shapes measured in #3878: 27.5.46862.48827
    /// carries <c>Ncl.dll</c> and 82 DLLs and is short exactly this file, so every
    /// "is this a service-tier directory" check passes and the failure moves to an assembly
    /// load.</para>
    /// </summary>
    public const string ClosureSentinel = "Microsoft.Identity.ServiceEssentials.Core.dll";

    /// <summary>
    /// Every required file absent from <paramref name="serviceTierDir"/>, empty when the
    /// closure is complete. A directory that does not exist reports the whole set missing.
    /// Never throws — an unreadable path is the caller's third state to classify
    /// (<see cref="ArtifactDirState"/>), not an exception from a list-of-strings function.
    /// </summary>
    public static IReadOnlyList<string> MissingFiles(string serviceTierDir)
    {
        var missing = new List<string>();
        if (!Directory.Exists(serviceTierDir))
        {
            missing.AddRange(CoreEngineDlls);
            missing.Add(ClosureSentinel);
            return missing;
        }
        foreach (var dll in CoreEngineDlls)
            if (!File.Exists(Path.Combine(serviceTierDir, dll)))
                missing.Add(dll);
        if (!File.Exists(Path.Combine(serviceTierDir, ClosureSentinel)))
            missing.Add(ClosureSentinel);
        return missing;
    }

    /// <summary>True when nothing is missing.</summary>
    public static bool IsComplete(string serviceTierDir) => MissingFiles(serviceTierDir).Count == 0;
}
