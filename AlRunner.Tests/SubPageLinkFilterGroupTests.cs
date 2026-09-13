// The runner-side half of #4156: a part's SubPageLink entries keep the filter group the page
// metadata declares, which LiveNavTestPart.ApplyLink applies them in. The AL-observable claim
// (group 4 answers the link, group 0 answers '') is measured upstream by corpus codeunit 60232
// "SPLG Tests"; this pins only that the group is read from the metadata rather than dropped.
using System.Collections.Generic;
using AlRunner;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class SubPageLinkFilterGroupTests
{
    private static FilterDefinition Link(int group, int fieldId, FilterType kind, string value)
        => new() { FilterGroup = group, FieldID = fieldId, FilterType = kind, FilterValue = value };

    [Fact]
    public void SubPageLinks_CarryTheLinkGroupFromMetadata_ForEveryKind()
    {
        var definition = new InfopartPageDefinition
        {
            SubFormLink = new List<FilterDefinition>
            {
                Link(4, 1, FilterType.FIELD, "3"),
                Link(4, 2, FilterType.CONST, "H9"),
                Link(4, 5, FilterType.FILTER, "1|2"),
            },
        };

        var links = LiveNavTestPage.SubPageLinks(definition, partPageId: 60232);

        Assert.Equal(3, links.Length);
        Assert.All(links, l => Assert.Equal(4, l.FilterGroup));
        Assert.Equal((1, FilterType.FIELD, 3), (links[0].PartFieldNo, links[0].Kind, links[0].ParentFieldNo));
        Assert.Equal((2, FilterType.CONST, "H9"), (links[1].PartFieldNo, links[1].Kind, links[1].Value));
    }

    // The group is the metadata's, not a constant: an entry declaring another group keeps it.
    [Fact]
    public void SubPageLinks_KeepANonLinkGroupAsDeclared()
    {
        var definition = new InfopartPageDefinition
        {
            SubFormLink = new List<FilterDefinition> { Link(2, 1, FilterType.CONST, "X"), Link(4, 2, FilterType.CONST, "Y") },
        };

        var links = LiveNavTestPage.SubPageLinks(definition, partPageId: 60232);

        Assert.Equal(new[] { 2, 4 }, new[] { links[0].FilterGroup, links[1].FilterGroup });
    }
}
