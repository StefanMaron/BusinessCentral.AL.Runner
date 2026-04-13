codeunit 79101 StreamSurfaceTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit StreamSurfaceHelper;

    [Test]
    procedure TestOutStreamAssign()
    var
        Rec: Record StreamSurfaceTable;
        Result: Text;
    begin
        // [GIVEN] a record with a BLOB field
        Rec.Init();
        Rec.Id := 1;
        Rec.Insert();

        // [WHEN] writing text via an assigned OutStream (OutStr2 := OutStr1)
        Result := Helper.WriteViaAssignedOutStream(Rec, 'AssignedOut');

        // [THEN] the text is readable back
        Assert.AreEqual('AssignedOut', Result, 'OutStream assign should propagate writes');
    end;

    [Test]
    procedure TestInStreamAssign()
    var
        Rec: Record StreamSurfaceTable;
        OStr: OutStream;
        Result: Text;
    begin
        // [GIVEN] a record with 'AssignedIn' written to BLOB
        Rec.Init();
        Rec.Id := 2;
        Rec.Insert();
        Rec.Data.CreateOutStream(OStr);
        OStr.WriteText('AssignedIn');

        // [WHEN] reading via an assigned InStream (InStr2 := InStr1)
        Result := Helper.ReadViaAssignedInStream(Rec);

        // [THEN] the text is read correctly
        Assert.AreEqual('AssignedIn', Result, 'InStream assign should propagate reads');
    end;

    [Test]
    procedure TestCopyStream()
    var
        SrcRec: Record StreamSurfaceTable;
        DstRec: Record StreamSurfaceTable;
        OStr: OutStream;
        IStr: InStream;
        Result: Text;
    begin
        // [GIVEN] a source record with data
        SrcRec.Init();
        SrcRec.Id := 3;
        SrcRec.Insert();
        SrcRec.Data.CreateOutStream(OStr);
        OStr.WriteText('CopiedData');

        DstRec.Init();
        DstRec.Id := 4;
        DstRec.Insert();

        // [WHEN] copying the BLOB with COPYSTREAM
        Helper.CopyBlobData(SrcRec, DstRec);

        // [THEN] destination contains the same data
        DstRec.Data.CreateInStream(IStr);
        IStr.ReadText(Result);
        Assert.AreEqual('CopiedData', Result, 'COPYSTREAM should copy all bytes');
    end;

    [Test]
    procedure TestWriteReadInteger()
    var
        Rec: Record StreamSurfaceTable;
        Result: Integer;
    begin
        Rec.Init();
        Rec.Id := 5;
        Rec.Insert();

        // [WHEN] writing and reading an Integer via binary stream
        Result := Helper.WriteReadInteger(Rec, 42);

        // [THEN] round-trip is correct
        Assert.AreEqual(42, Result, 'Integer binary round-trip failed');
    end;

    [Test]
    procedure TestWriteReadIntegerNegative()
    var
        Rec: Record StreamSurfaceTable;
        Result: Integer;
    begin
        Rec.Init();
        Rec.Id := 6;
        Rec.Insert();

        Result := Helper.WriteReadInteger(Rec, -1234);
        Assert.AreEqual(-1234, Result, 'Negative integer binary round-trip failed');
    end;

    [Test]
    procedure TestWriteReadBooleanTrue()
    var
        Rec: Record StreamSurfaceTable;
        Result: Boolean;
    begin
        Rec.Init();
        Rec.Id := 7;
        Rec.Insert();

        Result := Helper.WriteReadBoolean(Rec, true);
        Assert.IsTrue(Result, 'Boolean true round-trip failed');
    end;

    [Test]
    procedure TestWriteReadBooleanFalse()
    var
        Rec: Record StreamSurfaceTable;
        Result: Boolean;
    begin
        Rec.Init();
        Rec.Id := 8;
        Rec.Insert();

        Result := Helper.WriteReadBoolean(Rec, false);
        Assert.IsFalse(Result, 'Boolean false round-trip failed');
    end;

    [Test]
    procedure TestWriteReadDecimal()
    var
        Rec: Record StreamSurfaceTable;
        Result: Decimal;
    begin
        Rec.Init();
        Rec.Id := 9;
        Rec.Insert();

        Result := Helper.WriteReadDecimal(Rec, 3.14);
        Assert.AreEqual(3.14, Result, 'Decimal binary round-trip failed');
    end;

    [Test]
    procedure TestWriteReadViaParam()
    var
        Rec: Record StreamSurfaceTable;
        Result: Text;
    begin
        Rec.Init();
        Rec.Id := 10;
        Rec.Insert();

        Result := Helper.WriteReadViaParam(Rec, 'ParamTest');
        Assert.AreEqual('ParamTest', Result, 'Stream var param round-trip failed');
    end;

    [Test]
    procedure TestEOSAfterRead()
    var
        Rec: Record StreamSurfaceTable;
        OStr: OutStream;
        IsEOS: Boolean;
    begin
        Rec.Init();
        Rec.Id := 11;
        Rec.Insert();
        Rec.Data.CreateOutStream(OStr);
        OStr.WriteText('EOSTest');

        IsEOS := Helper.IsEOSAfterRead(Rec);
        Assert.IsTrue(IsEOS, 'EOS should be true after reading all data');
    end;
}
