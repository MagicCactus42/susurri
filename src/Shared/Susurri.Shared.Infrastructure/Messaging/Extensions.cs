using Microsoft.Extensions.DependencyInjection;
using Susurri.Shared.Abstractions.Messaging;
using Susurri.Shared.Infrastructure.Messaging.Brokers;

namespace Susurri.Shared.Infrastructure.Messaging;

internal static class Extensions
{
    internal static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<IMessageBroker, InMemoryMessageBroker>();
        
        return services;
    }
}