codeunit 99001 "MDI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "MDI Src";

    [Test]
    procedure ModuleDependencyInfo_Id_ReturnsZeroGuid()
    var
        Dep: ModuleDependencyInfo;
    begin
        // Default ModuleDependencyInfo has zero Guid; Id() converts Guid to Text
        Assert.AreEqual('{00000000-0000-0000-0000-000000000000}', Src.GetDependencyId(Dep),
            'Id() must return zero Guid text for default ModuleDependencyInfo');
    end;

    [Test]
    procedure ModuleDependencyInfo_Name_ReturnsText()
    var
        Dep: ModuleDependencyInfo;
    begin
        Assert.AreEqual('', Src.GetDependencyName(Dep),
            'Name() must return empty string for default ModuleDependencyInfo');
    end;

    [Test]
    procedure ModuleDependencyInfo_Publisher_ReturnsText()
    var
        Dep: ModuleDependencyInfo;
    begin
        Assert.AreEqual('', Src.GetDependencyPublisher(Dep),
            'Publisher() must return empty string for default ModuleDependencyInfo');
    end;
}
