codeunit 84406 "TBM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TBM Src";

    // ── Insert ──────────────────────────────────────────────────────────────────
    [Test]
    procedure Insert_AtMiddle_ProducesExpectedString()
    begin
        // Positive: Insert(5, ', World') after 'Hello' gives 'Hello, World'.
        Assert.AreEqual('Hello, World', Src.InsertAtPosition('Hello', 5, ', World'),
            'Insert at position 5 must splice text correctly');
    end;

    [Test]
    procedure Insert_AtZero_PrependsText()
    begin
        // Positive: Insert(0, 'Hi ') before 'there' gives 'Hi there'.
        Assert.AreEqual('Hi there', Src.InsertAtPosition('there', 0, 'Hi '),
            'Insert at position 0 must prepend');
    end;

    [Test]
    procedure Insert_AtEnd_AppendsText()
    begin
        // Positive: Insert(5, '!') at end of 'Hello' gives 'Hello!'.
        Assert.AreEqual('Hello!', Src.InsertAtPosition('Hello', 5, '!'),
            'Insert at length position must append');
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────
    [Test]
    procedure Remove_MiddleRange_ProducesExpectedString()
    begin
        // Positive: Remove(5, 7) from 'Hello, World' gives 'Hello'.
        Assert.AreEqual('Hello', Src.RemoveRange('Hello, World', 5, 7),
            'Remove(5,7) must delete ", World"');
    end;

    [Test]
    procedure Remove_FromStart_ProducesExpectedString()
    begin
        // Positive: Remove(0, 6) from 'Hello World' gives 'World'.
        Assert.AreEqual('World', Src.RemoveRange('Hello World', 0, 6),
            'Remove(0,6) must delete "Hello "');
    end;

    // ── Replace ─────────────────────────────────────────────────────────────────
    [Test]
    procedure Replace_Occurrence_ProducesExpectedString()
    begin
        // Positive: Replace('World', 'BC') in 'Hello World' gives 'Hello BC'.
        Assert.AreEqual('Hello BC', Src.ReplaceText('Hello World', 'World', 'BC'),
            'Replace must substitute matching text');
    end;

    [Test]
    procedure Replace_NoMatch_LeavesStringUnchanged()
    begin
        // Negative: Replace('xyz', 'abc') in 'Hello' leaves 'Hello' unchanged.
        Assert.AreEqual('Hello', Src.ReplaceText('Hello', 'xyz', 'abc'),
            'Replace with no match must leave string unchanged');
    end;

    // ── Length ──────────────────────────────────────────────────────────────────
    [Test]
    procedure Length_ReturnsCharacterCount()
    begin
        // Positive: Length of 'Hello' is 5.
        Assert.AreEqual(5, Src.GetLength('Hello'),
            'Length must return the number of characters');
    end;

    [Test]
    procedure Length_EmptyBuilder_ReturnsZero()
    begin
        // Positive: Length of '' is 0.
        Assert.AreEqual(0, Src.GetLength(''),
            'Length of empty builder must be 0');
    end;

    // ── Clear ───────────────────────────────────────────────────────────────────
    [Test]
    procedure Clear_AfterAppend_ReturnsEmptyString()
    begin
        // Positive: Clear() resets ToText() to ''.
        Assert.AreEqual('', Src.AppendThenClear('Hello'),
            'Clear must reset builder to empty string');
    end;

    [Test]
    procedure Clear_NotEqualToOriginal()
    begin
        // Negative: cleared builder does not return original content.
        Assert.AreNotEqual('Hello', Src.AppendThenClear('Hello'),
            'After Clear, ToText must not return the original text');
    end;

    // ── Capacity ────────────────────────────────────────────────────────────────
    [Test]
    procedure Capacity_AfterAppend_IsPositive()
    begin
        // Positive: Capacity is greater than 0 after appending text.
        Assert.IsTrue(Src.GetCapacity() > 0,
            'Capacity must be positive after appending text');
    end;

    // ── MaxCapacity ─────────────────────────────────────────────────────────────
    [Test]
    procedure MaxCapacity_IsPositive()
    begin
        // Positive: MaxCapacity is a large positive number.
        Assert.IsTrue(Src.GetMaxCapacity() > 0,
            'MaxCapacity must be a positive number');
    end;

    // ── EnsureCapacity ──────────────────────────────────────────────────────────
    [Test]
    procedure EnsureCapacity_AtLeastRequestedSize()
    begin
        // Positive: after EnsureCapacity(100), Capacity >= 100.
        Assert.IsTrue(Src.EnsureAndGetCapacity(100) >= 100,
            'After EnsureCapacity(100), Capacity must be at least 100');
    end;

    [Test]
    procedure EnsureCapacity_WithSmallValue_DoesNotCrash()
    begin
        // Positive: EnsureCapacity(1) does not crash.
        Src.EnsureAndGetCapacity(1);
        Assert.IsTrue(true, 'EnsureCapacity(1) must not crash');
    end;
}
