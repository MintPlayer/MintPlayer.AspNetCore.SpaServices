using System;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Routing;

namespace Spa.SpaRoutes
{
    public static class SpaRouteExtensions
    {
        public static IServiceCollection AddSpaRoutes(this IServiceCollection services, Action<ISpaRouteBuilder> builder)
        {
            var routes = new SpaRouteBuilder();
            builder(routes);

            return services
                .AddSingleton<ISpaRouteService>(provider => new SpaRouteService(routes));
        }
    }
}
