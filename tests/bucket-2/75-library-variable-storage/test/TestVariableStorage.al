codeunit 50700 "Test Variable Storage"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        LibraryVariableStorage: Codeunit "Library - Variable Storage";

    [Test]
    procedure EnqueueDequeueText()
    var
        Result: Text;
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue('Hello');
        Result := LibraryVariableStorage.DequeueText();
        Assert.AreEqual('Hello', Result, 'DequeueText should return enqueued text');
    end;

    [Test]
    procedure EnqueueDequeueMultipleValues()
    var
        TextResult: Text;
        IntResult: Integer;
        BoolResult: Boolean;
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue('First');
        LibraryVariableStorage.Enqueue(42);
        LibraryVariableStorage.Enqueue(true);

        TextResult := LibraryVariableStorage.DequeueText();
        Assert.AreEqual('First', TextResult, 'First dequeue should return text');

        IntResult := LibraryVariableStorage.DequeueInteger();
        Assert.AreEqual(42, IntResult, 'Second dequeue should return integer');

        BoolResult := LibraryVariableStorage.DequeueBoolean();
        Assert.IsTrue(BoolResult, 'Third dequeue should return true');
    end;

    [Test]
    procedure AssertEmptyAfterDequeue()
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue('Only');
        LibraryVariableStorage.DequeueText();
        LibraryVariableStorage.AssertEmpty();
    end;

    [Test]
    procedure DequeueOnEmptyQueueErrors()
    begin
        LibraryVariableStorage.Clear();
        asserterror LibraryVariableStorage.DequeueText();
        Assert.ExpectedError('Queue is empty');
    end;

    [Test]
    procedure EnqueueDequeueDecimal()
    var
        Result: Decimal;
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue(3.14);
        Result := LibraryVariableStorage.DequeueDecimal();
        Assert.AreEqual(3.14, Result, 'DequeueDecimal should return enqueued decimal');
    end;

    [Test]
    procedure EnqueueDequeueDate()
    var
        Result: Date;
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue(20250101D);
        Result := LibraryVariableStorage.DequeueDate();
        Assert.AreEqual(20250101D, Result, 'DequeueDate should return enqueued date');
    end;

    [Test]
    procedure IsEmptyReturnsTrue()
    begin
        LibraryVariableStorage.Clear();
        Assert.IsTrue(LibraryVariableStorage.IsEmpty(), 'IsEmpty should return true on empty queue');
    end;

    [Test]
    procedure IsEmptyReturnsFalse()
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue('something');
        Assert.IsFalse(LibraryVariableStorage.IsEmpty(), 'IsEmpty should return false when queue has items');
    end;

    [Test]
    procedure AssertEmptyOnNonEmptyErrors()
    begin
        LibraryVariableStorage.Clear();
        LibraryVariableStorage.Enqueue('leftover');
        asserterror LibraryVariableStorage.AssertEmpty();
        Assert.ExpectedError('Queue is not empty');
    end;
}
