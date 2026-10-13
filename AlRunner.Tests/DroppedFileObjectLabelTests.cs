// #5559 — which object names a dropped FILE. BC lists the objects that failed to emit in no fixed order
// (one module named its profile on one process and its codeunit on another), and the first one decided the
// verdict. The choice must not depend on that order.

using Xunit;

namespace AlRunner.Tests;

public sealed class DroppedFileObjectLabelTests
{
    private static readonly (string Type, string Namespace, string Name) Profile = ("Profile", "", "Bad Profile");
    private static readonly (string Type, string Namespace, string Name) Codeunit = ("Codeunit", "", "Broken Tests");
    private static readonly (string Type, string Namespace, string Name) Table = ("Table", "Ns", "Broken Table");

    [Fact]
    public void AProfile_NamesTheFile_InEitherOrder()
    {
        Assert.Equal(Profile, BcCompiler.PickDroppedFileObject(new[] { Profile, Codeunit }));
        Assert.Equal(Profile, BcCompiler.PickDroppedFileObject(new[] { Codeunit, Profile }));
        Assert.Equal(Profile, BcCompiler.PickDroppedFileObject(new[] { Table, Codeunit, Profile }));
    }

    [Fact]
    public void WithoutAProfile_TheFirstFailedObjectNamesTheFile()
    {
        Assert.Equal(Codeunit, BcCompiler.PickDroppedFileObject(new[] { Codeunit, Table }));
        Assert.Equal(Table, BcCompiler.PickDroppedFileObject(new[] { Table, Codeunit }));
    }

    [Fact]
    public void NoFailedObjectDeclaredHere_NamesNothing()
    {
        Assert.Null(BcCompiler.PickDroppedFileObject(Array.Empty<(string, string, string)>()).Name);
    }
}
