codeunit 50121 "Error Producer"
{
    procedure RaiseCustomerNoError()
    begin
        Error('The field Customer No. must have a value');
    end;

    procedure RaiseAmountError()
    begin
        Error('The Amount must be greater than 0 for document type Invoice');
    end;
}
