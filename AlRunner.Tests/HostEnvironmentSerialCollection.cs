// HostEnvironmentSerialCollection — serialises the test classes that set a variable in the TEST
// HOST's own process environment (#5282).
//
// Process.Start copies the host's CURRENT environment into the child. Test sites all over
// the suite spawn the runner through `new ProcessStartInfo`, each from whatever collection it
// belongs to, and xunit runs collections in parallel (xunit.runner.json), so a class that sets
// a variable for the length of a test hands that value to every runner another collection
// starts in the window. The value usually names a fixture the first test deletes moments later
// (#5201: AL_RUNNER_WIN32_STUBS_SO, exit 134 in a `--watch` child).
//
// The in-process readers are exposed the same way: a class setting CI or
// AL_RUNNER_METADATA_GROUND_TRUTH changes the verdict of any other class reading it meanwhile.
//
// A DisableParallelization collection runs alone, after every parallel collection has finished
// (CollectionCostOrderer.cs; TestDataStaticsSerialCollection.cs holds the measurement), so a
// setter in ANY such collection has the process to itself. This one exists for a class with no
// other reason to be serialised; a class that already sits in another serial collection stays
// there. HostEnvironmentIsolationGuardTests derives the set of fences and fails the build for
// a class that mutates the environment outside one, whatever the variable is called.
//
// Prefer not to set the variable at all: hand it to the child through its own
// `ProcessStartInfo.Environment`, or give the runner's read a per-flow seam
// (`Win32Stubs.OverrideSoForTests`). Joining this collection is for the class whose subject is
// an in-process read of the real environment.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostEnvironmentSerialCollection
{
    public const string Name = "host-environment-serial";
}
