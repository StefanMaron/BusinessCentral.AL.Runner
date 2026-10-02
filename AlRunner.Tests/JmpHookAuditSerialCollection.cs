using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Test classes that write or read JmpHook's process-wide audit sets (<c>_orphaned</c> and
/// <c>_redundant</c>). <c>JmpHook.ResetOrphanAudit()</c> clears BOTH, so a class that resets while
/// another has added an entry and not yet asserted on it makes that assertion fail (#5188).
/// A <c>DisableParallelization</c> collection runs alone, after every parallel collection, so it
/// also keeps in-process patch installs (which add to the same sets) out of its way.
/// JmpHookAuditCollectionGuardTests fails the build when a class touching the audit API does not join.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JmpHookAuditSerialCollection
{
    public const string Name = "jmphook-audit-serial";
}
