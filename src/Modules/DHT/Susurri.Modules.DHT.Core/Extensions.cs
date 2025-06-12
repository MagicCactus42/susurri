using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("Susurri.Modules.DHT.Application")]
namespace Susurri.Modules.DHT.Core;

internal static class Extensions
{
    public static IServiceCollection AddCore(this IServiceCollection services)
        => services;
}