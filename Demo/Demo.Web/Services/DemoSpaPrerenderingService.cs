using Demo.Data.Dal.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Routing;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Demo.Web.Services
{
    public class DemoSpaPrerenderingService : MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService
    {
        private readonly ISpaRouteService spaRouteService;
        private readonly IPersonService personService;
        public DemoSpaPrerenderingService(ISpaRouteService spaRouteService, IPersonService personService)
        {
            this.spaRouteService = spaRouteService;
            this.personService = personService;
        }

        public Task BuildRoutes(MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaRouteBuilder routeBuilder)
        {
            routeBuilder
               .Route("", "home")
               .Group("person", "person", person_routes => person_routes
                   .Route("", "list")
                   .Route("create", "create")
                   .Route("{personid}", "show")
                   .Route("{personid}/edit", "edit")
               );

            return Task.CompletedTask;
        }

        public async Task OnSupplyData(HttpContext context, IDictionary<string, object> data)
        {
            var route = await spaRouteService.GetCurrentRoute(context);
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
