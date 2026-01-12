using Susurri.Shared.Abstractions.Messaging;

namespace Susurri.Shared.Infrastructure.Messaging.Dispatchers;

internal sealed class AsyncMessageDispatcher : IAsyncMessageDispatcher
{
    private readonly IMessageChannel _channel;

    public AsyncMessageDispatcher(IMessageChannel channel)
    {
        _channel = channel;
    }

    public async Task PublishAsync<TMessage>(TMessage message) where TMessage : class, IMessage
        => await _channel.Writer.WriteAsync(message);
}