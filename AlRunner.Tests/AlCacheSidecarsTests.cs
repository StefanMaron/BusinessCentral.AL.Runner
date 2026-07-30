// AlCacheSidecarsTests — pins the AL-output-cache completeness rule.
//
// RED before the fix: a bundle declaring an AL query cache-HIT on {dll + enum sidecar}
// alone. Emit never ran, so the compilation's SymbolReference — the only source of the
// BC-assigned query column ids — was never registered, NCLMetaQuery came out null and
// every query Find threw NullReferenceException inside NavQuery.ValidateTablesNotVirtual.
// Symptom: tests/runner-extras/query-join scored 7/7 on the first run and 0/7 on every
// run after it, because a rebuild changes the cache key and forces a MISS.
//
// The e2e proof is running that suite TWICE (MISS then HIT); this pins the decision rule.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlCacheSidecarsTests
{
    [Fact]
    public void QueryBundle_WithoutQuerySidecar_IsNotServable()
    {
        // The exact broken state: DLL + enum sidecar present, query symbols absent.
        Assert.False(AlCacheSidecars.IsCompleteEntry(
            dllExists: true, enumSidecarExists: true,
            bundleDeclaresQuery: true, querySidecarExists: false));
    }

    [Fact]
    public void QueryBundle_WithQuerySidecar_IsServable()
    {
        Assert.True(AlCacheSidecars.IsCompleteEntry(
            dllExists: true, enumSidecarExists: true,
            bundleDeclaresQuery: true, querySidecarExists: true));
    }

    [Fact]
    public void QuerylessBundle_DoesNotRequireQuerySidecar()
    {
        // The sidecar is only written for bundles declaring a query, so requiring it
        // unconditionally would permanently defeat the cache for every other bundle.
        Assert.True(AlCacheSidecars.IsCompleteEntry(
            dllExists: true, enumSidecarExists: true,
            bundleDeclaresQuery: false, querySidecarExists: false));
    }

    [Fact]
    public void MissingDllOrEnumSidecar_IsNeverServable()
    {
        Assert.False(AlCacheSidecars.IsCompleteEntry(false, true, false, true));
        Assert.False(AlCacheSidecars.IsCompleteEntry(true, false, false, true));
    }
}
