codeunit 59822 "TaskScheduler Helper"
{
    procedure CreateTaskReturnsGuid(): Guid
    begin
        exit(TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true));
    end;

    procedure TaskExistsReturnsBool(): Boolean
    var
        TaskId: Guid;
    begin
        TaskId := TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true);
        exit(TaskScheduler.TaskExists(TaskId));
    end;

    procedure CancelTaskNoError()
    var
        TaskId: Guid;
    begin
        TaskId := TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true);
        TaskScheduler.CancelTask(TaskId);
    end;

    procedure SetTaskReadyNoError()
    var
        TaskId: Guid;
    begin
        TaskId := TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true);
        TaskScheduler.SetTaskReady(TaskId);
    end;
}
