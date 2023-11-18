using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.AspNetCore.XsrfForSpas
{
    // You may need to install the Microsoft.AspNetCore.Http.Abstractions package into your project
    internal class Antiforgery
    {
        private readonly RequestDelegate next;
        private readonly IAntiforgery antiforgery;
        public Antiforgery(RequestDelegate next, IAntiforgery antiforgery)
        {
            this.next = next;
            this.antiforgery = antiforgery;
        }

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
        public static IApplicationBuilder UseAntiforgery(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<Antiforgery>();
        }
    }
}
