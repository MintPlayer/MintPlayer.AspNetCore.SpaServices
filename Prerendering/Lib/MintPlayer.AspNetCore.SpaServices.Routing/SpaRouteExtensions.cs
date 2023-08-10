using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SpaServices;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Prerendering;

namespace MintPlayer.AspNetCore.SpaServices.Routing;

public static class SpaRouteExtensions
{
	public static IServiceCollection AddSpaPrerenderingService<TService>(this IServiceCollection services) where TService : class, Prerendering.Services.ISpaPrerenderingService
	{
		return services
			.AddSingleton<ISpaRouteService, SpaRouteService>()
			.AddScoped<Prerendering.Services.ISpaPrerenderingService, TService>();
	}
}
