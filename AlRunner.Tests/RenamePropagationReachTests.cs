// RenamePropagationReachTests — issue #2325.
//
// The AL-observable claims (a renamed tenant profile carries User Personalization and Tenant
// Profile Page Metadata with it; a rename re-keys a referencing primary-key field; a per-tenant
// parent's rename reaches a per-company child) are adjudicated upstream by a real service tier:
// corpus codeunits 60018 and 61207. What is pinned HERE is the runner's own machinery those
// results depend on:
//
//   - which tables the runner's rename reverse index may consider, which must be BC's own
//     "non-virtual snapshot" classification rather than an id cut-off;
//   - the unlicensed company-name filter the CompanyHelper license reads are redirected to.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class RenamePropagationReachTests
{
    // Before #2325 every id >= 2,000,000,000 was excluded, so a rename never reached a system
    // table holding a TableRelation to it. BC's snapshot keeps the non-virtual ones.
    [Theory]
    [InlineData(2000000073, "User Personalization")]
    [InlineData(2000000187, "Tenant Profile Page Metadata")]
    [InlineData(2000000120, "User")]
    public void NonVirtualSystemTable_IsInTheReverseIndexUniverse(int tableId, string name)
        => Assert.True(RecordPatches.IsInBcNonVirtualSnapshot(tableId),
            $"{name} ({tableId}) is a non-virtual system table; BC's rename propagation reaches it");

    // The other direction: virtual system tables stay out, exactly as
    // GetSnapshotOfAllNonVirtualMetaTables leaves them out. All Profile is the table whose
    // rename started #2325; it must never itself be treated as a referencing table.
    [Theory]
    [InlineData(2000000178, "All Profile")]
    [InlineData(2000000041, "Field")]
    [InlineData(2000000038, "AllObj")]
    public void VirtualSystemTable_IsNotInTheReverseIndexUniverse(int tableId, string name)
        => Assert.False(RecordPatches.IsInBcNonVirtualSnapshot(tableId),
            $"{name} ({tableId}) is virtual; BC's snapshot excludes it");

    [Theory]
    [InlineData(18)]
    [InlineData(50000)]
    [InlineData(1999999999)]
    public void ApplicationTable_IsInTheReverseIndexUniverse(int tableId)
        => Assert.True(RecordPatches.IsInBcNonVirtualSnapshot(tableId));

    // An id in the system range that BC does not know is not a table BC's snapshot contains.
    [Fact]
    public void UnknownSystemRangeId_IsNotInTheReverseIndexUniverse()
        => Assert.False(RecordPatches.IsInBcNonVirtualSnapshot(2099999999));

    // The redirect stands in for license.CompanyNameFilter; an empty/null filter is what a
    // license without a company restriction answers, and BC's body then reads every Company row.
    // Any non-empty value would make GetAllCompaniesAsync filter the Company table and a
    // per-company referencing table would silently stop following a per-tenant rename.
    [Fact]
    public void UnlicensedCompanyNameFilter_IsNoFilter()
    {
        Assert.True(string.IsNullOrEmpty(CompanyAccessPatches.UnlicensedCompanyNameFilter(null)));
        Assert.True(string.IsNullOrEmpty(CompanyAccessPatches.UnlicensedCompanyNameFilter(new object())));
    }

    // The All Profile cascade is prepended to EVERY rename's propagation, so for anything that
    // is not a non-temporary All Profile record it must return without touching anything.
    [Fact]
    public void CascadeTenantProfileRename_IsNoOp_ForAnythingButAnAllProfileRecord()
    {
        AllProfileWritePatches.CascadeTenantProfileRename(null, null, null);
        AllProfileWritePatches.CascadeTenantProfileRename(new object(), new object(), new object());
    }
}
