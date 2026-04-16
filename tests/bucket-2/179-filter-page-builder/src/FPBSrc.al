/// Helper codeunit exercising FilterPageBuilder in standalone mode.
codeunit 97200 "FPB Src"
{
    /// Add one table and return the count.
    procedure AddTableAndCount(): Integer
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('Items', 27);
        exit(FPB.Count);
    end;

    /// Add two tables and return the count.
    procedure AddTwoTablesCount(): Integer
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('Items', 27);
        FPB.AddTable('Customers', 18);
        exit(FPB.Count);
    end;

    /// Set a view on a named table and retrieve it.
    procedure SetAndGetView(ViewText: Text): Text
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('Items', 27);
        FPB.SetView('Items', ViewText);
        exit(FPB.GetView('Items'));
    end;

    /// Return the Name (caption) at the given 1-based index.
    procedure NameAtIndex(Index: Integer): Text
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('Alpha', 27);
        FPB.AddTable('Beta', 18);
        exit(FPB.Name(Index));
    end;

    /// RunModal — returns the action result (Action::OK in standalone).
    procedure RunModalResult(): Action
    var
        FPB: FilterPageBuilder;
    begin
        FPB.AddTable('Items', 27);
        exit(FPB.RunModal());
    end;

    /// PageCaption — set and get the dialog caption.
    procedure SetAndGetPageCaption(Caption: Text): Text
    var
        FPB: FilterPageBuilder;
    begin
        FPB.PageCaption := Caption;
        exit(FPB.PageCaption);
    end;

    /// Proving helper: returns a constant to verify codeunit is live.
    procedure Ping(): Integer
    begin
        exit(42);
    end;
}
