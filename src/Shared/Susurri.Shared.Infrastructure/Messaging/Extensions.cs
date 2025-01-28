using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Infrastructure.Messaging.Brokers;
using Susurri.Shared.Infrastructure.Messaging.Dispatchers;

namespace Susurri.Shared.Infrastructure.Messaging;

internal static class Extensions
{
    private const string SectionName = "messaging";
    internal static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<IMessageBroker, InMemoryMessageBroker>();
        services.AddSingleton<IMessageChannel, MessageChannel>();
        services.AddSingleton<IAsyncMessageDispatcher, AsyncMessageDispatcher>();
        
        var messagingOptions = services.GetOptions<MessagingOptions>(SectionName);
        services.AddSingleton<MessagingOptions>();

        if (messagingOptions.UseBackgroundDispatcher)
        {
            services.AddHostedService<BackgroundDispatcher>();
        }
        
        return services;
    }
}