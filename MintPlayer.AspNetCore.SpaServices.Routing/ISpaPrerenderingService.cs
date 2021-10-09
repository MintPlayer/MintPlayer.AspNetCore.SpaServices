using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace MintPlayer.AspNetCore.SpaServices.Routing
{
    public interface ISpaPrerenderingService
    {
        Task OnSupplyData(HttpContext httpContext, IDictionary<string, object> data);
    }
}
