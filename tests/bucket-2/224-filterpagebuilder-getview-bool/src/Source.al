// Source helpers for issue #xxx — FilterPageBuilder.GetView(caption, useNames: Boolean).
//
// BC emits filterPageBuilder.ALGetView(NavText, bool) for the two-argument overload
// FilterPageBuilder.GetView(caption, useNames). The existing mock only accepts
// ALGetView(string) and ALGetView(DataError, string), causing CS1503 on compile.
// The fix adds the (NavText/string, bool) overloads.
codeunit 62232 "FPBGetViewSrc"
{
    /// Registers a table, sets a view, and retrieves it using the two-arg GetView form.
    /// Mimics the PermissionHelper.GetCustomFilter pattern.
    procedure RoundTripView(TableId: Integer; FilterIn: Text[2048]) Filter: Text[2048]
    var
        FilterPageBuilder: FilterPageBuilder;
        CaptionTok: Label 'Items', Locked = true;
        TempText: Text;
    begin
        FilterPageBuilder.AddTable(CaptionTok, TableId);

        if FilterIn <> '' then
            FilterPageBuilder.SetView(CaptionTok, FilterIn);

        // The two-arg GetView(caption, useNames) form — BC emits ALGetView(NavText, bool).
        // This is the exact pattern from PermissionHelper.GetCustomFilter line ~82.
        if FilterPageBuilder.RunModal() then begin
            TempText := FilterPageBuilder.GetView(CaptionTok, false);
            if StrLen(TempText) <= 2048 then
                Filter := CopyStr(TempText, 1, 2048);
        end;
    end;

    /// Returns the view via one-arg GetView to confirm the two-arg variant doesn't break
    /// the existing zero-or-one-arg behaviour.
    procedure GetViewOneArg(TableId: Integer; ViewIn: Text): Text
    var
        FilterPageBuilder: FilterPageBuilder;
        CaptionTok: Label 'Payments', Locked = true;
    begin
        FilterPageBuilder.AddTable(CaptionTok, TableId);
        if ViewIn <> '' then
            FilterPageBuilder.SetView(CaptionTok, ViewIn);
        exit(FilterPageBuilder.GetView(CaptionTok));
    end;
}
