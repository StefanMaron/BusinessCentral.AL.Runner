using AlRunner.Runtime;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests that MockRecordHandle methods which take int fieldNo as their first
/// parameter also accept a DataError-prefixed overload.
///
/// BC compiler versions may emit DataError as the first argument to field-level
/// operations (SetFieldValueSafe, GetFieldValueSafe, GetFieldRefSafe,
/// ALSetRangeSafe, ALSetFilter, ALModifyAllSafe) that previously only accepted
/// int. Without DataError overloads these cause CS1503 at Roslyn compilation.
///
/// Issue: #1267
/// </summary>
public class DataErrorToIntOverloadTests
{
    private MockRecordHandle CreateHandle()
    {
        var handle = new MockRecordHandle(99900);
        MockRecordHandle.RegisterPrimaryKey(99900, 1);
        MockRecordHandle.RegisterFieldName(99900, "EntryNo", 1);
        MockRecordHandle.RegisterFieldName(99900, "Name", 2);
        MockRecordHandle.RegisterFieldName(99900, "Amount", 3);
        return handle;
    }

    // -----------------------------------------------------------------------
    // SetFieldValueSafe DataError overloads
    // -----------------------------------------------------------------------

    [Fact]
    public void SetFieldValueSafe_DataError_3arg_SetsValue()
    {
        var rec = CreateHandle();
        // Use the existing non-DataError overload to set PK
        rec.SetFieldValueSafe(1, NavType.Integer, NavInteger.Default);

        // Call the DataError overload — must compile and set the value
        rec.SetFieldValueSafe(DataError.ThrowError, 2, NavType.Text, new NavText("hello"));

        var result = rec.GetFieldValueSafe(2, NavType.Text);
        Assert.Equal("hello", ((NavText)result).Value);
    }

    [Fact]
    public void SetFieldValueSafe_DataError_4arg_SetsValueWithValidate()
    {
        var rec = CreateHandle();
        rec.SetFieldValueSafe(1, NavType.Integer, NavInteger.Default);

        rec.SetFieldValueSafe(DataError.ThrowError, 2, NavType.Text, new NavText("world"), false);

        var result = rec.GetFieldValueSafe(2, NavType.Text);
        Assert.Equal("world", ((NavText)result).Value);
    }

    // -----------------------------------------------------------------------
    // GetFieldValueSafe DataError overloads
    // -----------------------------------------------------------------------

    [Fact]
    public void GetFieldValueSafe_DataError_2arg_ReturnsValue()
    {
        var rec = CreateHandle();
        rec.SetFieldValueSafe(2, NavType.Text, new NavText("test"));

        var result = rec.GetFieldValueSafe(DataError.ThrowError, 2, NavType.Text);
        Assert.Equal("test", ((NavText)result).Value);
    }

    [Fact]
    public void GetFieldValueSafe_DataError_3arg_WithLocale_ReturnsValue()
    {
        var rec = CreateHandle();
        rec.SetFieldValueSafe(2, NavType.Text, new NavText("locale"));

        var result = rec.GetFieldValueSafe(DataError.ThrowError, 2, NavType.Text, false);
        Assert.Equal("locale", ((NavText)result).Value);
    }

    // -----------------------------------------------------------------------
    // GetFieldRefSafe DataError overload
    // -----------------------------------------------------------------------

    [Fact]
    public void GetFieldRefSafe_DataError_ReturnsValue()
    {
        var rec = CreateHandle();
        rec.SetFieldValueSafe(2, NavType.Text, new NavText("ref"));

        var result = rec.GetFieldRefSafe(DataError.ThrowError, 2, NavType.Text);
        Assert.Equal("ref", ((NavText)result).Value);
    }

    // -----------------------------------------------------------------------
    // ALSetRangeSafe DataError overloads
    // -----------------------------------------------------------------------

    [Fact]
    public void ALSetRangeSafe_DataError_ClearRange_NoThrow()
    {
        var rec = CreateHandle();
        rec.ALSetRangeSafe(2, NavType.Text, new NavText("x"));
        // Clear range via DataError overload — must not throw
        rec.ALSetRangeSafe(DataError.ThrowError, 2, NavType.Text);
    }

    [Fact]
    public void ALSetRangeSafe_DataError_SingleValue_NoThrow()
    {
        var rec = CreateHandle();
        rec.ALSetRangeSafe(DataError.ThrowError, 2, NavType.Text, new NavText("filtered"));
    }

    [Fact]
    public void ALSetRangeSafe_DataError_FromToRange_NoThrow()
    {
        var rec = CreateHandle();
        rec.ALSetRangeSafe(DataError.ThrowError, 2, NavType.Text,
            new NavText("A"), new NavText("Z"));
    }

    // -----------------------------------------------------------------------
    // ALSetFilter DataError overloads
    // -----------------------------------------------------------------------

    [Fact]
    public void ALSetFilter_DataError_StringExpr_NoThrow()
    {
        var rec = CreateHandle();
        rec.ALSetFilter(DataError.ThrowError, 2, "<>''");
    }

    [Fact]
    public void ALSetFilter_DataError_WithNavType_NoThrow()
    {
        var rec = CreateHandle();
        rec.ALSetFilter(DataError.ThrowError, 2, NavType.Text, "<>''");
    }

    // -----------------------------------------------------------------------
    // ALModifyAllSafe DataError overloads
    // -----------------------------------------------------------------------

    private MockRecordHandle CreateHandleForModifyAll(int tableId)
    {
        var handle = new MockRecordHandle(tableId);
        MockRecordHandle.RegisterPrimaryKey(tableId, 1);
        MockRecordHandle.RegisterFieldName(tableId, "EntryNo", 1);
        MockRecordHandle.RegisterFieldName(tableId, "Name", 2);
        return handle;
    }

    [Fact]
    public void ALModifyAllSafe_DataError_3arg_UpdatesValue()
    {
        var rec = CreateHandleForModifyAll(99901);
        rec.SetFieldValueSafe(1, NavType.Integer, NavInteger.Default);
        rec.SetFieldValueSafe(2, NavType.Text, new NavText("original"));
        rec.ALInsert(DataError.ThrowError);

        rec.ALModifyAllSafe(DataError.ThrowError, 2, NavType.Text, new NavText("updated"));

        rec.ALFindFirst();
        var result = rec.GetFieldValueSafe(2, NavType.Text);
        Assert.Equal("updated", ((NavText)result).Value);
    }

    [Fact]
    public void ALModifyAllSafe_DataError_4arg_UpdatesValue()
    {
        var rec = CreateHandleForModifyAll(99902);
        rec.SetFieldValueSafe(1, NavType.Integer, NavInteger.Default);
        rec.SetFieldValueSafe(2, NavType.Text, new NavText("original"));
        rec.ALInsert(DataError.ThrowError);

        rec.ALModifyAllSafe(DataError.ThrowError, 2, NavType.Text, new NavText("triggered"), false);

        rec.ALFindFirst();
        var result = rec.GetFieldValueSafe(2, NavType.Text);
        Assert.Equal("triggered", ((NavText)result).Value);
    }
}
