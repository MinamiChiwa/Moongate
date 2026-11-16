namespace MabiTool.Modules.Rooms.Domain;

public sealed class UnavailableInputExecutor : IInputExecutor
{
    public InputPreparation Prepare(ScheduledAction action) => new(false, "input.bindingUnavailable");
    public InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken) =>
        new("rejected", null, false, "input.bindingUnavailable");
    public void ReleaseAll() { }
}
