codeunit 95001 "XI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XI Src";

    [Test]
    procedure Export_IsNoOp()
    begin
        Assert.IsTrue(Src.CallExport(), 'XmlPort.Export() must be a no-op');
    end;

    [Test]
    procedure Import_IsNoOp()
    begin
        Assert.IsTrue(Src.CallImport(), 'XmlPort.Import() must be a no-op');
    end;

    [Test]
    procedure Run_IsNoOp()
    begin
        Assert.IsTrue(Src.CallRun(), 'XmlPort.Run() must be a no-op');
    end;

    [Test]
    procedure SetDestination_IsNoOp()
    begin
        Assert.IsTrue(Src.CallSetDestination(), 'XmlPort.SetDestination() must be a no-op');
    end;

    [Test]
    procedure SetSource_IsNoOp()
    begin
        Assert.IsTrue(Src.CallSetSource(), 'XmlPort.SetSource() must be a no-op');
    end;

    [Test]
    procedure SetTableView_IsNoOp()
    begin
        Assert.IsTrue(Src.CallSetTableView(), 'XmlPort.SetTableView() must be a no-op');
    end;

    [Test]
    procedure CurrentPath_ReturnsEmpty()
    begin
        Assert.AreEqual('', Src.CallCurrentPath(), 'XmlPort.CurrentPath() must return empty string');
    end;

    [Test]
    procedure FieldDelimiter_RoundTrip()
    begin
        Assert.IsTrue(Src.CallFieldDelimiter(), 'XmlPort.FieldDelimiter(set) must be a no-op');
    end;

    [Test]
    procedure FieldDelimiter_Get_ReturnsDefault()
    begin
        Assert.AreEqual('', Src.GetFieldDelimiter(), 'XmlPort.FieldDelimiter() default must be empty');
    end;

    [Test]
    procedure FieldSeparator_RoundTrip()
    begin
        Assert.IsTrue(Src.CallFieldSeparator(), 'XmlPort.FieldSeparator(set) must be a no-op');
    end;

    [Test]
    procedure FieldSeparator_Get_ReturnsDefault()
    begin
        Assert.AreEqual('', Src.GetFieldSeparator(), 'XmlPort.FieldSeparator() default must be empty');
    end;

    [Test]
    procedure Filename_RoundTrip()
    begin
        Assert.IsTrue(Src.CallFilename(), 'XmlPort.Filename(set) must be a no-op');
    end;

    [Test]
    procedure Filename_Get_ReturnsDefault()
    begin
        Assert.AreEqual('', Src.GetFilename(), 'XmlPort.Filename() default must be empty');
    end;

    [Test]
    procedure RecordSeparator_RoundTrip()
    begin
        Assert.IsTrue(Src.CallRecordSeparator(), 'XmlPort.RecordSeparator(set) must be a no-op');
    end;

    [Test]
    procedure RecordSeparator_Get_ReturnsDefault()
    begin
        Assert.AreEqual('', Src.GetRecordSeparator(), 'XmlPort.RecordSeparator() default must be empty');
    end;

    [Test]
    procedure TableSeparator_RoundTrip()
    begin
        Assert.IsTrue(Src.CallTableSeparator(), 'XmlPort.TableSeparator(set) must be a no-op');
    end;

    [Test]
    procedure TableSeparator_Get_ReturnsDefault()
    begin
        Assert.AreEqual('', Src.GetTableSeparator(), 'XmlPort.TableSeparator() default must be empty');
    end;

    [Test]
    procedure TextEncoding_IsNoOp()
    begin
        Assert.IsTrue(Src.CallTextEncoding(), 'XmlPort.TextEncoding(set) must be a no-op');
    end;
}
