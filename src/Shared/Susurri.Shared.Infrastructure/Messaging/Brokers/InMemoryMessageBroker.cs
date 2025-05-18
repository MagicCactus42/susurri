using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Abstractions.Modules;

namespace Susurri.Shared.Infrastructure.Messaging.Brokers;

internal sealed class InMemoryMessageBroker : IMessageBroker
{
    private readonly IModuleClient _moduleClient;

    public InMemoryMessageBroker(IModuleClient moduleClient)
    {
        _moduleClient = moduleClient;
    }

    public async Task PublishAsync(params IMessage[] messages)
    {
        messages = messages.Where(x => x is not null).ToArray();

        if (!messages.Any())
        {
            return;
        }

        var tasks = new List<Task>();
        
        foreach (var message in messages)
        {
         tasks.Add(_moduleClient.PublishAsync(message));   
        }
        
        await Task.WhenAll(tasks);
    }
}