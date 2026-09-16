// BcRuntime.BundleEpoch — which bundle of a multi-bundle invocation an assembly belongs to.
//
// THE MARKER THAT DID NOT EXIST (#4222)
//   A one-shot `al-runner <A> <B>` loads both bundles into one process and runs them one
//   after the other. Before this file the runner kept no record of WHICH bundle iteration an
//   assembly was registered for, only that it had been registered at some point since the last
//   ResetForNewBundleReload — a reset a one-shot run never reaches (Program.cs gates it on
//   watch mode, and --server calls it per request, before its own bundle loop).
//
//   Three existing scopes were measured against a two-bundle reproducer and all three still
//   contained the previous bundle, because all three answer a LOAD-side question:
//   RegisteredModules(), GetModuleAppInfoFor(asm).AppId, and CurrentBundleAssemblies().
//   See docs/limitations.md#event-subscription-virtual-table and issue #4222.
//
// WHY AN EPOCH RATHER THAN A CLEAR
//   The obvious fix — call BcRuntime.ResetForNewBundleReload between bundles — is recorded as
//   known-bad at its own call site (Program.cs, the --server one): it wiped the app bundle's
//   parsed table schemas before the test bundle ran, and every Record op on an app-defined
//   table died with "no NCLMetaTable for table N (AL source not parsed)". A multi-bundle run
//   deliberately SHARES parsed schemas across bundles; an app bundle and its test bundle are
//   the normal pairing. So the inventory has to be scoped without clearing state the next
//   bundle legitimately needs, which is what a stamp does and a clear cannot.
//
//   The same shape #4230 used for #4225, one layer out: that keyed a memo on a registration
//   epoch, this stamps an assembly with the bundle iteration that registered it.
//
// WHAT IT IS AND IS NOT FOR
//   It answers "does this assembly belong to the bundle now running". It is deliberately NOT
//   wired into dispatch: subscriber lookup keys on (publisherId, eventMethodName) and is
//   already selective, and narrowing it would change which subscribers FIRE — a behaviour
//   change this issue does not ask for and no test covers. Inventory reads are the consumers.
using System.Collections.Generic;
using System.Reflection;

namespace AlRunner;

public static partial class BcRuntime
{
    // The bundle iteration now running. Advanced by BeginBundleEpoch, which Program.cs calls
    // once per bundle of a one-shot invocation. Starts at 0 so an assembly registered before
    // any BeginBundleEpoch call (the single-bundle path, which never calls it) carries the
    // same epoch as the run that reads it — see IsCurrentBundleAssembly's fail-open note.
    private static int _bundleEpoch;

    // Assembly -> the epoch current when it was last noted as belonging to a bundle. Written
    // only through NoteCurrentBundleAssembly, the single choke point every registration route
    // funnels through (SetTestAssembly and DependencyLoader both reach it via
    // RegisterAssemblyGeneration; a reused cached dependency calls it directly).
    private static readonly Dictionary<Assembly, int> _bundleEpochByAssembly = new();

    /// <summary>
    /// The bundle iteration now running — 0 until <see cref="BeginBundleEpoch"/> is first
    /// called. Exposed for tests and for diagnostics; callers deciding scope should ask
    /// <see cref="IsCurrentBundleAssembly"/> instead.
    /// </summary>
    internal static int CurrentBundleEpoch
    {
        get { lock (_bundleEpochByAssembly) return _bundleEpoch; }
    }

    /// <summary>
    /// Start a new bundle iteration: every assembly noted from here on belongs to it, and
    /// <see cref="IsCurrentBundleAssembly"/> stops answering true for the previous bundle's.
    ///
    /// <para>Called once per bundle by the one-shot bundle loop, unconditionally — unlike
    /// <see cref="ResetForNewBundleReload"/>, which is gated on watch mode because it CLEARS
    /// parsed schemas the next bundle needs. This clears nothing.</para>
    /// </summary>
    internal static void BeginBundleEpoch()
    {
        lock (_bundleEpochByAssembly) _bundleEpoch++;
    }

    private static void StampBundleEpoch(Assembly asm)
    {
        // Assigned rather than added-if-absent: a dependency module reused as-is across two
        // bundles is re-noted by DependencyLoader precisely so it counts as the NEW bundle's
        // (#4100), and an add-if-absent would leave it stamped with the first bundle that
        // happened to load it.
        lock (_bundleEpochByAssembly) _bundleEpochByAssembly[asm] = _bundleEpoch;
    }

    /// <summary>
    /// True when <paramref name="asm"/> was registered for the bundle iteration now running.
    ///
    /// <para><b>Fails open for an assembly this never saw</b>, answering true: the stamp is
    /// written only for bundle and dependency modules, so a service-tier DLL, a Base/System
    /// Application chunk, or anything loaded before the first <see cref="BeginBundleEpoch"/>
    /// has no entry — and those are exactly the assemblies whose subscribers every bundle
    /// legitimately sees. Failing closed would drop Microsoft's own subscribers from the
    /// inventory, a much larger wrong answer than the one being fixed.</para>
    /// </summary>
    internal static bool IsCurrentBundleAssembly(Assembly asm)
    {
        lock (_bundleEpochByAssembly)
            return !_bundleEpochByAssembly.TryGetValue(asm, out var epoch) || epoch == _bundleEpoch;
    }

    /// <summary>Drop every stamp — <see cref="ResetForNewBundleReload"/> already forgets the
    /// assemblies these describe, and a stamp outliving its assembly list would let a stale
    /// generation read as current.</summary>
    private static void ResetBundleEpochStamps()
    {
        lock (_bundleEpochByAssembly) _bundleEpochByAssembly.Clear();
    }
}
