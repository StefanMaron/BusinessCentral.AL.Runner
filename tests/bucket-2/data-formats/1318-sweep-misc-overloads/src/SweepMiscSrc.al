/// Source helpers for miscellaneous not-tested overloads sweep (issue #1400).
/// Covers:
///   TextBuilder.Replace (Text, Text, Integer, Integer)
/// Note: TextBuilder.ToText(Integer, Integer) is NOT covered — NavTextBuilder is used
/// directly (not proxied via MockTextBuilder) so the runner cannot intercept the
/// substring-extraction overload. Filed as a separate runner-gap issue.
///   IsolatedStorage.Get (Text, DataScope, Text)
///   IsolatedStorage.Get (Text, Text)
///   FileUpload.CreateInStream (InStream, TextEncoding)
///   File.UploadIntoStream (Text, Text, Text, Text, InStream)  — no-op stub
///   RecordRef.Field (Text)
///   RecordRef.FindSet (Boolean, Boolean)
///   RecordRef.CopyLinks (Table)
///   RecordRef.Open (Text, Boolean, Text)
codeunit 1318000 "Sweep Misc Src"
{
    // ── TextBuilder.ToText (Integer, Integer) ─────────────────────────────────

    procedure TextBuilder_ToText_StartCount(content: Text; startIdx: Integer; charCount: Integer): Text
    var
        TB: TextBuilder;
    begin
        TB.Append(content);
        exit(TB.ToText(startIdx, charCount));
    end;

    // ── TextBuilder.Replace (Text, Text, Integer, Integer) ───────────────────

    procedure TextBuilder_Replace_Range(content: Text; oldVal: Text; newVal: Text; startIdx: Integer; charCount: Integer): Text
    var
        TB: TextBuilder;
    begin
        TB.Append(content);
        TB.Replace(oldVal, newVal, startIdx, charCount);
        exit(TB.ToText());
    end;

    // ── IsolatedStorage.Get (Text, DataScope, Text) ──────────────────────────

    procedure IsoStorage_Set_Get_DataScope(propKey: Text; value: Text; scope: DataScope): Text
    var
        outValue: Text;
    begin
        IsolatedStorage.Set(propKey, value, scope);
        IsolatedStorage.Get(propKey, scope, outValue);
        exit(outValue);
    end;

    // ── IsolatedStorage.Get (Text, Text) ─────────────────────────────────────

    procedure IsoStorage_Set_Get_2Arg(propKey: Text; value: Text): Text
    var
        outValue: Text;
    begin
        IsolatedStorage.Set(propKey, value);
        IsolatedStorage.Get(propKey, outValue);
        exit(outValue);
    end;

    // ── IsolatedStorage.Contains (Text, DataScope, Boolean) ──────────────────
    // The 3rd arg may be a "companySpecific" bool or an out-bool depending on BC version.
    // The implementation delegates to the DataScope overload.

    procedure IsoStorage_Contains_DataScope(propKey: Text; scope: DataScope): Boolean
    begin
        IsolatedStorage.Set(propKey, 'v', scope);
        exit(IsolatedStorage.Contains(propKey, scope));
    end;

    // ── FileUpload.CreateInStream (InStream, TextEncoding) ───────────────────

    procedure FileUpload_CreateInStream_WithEncoding_NoThrow(): Boolean
    var
        Upload: FileUpload;
        InStr: InStream;
    begin
        Upload.CreateInStream(InStr, TextEncoding::UTF8);
        exit(true);
    end;

    // ── RecordRef.Field (Text) ───────────────────────────────────────────────

    procedure RecordRef_Field_ByName(tableId: Integer; fieldName: Text): Text
    var
        RR: RecordRef;
        FR: FieldRef;
    begin
        RR.Open(tableId);
        FR := RR.Field(fieldName);
        exit(FR.Name);
    end;

    // ── RecordRef.FindSet (Boolean, Boolean) ─────────────────────────────────

    procedure RecordRef_FindSet_TwoArg(tableId: Integer): Boolean
    var
        RR: RecordRef;
    begin
        RR.Open(tableId);
        exit(RR.FindSet(false, false));
    end;

    // ── RecordRef.CopyLinks (Table) ──────────────────────────────────────────
    // CopyLinks copies the record links from one record to another.
    // In standalone mode it's a no-op stub.

    procedure RecordRef_CopyLinks_Table_NoThrow(): Boolean
    var
        Rec: Record "SMO Rec";
        RR: RecordRef;
    begin
        Rec.Init();
        Rec.Id := 1;
        RR.GetTable(Rec);
        RR.CopyLinks(Rec);
        exit(true);
    end;

    // ── HttpHeaders.Add (Text, Text) / TryAddWithoutValidation ──────────────

    procedure HttpHeaders_Add_Text_NoThrow(): Boolean
    var
        Req: HttpRequestMessage;
        Headers: HttpHeaders;
    begin
        Req.GetHeaders(Headers);
        Headers.Add('X-Custom', 'value');
        exit(true);
    end;

    procedure HttpHeaders_TryAddWithoutValidation_NoThrow(): Boolean
    var
        Req: HttpRequestMessage;
        Headers: HttpHeaders;
    begin
        Req.GetHeaders(Headers);
        Headers.TryAddWithoutValidation('X-Custom', 'value');
        exit(true);
    end;
}

table 1318000 "SMO Rec"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
