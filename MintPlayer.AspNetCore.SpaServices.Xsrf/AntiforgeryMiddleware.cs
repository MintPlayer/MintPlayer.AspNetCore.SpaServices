using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.AspNetCore.SpaServices.Xsrf;

// You may need to install the Microsoft.AspNetCore.Http.Abstractions package into your project
internal partial class Antiforgery
{
	[Inject] private readonly RequestDelegate next;
	[Inject] private readonly IAntiforgery antiforgery;

	public async Task Invoke(HttpContext httpContext)
	{
		httpContext.Response.OnStarting((state) =>
		{
			var context = (HttpContext)state;
			//if (string.Equals(httpContext.Request.Path.Value, "/", StringComparison.OrdinalIgnoreCase))
			//{
			var tokens = antiforgery.GetAndStoreTokens(httpContext);
			httpContext.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken, new CookieOptions() { Path = "/", HttpOnly = false });
			//}
			return Task.CompletedTask;
		}, httpContext);

		await next(httpContext);
	}
}

// Extension method used to add the middleware to the HTTP request pipeline.
public static class AntiforgeryExtensions
{
	/// <summary>
	/// Adds a middleware to the http-request pipeline that generates an XSRF-token for the current user
	/// and stores it in a cookie named "XSRF-TOKEN".
	/// </summary>
	public static IApplicationBuilder UseAntiforgeryGenerator(this IApplicationBuilder builder)
	{
		return builder.UseMiddleware<Antiforgery>();
	}
}
