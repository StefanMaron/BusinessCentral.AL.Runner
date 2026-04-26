codeunit 60281 "NAR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "NAR Src";

    [Test]
    procedure GetArchiveVersion_ReturnsEmpty()
    begin
        Assert.AreEqual('', Src.GetArchiveVersion(),
            'NavApp.GetArchiveVersion must return empty in standalone mode');
    end;

    [Test]
    procedure LoadPackageData_DoesNotThrow()
    begin
        Assert.IsTrue(Src.LoadPackageData_DoesNotThrow(),
            'NavApp.LoadPackageData must complete without throwing');
    end;

    [Test]
    procedure RestoreArchiveData_DoesNotThrow()
    begin
        Assert.IsTrue(Src.RestoreArchiveData_DoesNotThrow(),
            'NavApp.RestoreArchiveData must complete without throwing');
    end;

    [Test]
    procedure DeleteArchiveData_DoesNotThrow()
    begin
        Assert.IsTrue(Src.DeleteArchiveData_DoesNotThrow(),
            'NavApp.DeleteArchiveData must complete without throwing');
    end;
}
