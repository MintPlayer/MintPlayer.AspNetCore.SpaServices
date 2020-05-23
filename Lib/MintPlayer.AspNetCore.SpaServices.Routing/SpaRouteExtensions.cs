using System;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.AspNetCore.SpaServices.Routing
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
