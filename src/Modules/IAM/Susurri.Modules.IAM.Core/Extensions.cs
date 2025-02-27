using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Crypto;
using Susurri.Modules.IAM.Core.Keys;


[assembly: InternalsVisibleTo("Susurri.Modules.IAM.Application")]
namespace Susurri.Modules.IAM.Core;

internal static class Extensions
{
    public static IServiceCollection AddCore(this IServiceCollection services)
        => services
            .AddSingleton<IInMemoryCredentialsCache, InMemoryCredentialsCache>()
            .AddSingleton<ICredentialsCache, CredentialsCache>()
            .AddSingleton<IKeyStorage, KeyStorage>()
            .AddSingleton<IKeyGenerator, KeyGenerator>()
            .AddSingleton<ICryptoKeyGenerator, CryptoKeyGenerator>()
            .AddSingleton<IOnionCrypto, OnionCrypto>();
}