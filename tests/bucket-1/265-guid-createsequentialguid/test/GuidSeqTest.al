codeunit 82101 "Guid Seq Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: CreateSequentialGuid returns a non-null GUID.
    // ------------------------------------------------------------------

    [Test]
    procedure CreateSequentialGuid_ReturnsNonNull()
    var
        Src: Codeunit "Guid Seq Src";
    begin
        // [GIVEN/WHEN] CreateSequentialGuid() is called
        // [THEN] The returned GUID must not be a null GUID
        Assert.IsTrue(Src.SequentialGuidIsNotNull(), 'CreateSequentialGuid() must return a non-null GUID');
    end;

    [Test]
    procedure CreateSequentialGuid_ReturnsDifferentValues()
    var
        Src: Codeunit "Guid Seq Src";
    begin
        // [GIVEN/WHEN] CreateSequentialGuid() is called twice
        // [THEN] The two returned GUIDs must be distinct
        Assert.IsTrue(Src.TwoSequentialGuidsAreDistinct(), 'Successive calls to CreateSequentialGuid() must return distinct GUIDs');
    end;

    // ------------------------------------------------------------------
    // Negative: result is a valid GUID (non-empty string representation).
    // ------------------------------------------------------------------

    [Test]
    procedure CreateSequentialGuid_ToTextIsNotEmpty()
    var
        Src: Codeunit "Guid Seq Src";
        g: Guid;
        t: Text;
    begin
        // [GIVEN/WHEN] CreateSequentialGuid() is called and converted to text
        g := Src.GetSequentialGuid();
        t := Format(g);
        // [THEN] Text representation must not be empty
        Assert.AreNotEqual('', t, 'CreateSequentialGuid() text representation must not be empty');
    end;
}
