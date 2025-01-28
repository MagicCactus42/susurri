using System.Threading.Channels;
using Susurri.Shared.Abstractions.Messaging;

namespace Susurri.Shared.Infrastructure.Messaging.Dispatchers;

public interface IMessageChannel
{
    ChannelReader<IMessage> Reader { get; }
    ChannelWriter<IMessage> Writer { get; }
}