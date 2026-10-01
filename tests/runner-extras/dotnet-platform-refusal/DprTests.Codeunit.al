// Issue #3222 -- System.Drawing reached through a DotNet MEMBER call, not a constructor.
//
// #3212 named the refusal for construction only. These tests pin the other two shapes: an
// instance call on an object whose constructor succeeded (Microsoft's own QR-code provider,
// reached through the System Application exactly as the #3222 report did) and a static call.
// On a Windows host both succeed, so none of this is a claim about BC -- it is the runner's
// own refusal contract (docs/scope.md#dotnet-platform).
codeunit 66501 "Dpr Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dpr Assert";

    [Test]
    procedure QrCodeInstanceCall_IsRefusedByName()
    var
        Provider: Interface "Barcode Image Provider 2D";
        Image: Codeunit "Temp Blob";
    begin
        // QRCodeProvider's constructor succeeds; GetBarcodeStream is the instance call that
        // reaches System.Drawing (the #3222 stack).
        Provider := Enum::"Barcode Image Provider 2D"::Dynamics2D;
        asserterror Image := Provider.EncodeImage('example', Enum::"Barcode Symbology 2D"::"QR-Code");

        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('dotnet-platform-unsupported');
        Assert.ExpectedError('GetBarcodeStream');
        Assert.ExpectedError('System.Drawing.Common');
        Assert.NotExpectedError('The type initializer for ''Gdip''');
    end;

    [Test]
    procedure StaticDrawingCall_IsRefusedByName()
    var
        TempBlob: Codeunit "Temp Blob";
        OutStr: OutStream;
        InStr: InStream;
        DrawingImage: DotNet DprDrawingImage;
    begin
        TempBlob.CreateOutStream(OutStr);
        OutStr.WriteText('not an image');
        TempBlob.CreateInStream(InStr);

        asserterror DrawingImage := DrawingImage.FromStream(InStr);

        Assert.ExpectedError('out-of-scope:');
        Assert.ExpectedError('dotnet-platform-unsupported');
        Assert.ExpectedError('FromStream');
        Assert.ExpectedError('System.Drawing.Common');
        Assert.NotExpectedError('The type initializer for ''Gdip''');
    end;

    [Test]
    procedure NonPlatformMemberFailure_KeepsBcsOwnError()
    var
        Convert: DotNet DprConvert;
        Value: Integer;
    begin
        // Control: a member call that fails for an ordinary reason must keep BC's own
        // NavNCLDotNetInvokeException text. Without this, refusing EVERY failed DotNet invoke
        // by name would pass both tests above.
        asserterror Value := Convert.ToInt32('not a number');

        Assert.ExpectedError('ToInt32');
        Assert.NotExpectedError('out-of-scope:');
    end;

    [Test]
    procedure SuccessfulMemberCall_IsUnaffected()
    var
        Convert: DotNet DprConvert;
        Value: Integer;
    begin
        // Control: the prologue sits on the failure path only; a member call that succeeds
        // returns its real value.
        Value := Convert.ToInt32('42');
        if Value <> 42 then
            Error('Convert.ToInt32(''42'') returned %1, expected 42', Value);
    end;
}
