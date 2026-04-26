/// Exercises the 2-arg AL StopSession(SessionId, Comment) overload — transpiles
/// to ALStopSession(DataError, int sessionId, string comment) in C#.
codeunit 311443 "StopSession Comment Src"
{
    procedure DoStopSessionWithComment(SessionId: Integer; Comment: Text)
    begin
        StopSession(SessionId, Comment);
    end;

    procedure DoStopSession(SessionId: Integer)
    begin
        StopSession(SessionId);
    end;
}
