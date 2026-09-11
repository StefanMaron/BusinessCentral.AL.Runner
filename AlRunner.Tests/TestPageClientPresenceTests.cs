// TestPageClientPresenceTests — pins the fact two code comments got wrong for a long time
// (#3799): Microsoft.Dynamics.Nav.Client.TestPageClient.dll SHIPS in the BC artifact
// directory and LOADS here. It is not absent, and the runner's parallel TestPage
// implementation does not rest on it being absent.
//
// Why this test exists at all, rather than just a corrected comment: the comments claiming
// the DLL "does not exist in the runner" / was "not present in the runner" were false, and
// because they were written down every later reader had a documented reason not to look
// again. A comment cannot fail; this can. If Microsoft ever genuinely stops shipping the
// assembly, this test fails and the docs get revisited on evidence instead of on memory.
//
// What it deliberately does NOT assert: that reusing TestPageClient is viable. It is not,
// for a measured reason that has nothing to do with presence — BC's TestPageProxy resolves
// fields only by name through a control tree, and the runner synthesizes none for a
// precompiled page. docs/testpageclient-reuse.md has that measurement.
using System;
using System.IO;
using System.Reflection;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageClientPresenceTests
{
    private const string TestPageClientAssembly =
        "Microsoft.Dynamics.Nav.Client.TestPageClient";

    private static string ServiceTierDirOrSkip()
    {
        string dir = string.Empty;
        string? selectionError = null;
        try { dir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir; }
        catch (Exception ex) { selectionError = ex.Message; }
        TestArtifacts.SkipIf(selectionError != null,
            $"no BC service-tier artifact directory could be selected: {selectionError}");

        // #3893: a selected directory is not a usable one. Selection can land on a
        // partially-provisioned directory (27.5.46862.48827 carries Ncl.dll and 82 DLLs, and
        // is short the closure sentinel) — this suite then failed inside Assembly.Load, and
        // #3799 traced that by hand to an absent Framework.UI.dll. This is a presence audit,
        // so a broken artifact directory is an environment fault, not a product defect: skip
        // with the classifier's own explanation rather than reporting a false failure.
        var state = AlRunner.Infrastructure.ArtifactDirState.Classify(dir);
        TestArtifacts.SkipIf(!state.IsUsable, state.Explain());
        return dir;
    }

    [SkippableFact]
    public void TestPageClientDll_ShipsInTheArtifactDirectory()
    {
        var dir = ServiceTierDirOrSkip();
        var probe = Path.Combine(dir, TestPageClientAssembly + ".dll");

        Assert.True(File.Exists(probe),
            $"'{probe}' does not exist. Two code comments once claimed this assembly was "
            + "'not present in the runner' and that claim was false on every version measured "
            + "in #3799 (27.0, 27.3, 27.5, 28.0-28.4). If it is genuinely absent now, that is "
            + "a real change: re-measure and update docs/testpageclient-reuse.md rather than "
            + "restoring the old wording.");
    }

    // The load BC itself performs, reproduced exactly: NavTestExecution.CreateTestClientSession
    // builds the name as "Microsoft.Dynamics.Nav.Client.TestPageClient" + Ncl's own
    // version/culture/PublicKeyToken suffix. That is a STRONG-NAME load, and "the strong name
    // did not match" was one candidate explanation for the original swallowed failure. It is
    // not the explanation: the identities match exactly and the load succeeds.
    [SkippableFact]
    public void TheStrongNameLoadBcPerforms_Succeeds_AndYieldsTestPageClientSession()
    {
        var dir = ServiceTierDirOrSkip();
        TestArtifacts.SkipIf(
            !File.Exists(Path.Combine(dir, TestPageClientAssembly + ".dll")),
            "BC artifacts are incomplete: TestPageClient.dll is not in the artifact dir.");
        // BC reaches TestPageClientSession through Framework.UI. One provisioned directory
        // (27.5.46862.48827) lacks it, which is exactly the missing-transitive-dependency
        // case BC's `catch (FileNotFoundException)` converts into "test client not installed".
        TestArtifacts.SkipIf(
            !File.Exists(Path.Combine(dir, "Microsoft.Dynamics.Framework.UI.dll")),
            "BC artifacts are incomplete: Microsoft.Dynamics.Framework.UI.dll is not in the "
            + "artifact dir, so TestPageClientSession cannot resolve (see #3799).");

        DependencyLoader.EnsureResolverInstalled_Public();

        var ncl = Assembly.Load(new AssemblyName("Microsoft.Dynamics.Nav.Ncl"));
        var nclFullName = ncl.FullName!;
        var requested = TestPageClientAssembly
                        + nclFullName.Substring(nclFullName.IndexOf(','));

        var asm = Assembly.Load(requested);

        Assert.NotNull(asm);
        // Identity, not just "something loaded": the whole point is that the strong name matches.
        Assert.Equal(ncl.GetName().Version, asm.GetName().Version);
        Assert.Equal(
            BitConverter.ToString(ncl.GetName().GetPublicKeyToken()!),
            BitConverter.ToString(asm.GetName().GetPublicKeyToken()!));

        // The exact type and factory BC invokes by reflection.
        var sessionType = asm.GetType(
            "Microsoft.Dynamics.Nav.Client.TestPageClient.TestPageClientSession",
            throwOnError: false);
        Assert.NotNull(sessionType);

        var create = sessionType!.GetMethod(
            "Create", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(create);
        Assert.Equal(6, create!.GetParameters().Length);
    }

    // The OTHER half of what the Forms.cs comment got wrong. It said TestClientProxy<T>.Proxy
    // "tries to load Microsoft.Dynamics.Nav.Client.TestPageClient". It does not: Proxy is a
    // System.Reflection.DispatchProxy living in Nav.Types.dll, and the reason the Cecil
    // rewrite strips it is that its dispatcher needs an initialized UISessionManager — which
    // is what the two neighbouring comments in that same file always said.
    [SkippableFact]
    public void TestClientProxy_IsADispatchProxy_NotAnAssemblyLoader()
    {
        var dir = ServiceTierDirOrSkip();
        TestArtifacts.SkipIf(
            !File.Exists(Path.Combine(dir, "Microsoft.Dynamics.Nav.Types.dll")),
            "BC artifacts are incomplete: Nav.Types.dll is not in the artifact dir.");

        DependencyLoader.EnsureResolverInstalled_Public();

        var types = Assembly.Load(new AssemblyName("Microsoft.Dynamics.Nav.Types"));
        var proxyType = types.GetType(
            "Microsoft.Dynamics.Nav.Types.TestClientProxy`1", throwOnError: false);

        Assert.NotNull(proxyType);
        Assert.Equal(
            "System.Reflection.DispatchProxy",
            proxyType!.BaseType?.FullName);

        // It declares Proxy(T) itself — the call site the Cecil rewrite removes.
        var proxy = proxyType.GetMethod(
            "Proxy", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.NotNull(proxy);
    }
}
