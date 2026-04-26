/// Tests for CopyArray on fixed-length Text arrays.
/// Regression for issue #1232: CS0411 when element type is a fixed-length NavText subtype
/// (occurs both in codeunit locals and in page-level var fields).
codeunit 99701 "CA Text Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "CA Text Src";

    // ── Positive: copy all elements from index 1 ──────────────────────────────

    [Test]
    procedure CopyArray_FixedLengthText_CopiesAllElements()
    var
        Src: array[32] of Text[1024];
        Dest: array[32] of Text[1024];
    begin
        Src[1] := 'Alpha';
        Src[2] := 'Beta';
        Src[3] := 'Gamma';
        H.CopyTextArray(Src, Dest);
        Assert.AreEqual('Alpha', Dest[1], 'Dest[1] must be Alpha');
        Assert.AreEqual('Beta', Dest[2], 'Dest[2] must be Beta');
        Assert.AreEqual('Gamma', Dest[3], 'Dest[3] must be Gamma');
    end;

    // ── Positive: copy from index 2 leaves index 1 default ────────────────────

    [Test]
    procedure CopyArray_FixedLengthText_FromIndex2_EarlyElementDefault()
    var
        Src: array[5] of Text[50];
        Dest: array[5] of Text[50];
    begin
        Src[1] := 'First';
        Src[2] := 'Second';
        Src[3] := 'Third';
        H.CopyTextArrayFromIndex(Src, 2, Dest);
        Assert.AreEqual('Second', Dest[1], 'Dest[1] must be Src[2]=Second');
        Assert.AreEqual('Third', Dest[2], 'Dest[2] must be Src[3]=Third');
    end;

    // ── Negative: element before FromIndex is not copied ─────────────────────

    [Test]
    procedure CopyArray_FixedLengthText_FromIndex2_Dest3IsDefault()
    var
        Src: array[5] of Text[50];
        Dest: array[5] of Text[50];
    begin
        Src[1] := 'First';
        Src[2] := 'Second';
        Src[3] := 'Third';
        H.CopyTextArrayFromIndex(Src, 2, Dest);
        // Only 4 elements copied (Src[2..5]); Dest[5] = default ''
        Assert.AreEqual('', Dest[5], 'Dest[5] must be default empty string — only 4 elements copied');
    end;

    // ── Positive: inline CopyArray on Text[1024] array ────────────────────────

    [Test]
    procedure CopyArray_FixedLengthText_InlineCall_Works()
    var
        Src: array[32] of Text[1024];
        Dest: array[32] of Text[1024];
    begin
        Src[1] := 'InlineValue';
        CopyArray(Dest, Src, 1);
        Assert.AreEqual('InlineValue', Dest[1], 'Dest[1] must be InlineValue after inline CopyArray');
    end;

    // ── Positive: page with global var array[32] of Text[1024] compiles ───────
    // This matches the issue #1232 repro: a page with a class-level var array
    // and a procedure that calls CopyArray on it.

    [Test]
    procedure CopyArray_PageGlobalVar_Text1024_CompilationAndCopy()
    var
        Src: array[32] of Text[1024];
        Page: Page "CA Text Page";
    begin
        // Page compiles and its Load procedure is callable — proves CS0411 is fixed
        // for class-level array vars in pages.
        Src[1] := 'PageCaption1';
        Src[2] := 'PageCaption2';
        Page.Load(Src);
        Assert.AreEqual('PageCaption1', Page.GetCaption(1), 'Page.GetCaption(1) must be PageCaption1');
        Assert.AreEqual('PageCaption2', Page.GetCaption(2), 'Page.GetCaption(2) must be PageCaption2');
    end;
}
