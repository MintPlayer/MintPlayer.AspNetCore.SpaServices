using System;
using Microsoft.Extensions.DependencyInjection;
using Spa.SpaRoutes.Abstractions;
using Spa.SpaRoutes.CurrentSpaRoute.Interfaces;

namespace Spa.SpaRoutes
{
    public static class SpaRouteExtensions
    {
        public static IServiceCollection AddSpaRoutes(this IServiceCollection services, Action<ISpaRouteBuilder> builder)
        {
            var routes = new SpaRouteBuilder();
            builder(routes);
            services.AddSingleton<ICurrentSpaRoute>(provider => new CurrentSpaRoute.CurrentSpaRoute(routes));
            return services;
        }
    }
}
