using Microsoft.AspNetCore.Http;

namespace Spa.SpaRoutes.CurrentSpaRoute.Interfaces
{
    public interface ICurrentSpaRoute
    {
        SpaRoute GetCurrentRoute(HttpContext httpContext);
    }
}
