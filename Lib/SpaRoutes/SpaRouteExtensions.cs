using System;
using Microsoft.Extensions.DependencyInjection;
using Spa.SpaRoutes.CurrentSpaRoute;

namespace Spa.SpaRoutes
{
    public static class SpaRouteExtensions
    {
        public static IServiceCollection AddSpaRoutes(this IServiceCollection services, Action<ISpaRouteBuilder> builder)
        {
            var routes = new SpaRouteBuilder();
            builder(routes);
            services.AddSingleton<ISpaRouteService>(provider => new SpaRouteService(routes));
            return services;
        }
    }
}
