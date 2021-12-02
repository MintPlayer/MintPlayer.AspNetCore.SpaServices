using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SpaServices;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Prerendering;

namespace MintPlayer.AspNetCore.SpaServices.Routing
{
    public static class SpaRouteExtensions
    {
        public static IServiceCollection AddSpaPrerenderingService<TService>(this IServiceCollection services) where TService : class, ISpaPrerenderingService
        {
            return services
                .AddSingleton<ISpaRouteService, SpaRouteService>()
                .AddSingleton<ISpaPrerenderingService, TService>();
        }

        public static void UseSpaPrerenderingService(this ISpaBuilder spaBuilder, Action<SpaPrerenderingOptions> options)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            spaBuilder.UseSpaPrerendering(op =>
            {
                var spaPrerenderingService = spaBuilder.ApplicationBuilder.ApplicationServices.GetService<ISpaPrerenderingService>();
                op.SupplyData = async (context, data) =>
                {
                    await spaPrerenderingService.OnSupplyData(context, data);
                };
                options(op);
            });
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }
}
