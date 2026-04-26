/// Helper that uses a case statement on a Code[10] parameter.
/// BC codegen for Code comparisons in case statements calls into
/// NavEnvironment-dependent methods, which the runner must intercept.
// Renumbered from 59200 to avoid collision in new bucket layout (#1385).
codeunit 1059200 "CCT Helper"
{
    procedure CategoryLabel(Category: Code[10]): Text
    begin
        case Category of
            'A':
                exit('Premium');
            'B':
                exit('Standard');
            else
                exit('Other');
        end;
    end;

    procedure StatusCode(Status: Code[20]): Integer
    begin
        case Status of
            'OPEN':
                exit(1);
            'CLOSED':
                exit(2);
            'PENDING':
                exit(3);
            else
                exit(0);
        end;
    end;
}
