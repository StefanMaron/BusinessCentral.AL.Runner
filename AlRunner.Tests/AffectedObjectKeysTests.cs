// AffectedObjectKeysTests — #5003: the key a statement in a file declaring several objects is attributed
// to, without a server. docs/server-mode.md#affectedonly-and-files-declaring-several-objects.
using AlRunner.Infrastructure;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using Xunit;

namespace AlRunner.Tests;

public class AffectedObjectKeysTests
{
    private const string TrackedFile = "/b/Single.al";
    private const string MultiFile = "/b/Multi.al";

    private static readonly Dictionary<string, AffectedObjectId> Tracked = new()
    {
        [TrackedFile] = new AffectedObjectId("Codeunit", 62430, "MO Single"),
    };

    private static readonly HashSet<string> Multi = new() { MultiFile };

    private static AlCoverageTracker.AlStatementRecord Statement(string file, string label = "", int id = 0)
        => new(file, "Proc", 0, 1, 1, 1, 10, 1, label, id);

    [Fact]
    public void ATrackedFile_KeysAsItsOneObject_WhateverTheScopeSays()
    {
        // Its own identity decides; the scope's object is not consulted for a file with one object.
        var key = AffectedObjectKeys.OfStatement(Statement(TrackedFile, "Table", 1), Tracked, Multi);
        Assert.Equal("Codeunit|id:62430", key);
    }

    /// <summary>
    /// A file declaring several objects keys each statement by the object whose scope ran it: the
    /// same file gives different keys for different owners, and each equals the key the change side
    /// would build for that object's kind and id.
    /// </summary>
    [Theory]
    [InlineData("Codeunit62402", NavCA.SymbolKind.Codeunit, 62402)]
    [InlineData("Table62404", NavCA.SymbolKind.Table, 62404)]
    [InlineData("Record62404", NavCA.SymbolKind.Table, 62404)]
    [InlineData("TableExtension62406", NavCA.SymbolKind.TableExtension, 62406)]
    [InlineData("Page62407", NavCA.SymbolKind.Page, 62407)]
    [InlineData("PageExtension62408", NavCA.SymbolKind.PageExtension, 62408)]
    [InlineData("Report62409", NavCA.SymbolKind.Report, 62409)]
    [InlineData("ReportExtension62410", NavCA.SymbolKind.ReportExtension, 62410)]
    [InlineData("Query62411", NavCA.SymbolKind.Query, 62411)]
    [InlineData("XmlPort62412", NavCA.SymbolKind.XmlPort, 62412)]
    public void AMultiObjectFile_KeysByTheObjectThatRanTheStatement(string className, NavCA.SymbolKind kind, int id)
    {
        var (label, parsedId) = AlCallStackCapture.ParseObjectTypeAndIdForTests(className);
        var key = AffectedObjectKeys.OfStatement(Statement(MultiFile, label, parsedId), Tracked, Multi);
        Assert.Equal(AffectedObjectKeys.Of(new AffectedObjectId(kind.ToString(), id, "any name")), key);
        Assert.Equal($"{kind}|id:{id}", key);
    }

    [Fact]
    public void TwoStatementsOfOneMultiObjectFile_KeyByTheirOwnObjects_NotTheFirst()
    {
        var one = AffectedObjectKeys.OfStatement(Statement(MultiFile, "CodeUnit", 62401), Tracked, Multi);
        var two = AffectedObjectKeys.OfStatement(Statement(MultiFile, "CodeUnit", 62402), Tracked, Multi);
        Assert.Equal("Codeunit|id:62401", one);
        Assert.Equal("Codeunit|id:62402", two);
    }

    [Fact]
    public void AFileInNeitherSet_OrAScopeWithNoObject_IsUnattributable()
    {
        Assert.Null(AffectedObjectKeys.OfStatement(Statement("/b/Other.al", "CodeUnit", 62401), Tracked, Multi));
        // A scope that names no object cannot key a multi-object file, whatever its label says.
        Assert.Null(AffectedObjectKeys.OfStatement(Statement(MultiFile, "?", 0), Tracked, Multi));
        Assert.Null(AffectedObjectKeys.OfStatement(Statement(MultiFile), Tracked, Multi));
    }

    [Fact]
    public void TheKeyOfAnIdLessObject_IsItsName()
    {
        Assert.Equal("Interface|name:Face", AffectedObjectKeys.Of(new AffectedObjectId("Interface", null, "Face")));
    }
}
