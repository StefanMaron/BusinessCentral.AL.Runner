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
// WHAT THIS DOES NOT COVER — read this before assuming the hole is closed.
// Every class that calls Log.Install() is in here, and that is the whole of it. The race
// described above needs neither Log.Install() nor Log.Verbose: two classes swapping
// Console.Out/Console.Error is enough on its own. These swap and are NOT in this collection —
// AlCallStackCaptureNoFallbackTests, HotPathHookCostTests, PhaseLogTests, WatchSourceTests
// (no collection at all) and ProvisionGapLogTests (in RecordPatchesSerialCollection, because
// ProvisionGapLog is process-global state; a class cannot be in two collections, so it cannot
// simply join this one). Tracked in #2913, with the options.
//
// The consequence for anything added HERE: a class that needs another serial collection must
// NOT swap the console as well — it would be a sixth perpetrator of the same race rather than
// a smaller one. CorruptSidecarGapSummaryTests (CorruptSidecarLoudnessTests.cs) is the worked
// example: it needs RecordPatchesSerialCollection, so it takes the stderr noise instead.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleFilterSerialCollection
{
    public const string Name = "console-filter-serial";
}
