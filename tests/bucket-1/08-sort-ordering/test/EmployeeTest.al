codeunit 50908 "Employee Sort Tests"
{
    Subtype = Test;

    var
        EmpMgmt: Codeunit "Employee Management";
        Assert: Codeunit Assert;

    [Test]
    procedure TestSortBySalaryAscending()
    var
        Employee: Record "Test Employee";
        FirstName: Text[100];
    begin
        // [GIVEN] Employees inserted in random order
        CreateEmployee('EMP-03', 'Charlie', 75000, 'SALES');
        CreateEmployee('EMP-01', 'Alice', 50000, 'DEV');
        CreateEmployee('EMP-02', 'Bob', 90000, 'MGMT');

        // [WHEN] Finding with sort by salary ascending
        Employee.SetCurrentKey("Salary");
        Employee.SetAscending("Salary", true);
        Employee.FindFirst();

        // [THEN] First record should be the lowest salary
        Assert.AreEqual('Alice', Employee."Name", 'Lowest salary should be Alice at 50000');
    end;

    [Test]
    procedure TestSortBySalaryDescending()
    var
        Employee: Record "Test Employee";
    begin
        // [GIVEN] Employees inserted in random order
        CreateEmployee('EMP-13', 'Charlie', 75000, 'SALES');
        CreateEmployee('EMP-11', 'Alice', 50000, 'DEV');
        CreateEmployee('EMP-12', 'Bob', 90000, 'MGMT');

        // [WHEN] Getting highest paid via management codeunit
        // [THEN] Should return Bob
        Assert.AreEqual('Bob', EmpMgmt.GetHighestPaidName(), 'Highest paid should be Bob at 90000');
    end;

    [Test]
    procedure TestSortByNameAlphabetical()
    var
        Names: Text[1024];
    begin
        // [GIVEN] Employees inserted out of alphabetical order
        CreateEmployee('EMP-23', 'Zara', 60000, 'HR');
        CreateEmployee('EMP-21', 'Anna', 55000, 'DEV');
        CreateEmployee('EMP-22', 'Mike', 70000, 'SALES');

        // [WHEN] Getting names in alphabetical order
        Names := EmpMgmt.GetNamesInAlphabeticalOrder();

        // [THEN] Should be alphabetically sorted
        Assert.AreEqual('Anna,Mike,Zara', Names, 'Names should be sorted alphabetically');
    end;

    [Test]
    procedure TestSortWithFindSetIteration()
    var
        Employee: Record "Test Employee";
        PrevSalary: Integer;
    begin
        // [GIVEN] Multiple employees
        CreateEmployee('EMP-33', 'Dave', 80000, 'DEV');
        CreateEmployee('EMP-31', 'Eve', 45000, 'HR');
        CreateEmployee('EMP-34', 'Frank', 95000, 'MGMT');
        CreateEmployee('EMP-32', 'Grace', 60000, 'SALES');

        // [WHEN] Iterating with SetCurrentKey(Salary) ascending
        Employee.SetCurrentKey("Salary");
        Employee.SetAscending("Salary", true);
        Employee.FindSet();

        // [THEN] Each salary should be >= previous
        PrevSalary := Employee."Salary";
        Assert.AreEqual(45000, PrevSalary, 'First should be lowest salary 45000');
        Employee.Next();
        Assert.AreEqual(60000, Employee."Salary", 'Second should be 60000');
        Employee.Next();
        Assert.AreEqual(80000, Employee."Salary", 'Third should be 80000');
        Employee.Next();
        Assert.AreEqual(95000, Employee."Salary", 'Fourth should be 95000');
    end;

    local procedure CreateEmployee(EmpNo: Code[20]; EmpName: Text[100]; Salary: Integer; Dept: Code[20])
    var
        Employee: Record "Test Employee";
    begin
        Employee.Init();
        Employee."No." := EmpNo;
        Employee."Name" := EmpName;
        Employee."Salary" := Salary;
        Employee."Department" := Dept;
        Employee.Insert(true);
    end;
}
