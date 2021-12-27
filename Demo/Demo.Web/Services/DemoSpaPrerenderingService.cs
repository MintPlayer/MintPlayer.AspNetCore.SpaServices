using Demo.Data.Dal.Services;
using Demo.Web.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Routing;
using System;
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
                   .Route("{personid}/{name}", "show-name")
                   .Route("{personid}/{name}/edit", "edit-name")
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
                        var personid = Convert.ToInt32(route.Parameters["personid"]);
                        var person = await personService.GetPerson(personid, false);
                        if (person == null)
                        {
                            context.Response.OnStarting(() =>
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                return Task.CompletedTask;
                            });
                        }
                        else
                        {
                            context.Response.OnStarting(async () =>
                            {
                                var url = await spaRouteService.GenerateUrl($"{route.Name}-name", new { personid = personid, name = (person.FirstName + " " + person.LastName).Slugify() });
                                context.Response.Redirect(url);
                            });
                        }
                    }
                    break;
                case "person-show-name":
                case "person-edit-name":
                    {
                        var personid = Convert.ToInt32(route.Parameters["personid"]);
                        var person = await personService.GetPerson(personid);
                        if (person == null)
                        {
                            context.Response.OnStarting(() =>
                            {
                                context.Response.StatusCode = StatusCodes.Status404NotFound;
                                return Task.CompletedTask;
                            });
                        }
                        else if (route.Parameters["name"] == (person.FirstName + " " + person.LastName).Slugify())
                        {
                            data["person"] = person;
                        }
                        else
                        {
                            var url = await spaRouteService.GenerateUrl(route.Name, new { personid = personid, name = (person.FirstName + " " + person.LastName).Slugify() });
                            context.Response.Redirect(url);
                        }
                    }
                    break;
            }

            data.Add("message", "Message from server");
        }
    }
}
