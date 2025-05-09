﻿using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.IAM.Core.Abstractions;
using Susurri.Modules.IAM.Core.Keys;

namespace Susurri.Modules.IAM.Core;

internal static class Extensions
{
    public static IServiceCollection AddCore(this IServiceCollection services) 
        => services.AddSingleton<IKeyGenerator, KeyGenerator>();
}