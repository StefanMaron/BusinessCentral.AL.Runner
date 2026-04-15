codeunit 59980 "Notification Sender"
{
    procedure SendSimple(Msg: Text)
    var
        N: Notification;
    begin
        N.Message := Msg;
        N.Send();
    end;

    procedure SendWithData(Msg: Text; DataKey: Text; DataValue: Text)
    var
        N: Notification;
    begin
        N.Message := Msg;
        N.SetData(DataKey, DataValue);
        N.Send();
    end;
}
