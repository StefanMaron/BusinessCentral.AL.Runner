/// <summary>
/// Tests that Clear() on an interface variable compiles and runs.
/// BC lowers Clear(IfaceVar) to IfaceVar.ClearReference() — this requires
/// MockInterfaceHandle.ClearReference() which was missing (issue #1565).
/// </summary>
codeunit 1900006 "IC Interface Clear Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ClearInterfaceVar_NoThrow()
    var
        SourceList: Interface "IC Source List";
        ImplA: Codeunit "IC Source List Impl A";
    begin
        // [GIVEN] An interface variable assigned to an implementation
        SourceList := ImplA;

        // [WHEN] Clear() is called on the interface variable — BC emits ClearReference()
        Clear(SourceList);

        // [THEN] No exception is thrown (Clear is always a no-op / reset to default)
    end;

    [Test]
    procedure ClearInterfaceVar_ReassignAfterClear()
    var
        SourceList: Interface "IC Source List";
        ImplA: Codeunit "IC Source List Impl A";
        ImplB: Codeunit "IC Source List Impl B";
        Count: Integer;
    begin
        // [GIVEN] An interface variable assigned to ImplA, then cleared, then reassigned to ImplB
        SourceList := ImplA;
        Clear(SourceList);
        SourceList := ImplB;

        // [WHEN] A method is called on the reassigned interface variable
        Count := SourceList.GetCount();

        // [THEN] The result is from ImplB (99), not ImplA (42)
        Assert.AreEqual(99, Count, 'After Clear and reassign to ImplB, GetCount must return 99');
    end;

    [Test]
    procedure ClearInterfaceVar_MultipleClears()
    var
        SourceList: Interface "IC Source List";
        ImplA: Codeunit "IC Source List Impl A";
        Name: Text;
    begin
        // [GIVEN] An interface variable that is assigned, cleared, reassigned, and cleared again
        SourceList := ImplA;
        Clear(SourceList);
        SourceList := ImplA;
        Clear(SourceList);
        SourceList := ImplA;

        // [WHEN] A method is called after the final assignment
        Name := SourceList.GetName();

        // [THEN] The result is from ImplA
        Assert.AreEqual('ImplA', Name, 'After multiple clears and final assign to ImplA, GetName must return ImplA');
    end;
}
