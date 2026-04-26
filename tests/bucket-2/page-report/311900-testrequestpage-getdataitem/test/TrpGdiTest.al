/// Tests for issue #1457: TestRequestPage.GetDataItem missing on MockTestPageHandle.
/// BC generates tP.Target.GetDataItem("Customer") when AL code accesses a report
/// data item (e.g. RequestPage.Customer) from inside a RequestPageHandler.
/// The Mock must expose GetDataItem(string) returning an object that supports
/// ALSetFilter / ALGetFilter so that the generated C# compiles and runs.
codeunit 99801 "TRP GDI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TRP GDI Src";
        GotFilter: Text;
        GotFilter2: Text;

    // ── SetFilter / GetFilter round-trip via data item property ───────────────
    [Test]
    [HandlerFunctions('SetFilter_Handler')]
    procedure GetDataItem_SetFilter_Roundtrips()
    begin
        // Positive: accessing a data item and calling SetFilter on it must store
        // the filter so GetFilter returns the exact value.
        // A no-op stub that discards SetFilter would return '' instead of '10..20'.
        Src.RunReport();
        Assert.AreEqual('10..20', GotFilter, 'Data item SetFilter/GetFilter round-trip must preserve the filter value');
    end;

    [RequestPageHandler]
    procedure SetFilter_Handler(var RequestPage: TestRequestPage "TRP GDI Report")
    begin
        RequestPage.Customer.SetFilter(Id, '10..20');
        GotFilter := RequestPage.Customer.GetFilter(Id);
    end;

    [Test]
    [HandlerFunctions('SetFilter_NotDefault_Handler')]
    procedure GetDataItem_GetFilter_NotDefaultWhenSet()
    begin
        // Negative: proves the mock is not a no-op that always returns ''.
        // If GetFilter always returned '' this assertion would fail.
        Src.RunReport();
        Assert.AreNotEqual('', GotFilter2, 'GetFilter must return the value that was set, not the default empty string');
    end;

    [RequestPageHandler]
    procedure SetFilter_NotDefault_Handler(var RequestPage: TestRequestPage "TRP GDI Report")
    begin
        RequestPage.Customer.SetFilter(Id, '>=5');
        GotFilter2 := RequestPage.Customer.GetFilter(Id);
    end;

    // ── Nested data item ───────────────────────────────────────────────────────
    [Test]
    [HandlerFunctions('NestedDataItem_Handler')]
    procedure GetDataItem_NestedDataItem_DoesNotCrash()
    begin
        // Positive: accessing a nested data item compiles and runs without error.
        Src.RunReport();
        Assert.IsTrue(true, 'Nested data item access must not crash');
    end;

    [RequestPageHandler]
    procedure NestedDataItem_Handler(var RequestPage: TestRequestPage "TRP GDI Report")
    begin
        RequestPage.Entry.SetFilter(CustId, '42');
    end;

    // ── Two data items are independent ────────────────────────────────────────
    [Test]
    [HandlerFunctions('TwoDataItems_Handler')]
    procedure GetDataItem_TwoDataItems_AreIndependent()
    begin
        // Positive: filters set on two different data items do not bleed over.
        Src.RunReport();
        Assert.AreEqual('A', GotFilter, 'Customer data item filter must equal the value set for Customer');
        Assert.AreEqual('B', GotFilter2, 'Entry data item filter must equal the value set for Entry');
    end;

    [RequestPageHandler]
    procedure TwoDataItems_Handler(var RequestPage: TestRequestPage "TRP GDI Report")
    begin
        RequestPage.Customer.SetFilter(Id, 'A');
        RequestPage.Entry.SetFilter(CustId, 'B');
        GotFilter := RequestPage.Customer.GetFilter(Id);
        GotFilter2 := RequestPage.Entry.GetFilter(CustId);
    end;
}
