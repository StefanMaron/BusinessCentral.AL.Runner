/// <summary>
/// Source helpers exercising the IConvertible code path.
/// The BC transpiler emits ALCompiler.NavIndirectValueToInt32 / NavIndirectValueToBoolean
/// when a Variant (holding a Record) is assigned to an Integer or Boolean local.
/// Without IConvertible on MockRecordHandle these calls throw
/// "Unable to cast MockRecordHandle to IConvertible".
/// </summary>

table 235010 "RIC Table"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 235011 "RIC Helper"
{
    /// <summary>
    /// Extract an Integer and a Boolean from a Variant that holds a Record.
    /// The BC transpiler emits NavIndirectValueToInt32 / NavIndirectValueToBoolean
    /// which internally call Convert.ToInt32 / Convert.ToBoolean — both require
    /// IConvertible on the wrapped value.
    /// </summary>
    procedure VariantRecordToIntBool(V: Variant; var ResultInt: Integer; var ResultBool: Boolean)
    begin
        ResultInt := V;
        ResultBool := V;
    end;

    /// <summary>
    /// Format a Record held inside a Variant.
    /// NavFormatEvaluateHelper.Format(session, variant) → AlCompat.Format(variant).
    /// AlCompat.Format unwraps the MockVariant and calls MockRecordHandle.ToString()
    /// which is backed by ALGetPosition(); result must be non-empty.
    /// </summary>
    procedure FormatVariantRecord(V: Variant): Text
    begin
        exit(Format(V));
    end;

    /// <summary>
    /// Assign a Record directly to a Variant and convert it to Text via Format.
    /// Exercises the full round-trip: Record → Variant assignment → Format → Text.
    /// </summary>
    procedure RecordToVariantToText(Rec: Record "RIC Table"): Text
    var
        V: Variant;
    begin
        V := Rec;
        exit(Format(V));
    end;
}
