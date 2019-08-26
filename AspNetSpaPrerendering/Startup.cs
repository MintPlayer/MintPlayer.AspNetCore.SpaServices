using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SpaServices.AngularCli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AspNetCoreSpaPrerendering.Data.Extensions;
using Spa.SpaRoutes;
using Microsoft.AspNetCore.Mvc.Razor;
using System.Linq;

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
                .AddMvc()
                .SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            // In production, the Angular files will be served from this directory
            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/dist";
            });

            // Define the SPA-routes for our helper
            services.AddSpaRoutes(routes => routes
                .Route("", "home")
                .Group("person", "person", person_routes => person_routes
                    .Route("", "list")
                    .Route("create", "create")
                    .Route("{id}", "show")
                    .Route("{id}/edit", "edit")
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
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
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
            app.UseSpaStaticFiles();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller}/{action=Index}/{id?}");
            });

            app.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501

                spa.Options.SourcePath = "ClientApp";

                if (env.IsDevelopment())
                {
                    spa.UseAngularCliServer(npmScript: "start");
                }
            });
        }
    }
}
