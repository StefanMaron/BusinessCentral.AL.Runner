using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Test classes that reset or read EventSubscriberPatches' process-wide registries in-process
/// (ResetForReload, the subscriber lookups). One class resetting while another scans would
/// interleave on the same static dictionaries.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EventSubscriberRegistrySerialCollection
{
    public const string Name = "event-subscriber-registry-serial";
}
