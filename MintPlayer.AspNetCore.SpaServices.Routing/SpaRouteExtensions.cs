using System.Diagnostics.CodeAnalysis;

namespace MintPlayer.AspNetCore.SpaServices.Routing;

public static class SpaRouteExtensions
{
	public static IServiceCollection AddSpaPrerenderingService<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>(this IServiceCollection services)
		where TService : class, Prerendering.Services.ISpaPrerenderingService
	{
		return services
			.AddHttpContextAccessor()
			.AddSpaRouteServices()
			.AddScoped<Prerendering.Services.ISpaPrerenderingService, TService>();
	}
}
