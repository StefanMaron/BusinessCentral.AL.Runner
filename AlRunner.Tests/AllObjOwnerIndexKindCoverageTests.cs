// AllObjOwnerIndexKindCoverageTests — issue #4000.
//
// The BC-behaviour claim (AllObj's package ids of an app's own tableextension, pageextension,
// enum, enumextension and permission set are that app's) is asserted upstream in corpus
// codeunit 60989 (corpus PR #333). This file pins the two runner mechanisms that answer it:
// the emitted-type-name scan now covers the three extension kinds that emit a type, and the
// kinds that emit none are owned through their source declaration.
using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AllObjOwnerIndexKindCoverageTests
{
    private static readonly Guid AppUnderTest = new("6EDA7750-0000-4000-8000-000000000001");
    private static readonly Guid SymbolOwner = new("5A8EDAC8-0000-4000-8000-000000000002");

    // Type names as an emitted AL assembly carries them: extension ids deliberately reuse the
    // ids of base objects of another kind, so a scan that attributes by id alone, or strips
    // the wrong prefix, lands on the wrong (kind, id) pair.
    private static readonly string[] EmittedTypeNames =
    {
        "Record60024", "Page60657", "Report60100",
        "TableExtension60024", "PageExtension60657", "ReportExtension60100",
        "Codeunit60989",
    };

    private static IEnumerable<string> Emitted(string prefix)
        => EmittedTypeNames.Where(n => n.StartsWith(prefix, StringComparison.Ordinal));

    private static Dictionary<(string Kind, int Id), Guid> ScanEmitted()
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();
        RecordPatches.AddEmittedAssemblyOwners(index, "MyApp.Emitted", AppUnderTest, Emitted);
        return index;
    }

    [Theory]
    [InlineData("tableextension", 60024)]
    [InlineData("pageextension", 60657)]
    [InlineData("reportextension", 60100)]
    public void EmittedExtensionType_IsOwnedUnderItsOwnKind(string kind, int id)
    {
        var index = ScanEmitted();
        Assert.True(index.TryGetValue((kind, id), out var owner), $"({kind}, {id}) is not in the owner index");
        Assert.Equal(AppUnderTest, owner);
    }

    [Fact]
    public void EmittedTypeScan_AttributesEachNameToExactlyOnePair()
    {
        // Seven type names, seven pairs: no extension type is also read as its base kind.
        var index = ScanEmitted();
        Assert.Equal(
            new[]
            {
                ("codeunit", 60989), ("page", 60657), ("pageextension", 60657), ("report", 60100),
                ("reportextension", 60100), ("table", 60024), ("tableextension", 60024),
            },
            index.Keys.OrderBy(k => k.Kind, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("Enum", "enum", 60880)]
    [InlineData("EnumExtension", "enumextension", 60881)]
    [InlineData("PermissionSet", "permissionset", 60930)]
    [InlineData("PermissionSetExtension", "permissionsetextension", 60931)]
    public void SourceDeclaredObject_IsOwnedByItsDeclaringApp(string declaredKind, string indexKind, int id)
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();
        RecordPatches.AddSourceDeclaredOwners(index, new Dictionary<(string Kind, int Id), Guid>
        {
            [(declaredKind, id)] = AppUnderTest,
        });

        Assert.True(index.TryGetValue((indexKind, id), out var owner), $"({indexKind}, {id}) is not in the owner index");
        Assert.Equal(AppUnderTest, owner);
    }

    [Fact]
    public void SourceDeclaredOwner_DoesNotOverrideAnEarlierAnswer_AndSkipsAnEmptyAppId()
    {
        var index = new Dictionary<(string Kind, int Id), Guid> { [("enum", 60880)] = SymbolOwner };
        RecordPatches.AddSourceDeclaredOwners(index, new Dictionary<(string Kind, int Id), Guid>
        {
            [("Enum", 60880)] = AppUnderTest,
            [("PermissionSet", 60930)] = Guid.Empty,
        });

        Assert.Equal(SymbolOwner, index[("enum", 60880)]);
        Assert.False(index.ContainsKey(("permissionset", 60930)));
    }
}
