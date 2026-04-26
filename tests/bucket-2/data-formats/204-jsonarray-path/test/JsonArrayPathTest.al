codeunit 60321 "JAP Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JAP Src";

    [Test]
    procedure Path_RootArray_IsDollar()
    begin
        // JSONPath notation: root-level container path is "$".
        Assert.AreEqual('$', Src.PathOfRootArray(),
            'JsonArray.Path at root must return "$" (JSONPath root)');
    end;

    [Test]
    procedure Path_Nested_ContainsKey()
    begin
        // JSONPath notation: "$.items" for an array under key "items".
        Assert.AreEqual('$.items', Src.PathOfNestedArray(),
            'JsonArray.Path for a nested array must be "$.items"');
    end;

    [Test]
    procedure Clone_IsIndependent()
    begin
        Assert.IsTrue(Src.CloneIsIndependent(),
            'Clone must produce an independent copy — mutations must not propagate');
    end;

    [Test]
    procedure AsToken_RoundTrip_PreservesCount()
    begin
        Assert.AreEqual(2, Src.AsTokenRoundTrip(),
            'AsToken().AsArray().Count must equal the original count');
    end;

    [Test]
    procedure WriteTo_ContainsValues()
    begin
        Assert.IsTrue(Src.WriteToContainsValues(),
            'WriteTo must serialise the array values to text');
    end;
}
