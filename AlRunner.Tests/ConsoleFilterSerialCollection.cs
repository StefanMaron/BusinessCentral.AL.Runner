// ConsoleFilterSerialCollection — Log's filter is installed by REPLACING the process-wide
// Console.Out / Console.Error writers, and Log.Verbose is a process-wide static. Any test
// that exercises the filter has to do all three of: swap the console writers for a sink,
// call Log.Install(), and set Log.Verbose — then put every one of them back.
//
// xunit runs each test class as its own collection and runs collections IN PARALLEL by
// default (parallelizeTestCollections=true — see xunit.runner.json). Two such classes
// running at once clobber each other's console swap: one class's Log.Install() wraps the
// OTHER class's sink, and one class's `finally` restores the real Console while the other
// is still writing to it. The result is a line landing in the wrong sink, which reads
// exactly like the filter having eaten it — the very thing these tests assert about.
//
// Observed for real while adding the #2750 tests: LogUserFacingTagsTests had been the only
// class doing this and was safe by being alone. Adding two more classes made
// UserFacingTags_SurviveTheDefaultFilter("[watch] waiting") fail — a tag that is exempt,
// has been exempt throughout, and had nothing to do with the change. Same class of
// accidental-parallelism bug as #1696 (see RecordPatchesSerialCollection).
//
// Tests that spawn the real runner as a SUBPROCESS do NOT need to join this collection:
// each subprocess gets its own Console and its own Log statics.
//
// WHAT THIS COVERS, since #2913 closed the hole this note used to describe.
// Every test class that swaps Console.Out/Console.Error is now fenced, not just the ones that
// call Log.Install() — the race needs neither Log.Install() nor Log.Verbose, so the six classes
// this note previously listed as uncovered (AlCallStackCaptureNoFallbackTests,
// CacheKeyUnhashableDependencyTests, HotPathHookCostTests, InstallTriggerAsyncObservationTests,
// PhaseLogTests, WatchSourceTests) joined this collection.
//
// ConsoleSwapIsolationGuardTests is what keeps it closed: it fails the build when a test class
// swaps the console without carrying a [Collection] whose definition sets
// DisableParallelization = true. It is not this collection specifically — any non-parallelizable
// collection satisfies it, because what a swapper needs is exclusive scheduling and not one
// particular name. That is how a class needing a different serial collection for an unrelated
// reason stays correct: ProvisionGapLogTests is in RecordPatchesSerialCollection for
// ProvisionGapLog's process-global state, cannot also be in this one, and is already safe.
//
// So a class that needs another serial collection should join THAT one and keep its swap; the
// older advice here — drop the swap rather than become "a sixth perpetrator" — was a workaround
// for the missing guard and no longer applies. Dropping a swap that only silences noise is still
// the better change where it applies, just not a requirement.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleFilterSerialCollection
{
    public const string Name = "console-filter-serial";
}
