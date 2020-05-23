using Demo.Data.Dal.Services;
using Demo.Data.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.SpaServices.AngularCli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.SpaServices.Routing;
using System.Linq;

namespace Demo.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddControllersWithViews();

            services
                .AddDemo(options =>
                {
                    options.ConnectionString = Configuration.GetConnectionString("Demo");
                })
                .AddSpaStaticFiles(configuration =>
                {
                    configuration.RootPath = "ClientApp/dist";
                });

            // Define the SPA-routes for our helper
            services.AddSpaRoutes(routes => routes
                .Route("", "home")
                .Group("person", "person", person_routes => person_routes
                    .Route("", "list")
                    .Route("create", "create")
                    .Route("{personid}", "show")
                    .Route("{personid}/edit", "edit")
                )
            );

            services
                .Configure<RazorViewEngineOptions>(options =>
                {
                    var new_locations = options.ViewLocationFormats.Select(vlf => $"/Server{vlf}").ToList();
                    options.ViewLocationFormats.Clear();
                    foreach (var format in new_locations)
                        options.ViewLocationFormats.Add(format);
                });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ISpaRouteService currentSpaRoute)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            if (!env.IsDevelopment())
            {
                app.UseSpaStaticFiles();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller}/{action=Index}/{id?}");
            });

            app.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501

                spa.Options.SourcePath = "ClientApp";

#pragma warning disable CS0618 // Type or member is obsolete
                spa.UseSpaPrerendering(options =>
                {
                    options.BootModulePath = $"{spa.Options.SourcePath}/dist/ClientApp/server/main.js";
                    options.BootModuleBuilder = env.IsDevelopment()
                        ? new AngularCliBuilder(npmScript: "build:ssr")
                        : null;

                    options.ExcludeUrls = new[] { "/sockjs-node" };

                    options.SupplyData = (context, data) =>
                    {
                        var route = currentSpaRoute.GetCurrentRoute(context);

                        var personService = context.RequestServices.GetRequiredService<IPersonService>();

                        switch (route?.Name)
                        {
                            case "manage-members-person-list":
                                {
                                    var people = personService.GetPeople();
                                    data["people"] = people;
                                }
                                break;
                            case "manage-members-person-show":
                            case "manage-members-person-edit":
                                {
                                    var id = System.Convert.ToInt32(route.Parameters["personid"]);
                                    var person = personService.GetPerson(id);
                                    data["person"] = person;
                                }
                                break;
                        }

                        data.Add("message", "Message from server");
                    };
                });
#pragma warning restore CS0618 // Type or member is obsolete

                if (env.IsDevelopment())
                {
                    spa.UseAngularCliServer(npmScript: "start");
                }
            });
        }
    }
}
