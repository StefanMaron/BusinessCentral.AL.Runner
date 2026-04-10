codeunit 50100 "Hello"
{
    trigger OnRun()
    var
        MyTable: Record MyTable;
    begin
        MyTable.MyField := 123;
        MyTable.Insert();
        MyTable.FindFirst();
        Message('Hello, world! MyField value is: %1', MyTable.MyField);
    end;
}
table 50100 MyTable
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; MyField; Integer)
        {
            DataClassification = ToBeClassified;

        }
    }

    keys
    {
        key(Key1; MyField)
        {
            Clustered = true;
        }
    }
}
