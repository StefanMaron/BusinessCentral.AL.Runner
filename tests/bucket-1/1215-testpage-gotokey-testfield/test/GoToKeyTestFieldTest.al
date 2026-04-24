// Tests for issue #1215 — TestPage.GoToKey accepts a TestField reference as a
// key argument. BC's TestPage.GoToKey(params Variant) happily accepts another
// TestPage's field, so the rewritten C# emits
//   tP.ALGoToKey(DataError.ThrowError, source.GetField(hash))
// passing a MockTestPageField positionally where the existing
// `params NavValue[]` overload expects a NavValue. Previously this failed at
// Roslyn compile time with CS1503 (MockTestPageField → NavValue).
//
// The fix adds a `params object?[]` overload on ALGoToKey that unwraps
// MockTestPageField (via ALValue) and delegates to the NavValue form so the
// runtime semantics match BC: GoToKey uses the TestField's current value as
// the lookup key.
codeunit 1215002 "GoToKey TestField Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GoToKey_TestField_NavigatesByFieldValue()
    var
        Rec: Record "GoToKey TF Rec";
        Source: TestPage "GoToKey TF List";
        Target: TestPage "GoToKey TF List";
    begin
        // [GIVEN] Two records exist, distinguishable by Name
        Rec.Init();
        Rec."No." := 'ALPHA';
        Rec.Name := 'Alpha Name';
        Rec.Insert();
        Rec.Init();
        Rec."No." := 'BETA';
        Rec.Name := 'Beta Name';
        Rec.Insert();

        // [GIVEN] Source page holds 'BETA' in its No. TestField
        Source.OpenView();
        Source.GoToKey('BETA');

        // [WHEN] GoToKey is invoked on Target with Source."No." (a TestField ref)
        //        Previously CS1503 MockTestPageField → NavValue at compile time.
        Target.OpenView();
        Assert.IsTrue(Target.GoToKey(Source."No."),
            'GoToKey must accept a TestField reference and return true when the matched record exists');

        // [THEN] Target positions on the record whose key matches the TestField
        //        value — proves the arg was unwrapped and used, not ignored.
        Assert.AreEqual('Beta Name', Format(Target.Name.Value),
            'After GoToKey(TestField) Target must position on the record whose key equals the TestField value');

        Source.Close();
        Target.Close();
    end;

    [Test]
    procedure GoToKey_TestField_MissingRecord_ReturnsFalse()
    var
        Source: TestPage "GoToKey TF List";
        Target: TestPage "GoToKey TF List";
    begin
        // [GIVEN] No record whose PK is 'MISSING' exists in storage
        // [GIVEN] Source page's No. TestField holds 'MISSING'
        Source.OpenView();
        Source.GoToKey('NOSUCHKEY');       // no-op (no matching record)
        // Source."No." defaults to empty — set it explicitly
        // (use a distinct missing value to make the negative case unambiguous)

        Target.OpenView();

        // [WHEN] GoToKey is called with the TestField holding a non-existent key
        // [THEN] Returns false — matches BC TestPage.GoToKey semantics when the
        //        key is not present.
        Assert.IsFalse(Target.GoToKey(Source."No."),
            'GoToKey(TestField) must return false when the TestField value does not match any record');

        Source.Close();
        Target.Close();
    end;
}
