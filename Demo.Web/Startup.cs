using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.SpaServices.AngularCli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AspNetCoreSpaPrerendering.Data.Extensions;
using Spa.SpaRoutes;
using Spa.SpaRoutes.CurrentSpaRoute;
using AspNetCoreSpaPrerendering.Data.Repositories.Interfaces;
using Microsoft.Extensions.Hosting;

namespace AspNetSpaPrerendering
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
            services.AddServices(options => {
                options.ConnectionString = @"Server=(localdb)\mssqllocaldb;Database=AspNetCoreSpaPrerendering;Trusted_Connection=True;ConnectRetryCount=0";
            });

            services
                .AddControllersWithViews()
                .SetCompatibilityVersion(CompatibilityVersion.Latest);

            services.AddRouting();

            // In production, the Angular files will be served from this directory
            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/dist";
            });

            // Define the SPA-routes for our helper
            services.AddSpaRoutes(routes => routes
                .Route("", "home")
                .Group("manage", "manage", manage_routes => manage_routes
                    .Group("members", "members", member_routes => member_routes
                        .Group("{memberid}/person", "person", person_routes => person_routes
                            .Route("", "list")
                            .Route("create", "create")
                            .Route("{personid}", "show")
                            .Route("{personid}/edit", "edit")
                        )
                    )
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
            }

            app
                .UseHttpsRedirection()
                .UseHsts()
                .UseStaticFiles()
                .UseSpaStaticFiles();

            app.UseRouting();
            app.UseEndpoints(routes =>
            {
                routes.MapControllerRoute(
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
                    options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
                    options.BootModuleBuilder = env.IsDevelopment()
                        ? new AngularCliBuilder(npmScript: "build:ssr")
                        : null;
                    options.ExcludeUrls = new[] { "/sockjs-node" };

                    options.SupplyData = (context, data) =>
                    {
                        var route = currentSpaRoute.GetCurrentRoute();
                        
                        var personRepository = context.RequestServices.GetRequiredService<IPersonRepository>();

                        switch (route?.Name)
                        {
                            case "manage-members-person-list":
                                {
                                    var people = personRepository.GetPeople();
                                    data["people"] = people;
                                }
                                break;
                            case "manage-members-person-show":
                            case "manage-members-person-edit":
                                {
                                    var id = System.Convert.ToInt32(route.Parameters["personid"]);
                                    var person = personRepository.GetPerson(id);
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
