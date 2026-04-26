using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests that MockRecordHandle.ALTestFieldNavValueSafe has an object-accepting overload
/// so that BC compiler output of ALTestFieldNavValueSafe(fieldNo, navType, objectValue)
/// does not produce CS1503 ('object' cannot be converted to 'NavValue').
///
/// Some BC compiler versions emit ALTestFieldNavValueSafe with an object-typed value
/// for Rec.TestField("Table No.") and similar calls inside table procedures.
/// Without the object overload, Roslyn compilation fails with CS1503 (13× per the telemetry).
///
/// Issue: #1324
/// </summary>
public class TestFieldNavValueObjectOverloadTests
{
    private MockRecordHandle CreateHandle()
    {
        var handle = new MockRecordHandle(99910);
        MockRecordHandle.RegisterPrimaryKey(99910, 1);
        MockRecordHandle.RegisterFieldName(99910, "No.", 1);
        MockRecordHandle.RegisterFieldName(99910, "TableNo", 2);
        MockRecordHandle.RegisterFieldName(99910, "Name", 3);
        return handle;
    }

    // -----------------------------------------------------------------------
    // ALTestFieldNavValueSafe — object overload, positive cases
    // -----------------------------------------------------------------------

    [Fact]
    public void ALTestFieldNavValueSafe_Object_IntegerMatch_DoesNotThrow()
    {
        // [GIVEN] Record with TableNo = 42
        var rec = CreateHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("A"));
        rec.SetFieldValueSafe(2, NavType.Integer, NavInteger.Create(42));
        rec.ALInsert(DataError.ThrowError);

        // [WHEN/THEN] ALTestFieldNavValueSafe(object) with matching integer value must not throw.
        // This mirrors the CS1503 scenario where the BC transpiler passes an object-typed value.
        object expectedValue = 42;
        rec.ALTestFieldNavValueSafe(2, NavType.Integer, expectedValue);
    }

    [Fact]
    public void ALTestFieldNavValueSafe_Object_TextMatch_DoesNotThrow()
    {
        // [GIVEN] Record with Name = 'Widget'
        var rec = CreateHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("B"));
        rec.SetFieldValueSafe(3, NavType.Text, new NavText("Widget"));
        rec.ALInsert(DataError.ThrowError);

        // [WHEN/THEN] ALTestFieldNavValueSafe(object) with matching text must not throw.
        object expectedValue = "Widget";
        rec.ALTestFieldNavValueSafe(3, NavType.Text, expectedValue);
    }

    [Fact]
    public void ALTestFieldNavValueSafe_Object_NavValuePassThrough_DoesNotThrow()
    {
        // [GIVEN] Record with TableNo = 7
        var rec = CreateHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("C"));
        rec.SetFieldValueSafe(2, NavType.Integer, NavInteger.Create(7));
        rec.ALInsert(DataError.ThrowError);

        // [WHEN/THEN] When the object IS a NavValue, it must be handled transparently.
        object expectedValue = NavInteger.Create(7);
        rec.ALTestFieldNavValueSafe(2, NavType.Integer, expectedValue);
    }

    // -----------------------------------------------------------------------
    // ALTestFieldNavValueSafe — object overload, negative cases
    // -----------------------------------------------------------------------

    [Fact]
    public void ALTestFieldNavValueSafe_Object_IntegerMismatch_Throws()
    {
        // [GIVEN] Record with TableNo = 99
        var rec = CreateHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("D"));
        rec.SetFieldValueSafe(2, NavType.Integer, NavInteger.Create(99));
        rec.ALInsert(DataError.ThrowError);

        // [WHEN/THEN] Mismatch must throw with the expected error.
        object wrongValue = 1;
        var ex = Assert.Throws<Exception>(() => rec.ALTestFieldNavValueSafe(2, NavType.Integer, wrongValue));
        Assert.Contains("expected '1' but was '99'", ex.Message);
    }

    [Fact]
    public void ALTestFieldNavValueSafe_Object_TextMismatch_Throws()
    {
        // [GIVEN] Record with Name = 'Widget'
        var rec = CreateHandle();
        rec.SetFieldValueSafe(1, NavType.Code, new NavText("E"));
        rec.SetFieldValueSafe(3, NavType.Text, new NavText("Widget"));
        rec.ALInsert(DataError.ThrowError);

        // [WHEN/THEN] Mismatch must throw with the expected error.
        object wrongValue = "Gadget";
        var ex = Assert.Throws<Exception>(() => rec.ALTestFieldNavValueSafe(3, NavType.Text, wrongValue));
        Assert.Contains("expected 'Gadget' but was 'Widget'", ex.Message);
    }
}
