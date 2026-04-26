/// Tests for CopyArray with 3 arguments (no count parameter).
/// Regression for issue #1155: CS7036 — required parameter 'count' missing.
codeunit 99601 "CA3 Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "CA3 Src";

    // ── Positive: copy from mid-array ─────────────────────────────────────────

    [Test]
    procedure CopyArray3Arg_FromIndex3_CopiesRemainingElements()
    var
        Src: array[5] of Integer;
        Dest: array[5] of Integer;
    begin
        // Src = [10, 20, 30, 40, 50]; FromIndex = 3 → copies elements 30, 40, 50
        Src[1] := 10;
        Src[2] := 20;
        Src[3] := 30;
        Src[4] := 40;
        Src[5] := 50;
        H.CopyFromIndex(Src, 3, Dest);
        Assert.AreEqual(30, Dest[1], 'Dest[1] must be Src[3]=30');
        Assert.AreEqual(40, Dest[2], 'Dest[2] must be Src[4]=40');
        Assert.AreEqual(50, Dest[3], 'Dest[3] must be Src[5]=50');
    end;

    // ── Positive: copy from index 1 copies everything ─────────────────────────

    [Test]
    procedure CopyArray3Arg_FromIndex1_CopiesAllElements()
    var
        Src: array[5] of Integer;
        Dest: array[5] of Integer;
    begin
        Src[1] := 1;
        Src[2] := 2;
        Src[3] := 3;
        Src[4] := 4;
        Src[5] := 5;
        H.CopyAll(Src, Dest);
        Assert.AreEqual(1, Dest[1], 'Dest[1] must be 1');
        Assert.AreEqual(2, Dest[2], 'Dest[2] must be 2');
        Assert.AreEqual(3, Dest[3], 'Dest[3] must be 3');
        Assert.AreEqual(4, Dest[4], 'Dest[4] must be 4');
        Assert.AreEqual(5, Dest[5], 'Dest[5] must be 5');
    end;

    // ── Negative: elements before FromIndex are not copied ────────────────────

    [Test]
    procedure CopyArray3Arg_FromIndex3_EarlyElementsNotInDest()
    var
        Src: array[5] of Integer;
        Dest: array[5] of Integer;
    begin
        Src[1] := 10;
        Src[2] := 20;
        Src[3] := 30;
        Src[4] := 40;
        Src[5] := 50;
        H.CopyFromIndex(Src, 3, Dest);
        // Dest[4] and Dest[5] should be default (0) — only 3 elements copied
        Assert.AreEqual(0, Dest[4], 'Dest[4] must be default 0 — only 3 elements were copied');
        Assert.AreEqual(0, Dest[5], 'Dest[5] must be default 0 — only 3 elements were copied');
    end;

    // ── Negative: 3-arg result differs from 4-arg with partial count ──────────

    [Test]
    procedure CopyArray3Arg_VsPartial4Arg_DifferentResults()
    var
        Src: array[5] of Integer;
        Dest3: array[5] of Integer;
        Dest4: array[5] of Integer;
    begin
        // CopyArray(Dest, Src, 2) copies 4 elements: Src[2..5]
        // CopyArray(Dest, Src, 2, 2) copies only 2 elements: Src[2..3]
        Src[1] := 100;
        Src[2] := 200;
        Src[3] := 300;
        Src[4] := 400;
        Src[5] := 500;
        CopyArray(Dest3, Src, 2);
        CopyArray(Dest4, Src, 2, 2);
        Assert.AreEqual(200, Dest3[1], '3-arg Dest3[1] = Src[2]');
        Assert.AreEqual(500, Dest3[4], '3-arg Dest3[4] = Src[5]');
        Assert.AreEqual(200, Dest4[1], '4-arg Dest4[1] = Src[2]');
        Assert.AreEqual(0, Dest4[4], '4-arg with count=2: Dest4[4] must be default 0');
    end;
}
