codeunit 50108 "Employee Management"
{
    procedure GetHighestPaidName(): Text[100]
    var
        Employee: Record "Test Employee";
    begin
        Employee.SetCurrentKey("Salary");
        Employee.SetAscending("Salary", false);
        if Employee.FindFirst() then
            exit(Employee."Name");
        exit('');
    end;

    procedure GetLowestPaidName(): Text[100]
    var
        Employee: Record "Test Employee";
    begin
        Employee.SetCurrentKey("Salary");
        Employee.SetAscending("Salary", true);
        if Employee.FindFirst() then
            exit(Employee."Name");
        exit('');
    end;

    procedure GetNamesInAlphabeticalOrder(): Text[1024]
    var
        Employee: Record "Test Employee";
        Result: Text[1024];
    begin
        Employee.SetCurrentKey("Name");
        if Employee.FindSet() then
            repeat
                if Result <> '' then
                    Result += ',';
                Result += Employee."Name";
            until Employee.Next() = 0;
        exit(Result);
    end;
}
