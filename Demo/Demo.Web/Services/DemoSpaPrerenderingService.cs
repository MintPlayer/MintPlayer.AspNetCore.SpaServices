using Demo.Data.Dal.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Routing;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Demo.Web.Services
{
    public class DemoSpaPrerenderingService : ISpaPrerenderingService
    {
        private readonly ISpaRouteService spaRouteService;
        public DemoSpaPrerenderingService(ISpaRouteService spaRouteService)
        {
            this.spaRouteService = spaRouteService;
        }

        public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
        {
            var route = spaRouteService.GetCurrentRoute(context);
            var personService = context.RequestServices.GetRequiredService<IPersonService>();
            switch (route?.Name)
            {
                case "person-list":
                    {
                        var people = await personService.GetPeople();
                        data["people"] = people;
                    }
                    break;
                case "person-show":
                case "person-edit":
                    {
                        var id = System.Convert.ToInt32(route.Parameters["personid"]);
                        var person = await personService.GetPerson(id);
                        data["person"] = person;
                    }
                    break;
            }

            data.Add("message", "Message from server");
        }
    }
}
