using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.DHT.Core.Abstractions;
using Susurri.Modules.DHT.Core.Kademlia.Storage;
using Susurri.Modules.DHT.Core.Node;

[assembly: InternalsVisibleTo("Susurri.Modules.DHT.Application")]
namespace Susurri.Modules.DHT.Core;

internal static class Extensions
{
    public static IServiceCollection AddCore(this IServiceCollection services)
        => services
            .AddSingleton<IDhtStorage, DhtStorage>()
            .AddSingleton<INodeClient, NodeClient>();
}