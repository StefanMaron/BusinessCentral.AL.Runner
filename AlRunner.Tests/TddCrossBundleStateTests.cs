// What --tdd remembers across the bundles of a run: the call edges of the source bundles another bundle
// depends on, and when it keeps them (#5264), and the codeunits a bundle's compile dropped (#5266). In-process: TddCrossBundle is process-global state, so these run in the serial collection and
// leave it empty. The claim is about the runner's own bookkeeping, not about BC.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class TddCrossBundleStateTests
{
    private static readonly Guid App = new("a1b2c3d4-0000-4a61-8b93-0c7d5e9f1a99");

    private static string BundleDir(string name, string? source)
    {
        var dir = Path.Combine(TestScratch.Dir("al-runner-tdd-keygraph"), name);
        Directory.CreateDirectory(dir);
        if (source != null) File.WriteAllText(Path.Combine(dir, "X.Codeunit.al"), source);
        return dir;
    }

    private static void Clean()
    {
        TddCrossBundle.ClearSourceImpls();
        TddCrossBundle.ResetForNewCycle();
    }

    /// <summary>
    /// Call edges are worth keeping only for a bundle another bundle depends on, and only when some
    /// bundle of the run declares an [EventSubscriber]: a run with none never reads them, and keeping
    /// them would make a source dependency compile instead of being served from the compiled-deps cache.
    /// The control is the same bundles with a subscriber added.
    /// </summary>
    [Fact]
    public void KeyGraph_IsWantedOnlyForAnImplOfARunWithASubscriber()
    {
        try
        {
            Clean();
            var impl = BundleDir("impl", "codeunit 1 \"A\" { procedure P() begin end; }");
            var plain = BundleDir("plain", "codeunit 2 \"B\" { procedure Q() begin end; }");
            TddCrossBundle.RegisterRunBundle(impl);
            TddCrossBundle.RegisterRunBundle(plain);
            TddCrossBundle.RegisterSourceImpl(impl, null, App);

            Assert.True(TddCrossBundle.IsSourceImpl(App));
            Assert.False(TddCrossBundle.WantsKeyGraph(App)); // no subscriber anywhere

            File.WriteAllText(Path.Combine(plain, "X.Codeunit.al"),
                "codeunit 2 \"B\" { [EventSubscriber(ObjectType::Codeunit, Codeunit::\"A\", 'OnP', '', false, false)] local procedure H() begin end; }");
            TddCrossBundle.ResetForNewCycle(); // the probe is per cycle
            Assert.True(TddCrossBundle.WantsKeyGraph(App));

            Assert.False(TddCrossBundle.WantsKeyGraph(Guid.NewGuid())); // not an impl of this run
        }
        finally { Clean(); }
    }

    /// <summary>
    /// #5286, #5309: a table extension or a page extension, a RecordRef or FieldRef, and a Codeunit.Run whose first argument is not a
    /// `Codeunit::Name` each make the edges of an earlier bundle readable, because each may start something a
    /// LATER bundle declares; a run with none of them does not, and neither does a Codeunit.Run naming its
    /// codeunit. The control is the same bundles with only the last two.
    /// </summary>
    [Theory]
    [InlineData("tableextension 2 \"E\" extends \"A\" { trigger OnModify() begin end; }", true)]
    [InlineData("pageextension 2 \"E\" extends \"A\" { trigger OnOpenPage() begin end; }", true)]
    [InlineData("codeunit 2 \"B\" { procedure Q() var R: RecordRef; begin R.Insert(true); end; }", true)]
    [InlineData("codeunit 2 \"B\" { procedure Q() var F: FieldRef; begin F.Validate(1); end; }", true)]
    [InlineData("codeunit 2 \"B\" { procedure Q(Id: Integer) begin Codeunit.Run(Id); end; }", true)]
    [InlineData("codeunit 2 \"B\" { procedure Q() begin Codeunit.Run(Codeunit::\"A\"); end; }", false)]
    [InlineData("codeunit 2 \"B\" { procedure Q() var A: Codeunit \"A\"; begin A.Run(); end; }", false)]
    [InlineData("codeunit 2 \"B\" { procedure Q() begin end; }", false)]
    public void KeyGraph_IsWantedWhenALaterBundleCouldBeStartedByAnOperation(string otherBundle, bool wanted)
    {
        try
        {
            Clean();
            var impl = BundleDir("impl", "codeunit 1 \"A\" { procedure P() begin end; }");
            var other = BundleDir("other", otherBundle);
            TddCrossBundle.RegisterRunBundle(impl);
            TddCrossBundle.RegisterRunBundle(other);
            TddCrossBundle.RegisterSourceImpl(impl, null, App);

            Assert.Equal(wanted, TddCrossBundle.WantsKeyGraph(App));
        }
        finally { Clean(); }
    }

    /// <summary>
    /// The callers of a procedure key, transitively, across the edges bundles recorded, each once even
    /// with a cycle; a key nobody calls has only itself. A new cycle forgets the edges and the bundles'
    /// graphs.
    /// </summary>
    [Fact]
    public void CallersOf_FollowsRecordedEdgesTransitivelyAndForgetsThemWithTheCycle()
    {
        try
        {
            Clean();
            TddCrossBundle.RecordKeyGraph(App, new[] { ("c|ondo", "c|doit"), ("c|doit", "c|doittwice"), ("c|doittwice", "c|ondo") });
            Assert.True(TddCrossBundle.HasKeyGraph(App));

            Assert.Equal(new[] { "c|ondo", "c|doit", "c|doittwice" }, TddCrossBundle.CallersOf("c|ondo"));
            Assert.Equal(new[] { "c|doittwice", "c|ondo", "c|doit" }, TddCrossBundle.CallersOf("c|doittwice"));
            Assert.Equal(new[] { "other|p" }, TddCrossBundle.CallersOf("other|p"));

            TddCrossBundle.ResetForNewCycle();
            Assert.False(TddCrossBundle.HasKeyGraph(App));
            Assert.Equal(new[] { "c|ondo" }, TddCrossBundle.CallersOf("c|ondo"));
        }
        finally { Clean(); }
    }
    /// <summary>
    /// #5266: a codeunit a source bundle's compile dropped is looked up by id, whichever bundle asks, and a
    /// new cycle forgets it. The control is an id nothing dropped.
    /// </summary>
    [Fact]
    public void DroppedCodeunits_AreFoundByIdAndForgottenWithTheCycle()
    {
        try
        {
            Clean();
            TddCrossBundle.RegisterDroppedCodeunit(65499, new TddCrossBundle.DroppedCodeunit("AL Runner_Some Library", "Some Dropped Helper", "error AL0132: gone"));

            Assert.Equal("Some Dropped Helper", TddCrossBundle.DroppedCodeunitById(65499)?.Name);
            Assert.Null(TddCrossBundle.DroppedCodeunitById(65500));

            var message = BcRuntime.BuildMissingCodeunitMessageForTests(65499);
            Assert.Contains("Codeunit 65499 (\"Some Dropped Helper\")", message);
            Assert.Contains("AL0132", message);
            Assert.DoesNotContain("provision", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("provision", BcRuntime.BuildMissingCodeunitMessageForTests(65500), StringComparison.OrdinalIgnoreCase);

            TddCrossBundle.ResetForNewCycle();
            Assert.Null(TddCrossBundle.DroppedCodeunitById(65499));
            Assert.Contains("provision", BcRuntime.BuildMissingCodeunitMessageForTests(65499), StringComparison.OrdinalIgnoreCase);
        }
        finally { Clean(); }
    }
}
