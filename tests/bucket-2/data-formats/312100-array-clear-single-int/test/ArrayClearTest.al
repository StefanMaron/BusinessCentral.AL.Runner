/// Tests for Clear(arr[i]) on arrays of complex types — issue #1448.
codeunit 312101 "ACSI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Src: Codeunit "ACSI Src";

    [Test]
    procedure ClearNodeListArrayElement_Slot2Intact()
    begin
        // POSITIVE: after Clear(NodeListArr[1]), slot [2] must report 3 children —
        // proving Clear(arr[i]) both compiles (no CS1503) and only targets slot [1].
        Assert.AreEqual(3, Src.ClearNodeListArrayElement(), 'Slot 2 count should be 3 after clearing slot 1');
    end;

    [Test]
    procedure ClearNodeListArrayElement_OtherSlotUnaffected()
    begin
        // POSITIVE: slot [2] must still have 2 children after slot [1] was cleared
        Assert.AreEqual(2, Src.NodeListSlot2UnaffectedBySlot1Clear(), 'Slot 2 count must be unaffected');
    end;
}
