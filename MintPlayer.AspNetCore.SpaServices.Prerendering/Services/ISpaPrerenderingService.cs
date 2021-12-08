namespace MintPlayer.AspNetCore.SpaServices.Prerendering.Services
{
    public interface ISpaPrerenderingService
    {
        Task BuildRoutes(ISpaRouteBuilder routeBuilder);
        Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data);
    }
}
