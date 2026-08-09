// RecordPatchesSerialCollection — the AL source parsers write their results into
// PROCESS-WIDE static dictionaries on RecordPatches (`_parsedTables`, `_parsedPages`, …).
// A test that drives a parser and then reads its result back out of one of those
// dictionaries is therefore reading shared mutable state, and anything else running
// concurrently that repopulates or clears them (RecordPatches.ResetForReload, an
// in-process bundle load, another parser test using a neighbouring id) can land between
// the write and the read.
//
// xunit runs each test class as its own collection and runs collections IN PARALLEL by
// default, so those classes were only ever accidentally safe. It surfaced on #1696: an
// AlSourceParserSyntaxTreeTests case failed as "table 61896 was not parsed at all" on four
// of five CI legs while passing on the fifth, passing in isolation, and passing in two full
// local suite runs (BC 28.1.49838.50794 and .53507 — the exact build a failing leg used).
// The parse itself was verified correct against the failing build's own
// Microsoft.Dynamics.Nav.CodeAnalysis, which is what rules the parser out and leaves
// scheduling as the difference: CI runners have a different core count, so xunit picks a
// different degree of parallelism.
//
// DisableParallelization makes this collection run on its own, so the parser tests get the
// static dictionaries to themselves for the duration.
using Xunit;

namespace AlRunner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RecordPatchesSerialCollection
{
    public const string Name = "record-patches-serial";
}
