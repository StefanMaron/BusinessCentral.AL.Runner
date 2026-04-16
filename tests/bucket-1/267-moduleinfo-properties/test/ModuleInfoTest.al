codeunit 88001 "MI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "MI Src";

    [Test]
    procedure AppVersion_Default_IsZero()
    begin
        // Positive: default ModuleInfo.AppVersion formats as 0.0.0.0 (zero Version).
        Assert.AreEqual(
            '0.0.0.0',
            Src.DefaultAppVersion(),
            'Default ModuleInfo.AppVersion must be 0.0.0.0');
    end;

    [Test]
    procedure DataVersion_Default_IsZero()
    begin
        // Positive: default ModuleInfo.DataVersion formats as 0.0.0.0 (zero Version).
        Assert.AreEqual(
            '0.0.0.0',
            Src.DefaultDataVersion(),
            'Default ModuleInfo.DataVersion must be 0.0.0.0');
    end;

    [Test]
    procedure Id_Default_IsEmptyGuid()
    begin
        // Positive: default ModuleInfo.Id formats as empty GUID (no braces in AL Format output).
        Assert.AreEqual(
            '00000000-0000-0000-0000-000000000000',
            Src.DefaultId(),
            'Default ModuleInfo.Id must be empty GUID');
    end;

    [Test]
    procedure PackageId_Default_IsEmptyGuid()
    begin
        // Positive: default ModuleInfo.PackageId formats as empty GUID (no braces in AL Format output).
        Assert.AreEqual(
            '00000000-0000-0000-0000-000000000000',
            Src.DefaultPackageId(),
            'Default ModuleInfo.PackageId must be empty GUID');
    end;

    [Test]
    procedure Name_Default_IsEmpty()
    begin
        // Positive: default ModuleInfo.Name is empty string.
        Assert.AreEqual(
            '',
            Src.DefaultName(),
            'Default ModuleInfo.Name must be empty string');
    end;

    [Test]
    procedure Publisher_Default_IsEmpty()
    begin
        // Positive: default ModuleInfo.Publisher is empty string.
        Assert.AreEqual(
            '',
            Src.DefaultPublisher(),
            'Default ModuleInfo.Publisher must be empty string');
    end;

    [Test]
    procedure Dependencies_Default_IsEmptyList()
    begin
        // Positive: default ModuleInfo.Dependencies has zero entries.
        Assert.AreEqual(
            0,
            Src.DefaultDependencyCount(),
            'Default ModuleInfo.Dependencies must be empty');
    end;
}
