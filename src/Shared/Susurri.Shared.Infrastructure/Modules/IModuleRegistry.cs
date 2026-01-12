namespace Susurri.Shared.Infrastructure.Modules;

public interface IModuleRegistry
{
    IEnumerable<ModuleBroadcastRegistration> GetModuleBroadcastRegistrations(string key);
    void AddBroadcastAction(Type requestType, Func<object, Task> action);
}

public sealed class ModuleBroadcastRegistration
{
    public Type ReceiverType { get; }
    public Func<object, Task> Action { get; }
    public string Key => ReceiverType.Name;

    public ModuleBroadcastRegistration(Type receiverType, Func<object, Task> action)
    {
        ReceiverType = receiverType;
        Action = action;
    }
}