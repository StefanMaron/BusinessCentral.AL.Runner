// LiveNavTestPartRecordlessTests — the C# half of issue #2195: a subpage part whose OWN page
// declares no SourceTable.
//
// The AL-observable claim — that a globals-bound CardPart is reachable from a TestPage, its
// controls read what its OnOpenPage set, and writes through them run its OnValidate — is a
// claim about BC, so it does not belong here. It is measured on a real service tier by corpus
// codeunit 60803 "Test Page NoSrc Part Tests", merged as
// StefanMaron/BusinessCentral.AL.Language.Tests commit ef52b7e9 (PR #80), all eight arms green
// on both BC 27.5 and BC 28.3.
//
// What is provable without a loaded BC runtime is the piece of the fix that made the AL
// possible: LiveNavTestPart accepting a NULL record. Before #2195 it did not, and the reason
// was ApplyLink — it opened with an unconditional RequireRecord("subpage link") on the
// assumption that "a part always has its own SourceTable (it is a subpage over a table)".
// That assumption is false for a CardPart bound to page globals, and it fails in a
// particularly misleading way: every cursor move on such a part would refuse naming a
// SubPageLink the part does not have and never could, because a FIELD SubPageLink names a
// field of the PART's source table.
//
// So the two rows below are the whole distinction: an UNLINKED part must not consult the
// record it has no use for, and a LINKED one still must. The refusal itself is not being
// removed anywhere — a record-less part asked to navigate still refuses, by the name of the
// operation the AL actually called.
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class LiveNavTestPartRecordlessTests
{
    private const int PartPageId = 79841;

    private static LiveNavTestPart RecordlessPart((int PartFieldNo, int ParentFieldNo)[] links)
        => new(
            record: null,
            controlIdToFieldNo: new Dictionary<int, int>(),
            creatable: false,
            page: null,
            owner: new object(),
            pageId: PartPageId,
            parentRecord: null,
            links: links);

    // Constructing it at all is half the fix: the ctor's record parameter was non-nullable,
    // so GetPart had no way to hand a part page with no source table to this class.
    [Fact]
    public void RecordlessUnlinkedPart_Constructs_AndReportsItsOwnPageId()
    {
        var part = RecordlessPart([]);

        Assert.Equal(PartPageId, part.PageId);
        Assert.True(part.Enabled);
        Assert.True(part.Visible);
    }

    // THE REGRESSION ROW. An unlinked, record-less part asked to navigate must refuse by the
    // name of the operation — MoveFirst() — not by "subpage link". Naming the link would be
    // both wrong and unactionable: there is no link to fix.
    [Theory]
    [InlineData("MoveFirst()")]
    [InlineData("MoveLast()")]
    [InlineData("MoveNext()")]
    [InlineData("MovePrevious()")]
    public void RecordlessUnlinkedPart_CursorMove_RefusesByTheOperationsName(string operation)
    {
        var part = RecordlessPart([]);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Move(part, operation));

        Assert.Contains(operation, ex.Message);
        Assert.DoesNotContain("subpage link", ex.Message);
        // The reason anchor the expectations manifest matches on must not drift.
        Assert.Contains("testpage-modal-no-source-table", ex.Message);
        Assert.Contains(PartPageId.ToString(), ex.Message);
    }

    // The other direction, and what keeps the early return honest: a part that DOES declare a
    // link still demands the record the link is applied to. Losing this would turn a genuine
    // "the runner has no record type for this part's source table" gap into a part that
    // silently showed an unfiltered rowset — every parent row's children at once.
    [Fact]
    public void RecordlessLinkedPart_CursorMove_StillRefusesNamingTheLink()
    {
        var part = RecordlessPart([(PartFieldNo: 1, ParentFieldNo: 1)]);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => part.MoveFirst());

        Assert.Contains("subpage link", ex.Message);
    }

    // InsertEmptyRow is the sibling call site: it applies the link, then stamps the linked
    // fields onto the new row. An unlinked record-less part must reach the base class's own
    // New() refusal rather than the link's.
    [Fact]
    public void RecordlessUnlinkedPart_InsertEmptyRow_RefusesByTheOperationsName()
    {
        var part = RecordlessPart([]);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => part.InsertEmptyRow(beforeCurrent: false));

        Assert.Contains("New()", ex.Message);
        Assert.DoesNotContain("subpage link", ex.Message);
    }

    private static void Move(LiveNavTestPart part, string operation)
    {
        switch (operation)
        {
            case "MoveFirst()": part.MoveFirst(); break;
            case "MoveLast()": part.MoveLast(); break;
            case "MoveNext()": part.MoveNext(); break;
            case "MovePrevious()": part.MovePrevious(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "unknown cursor move");
        }
    }
}
