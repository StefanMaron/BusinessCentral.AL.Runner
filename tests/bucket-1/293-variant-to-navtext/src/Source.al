/// Source for Variant-to-Text NavIndirectValueToNavValue tests.
/// BC emits ALCompiler.NavIndirectValueToNavValue<NavText>(variant, metadata)
/// when assigning a Variant to a Text variable. The 2-arg overload was not
/// previously handled, causing Roslyn compilation error CS1501.
codeunit 293001 "VNT Variant NavText Helper"
{
    /// Assign a Boolean wrapped in Variant to a Text variable.
    /// BC emits NavIndirectValueToNavValue<NavText>(v, metadata).
    procedure BoolVariantToText(Flag: Boolean): Text
    var
        V: Variant;
        T: Text;
    begin
        V := Flag;
        T := V;
        exit(T);
    end;

    /// Assign an Integer wrapped in Variant to a Text variable.
    procedure IntVariantToText(N: Integer): Text
    var
        V: Variant;
        T: Text;
    begin
        V := N;
        T := V;
        exit(T);
    end;

    /// Assign a Text wrapped in Variant to a Text variable (round-trip).
    procedure TextVariantToText(S: Text): Text
    var
        V: Variant;
        T: Text;
    begin
        V := S;
        T := V;
        exit(T);
    end;
}
