using Microsoft.Extensions.DependencyInjection;
using Susurri.Modules.IAM.Core;

namespace Susurri.Modules.IAM.Application;

public static class IamServiceRegistration
{
    public static IServiceCollection AddIam(this IServiceCollection services) => services.AddCore();
}
