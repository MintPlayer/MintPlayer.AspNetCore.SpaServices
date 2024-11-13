using MintPlayer.AspNetCore.SpaServices.Extensions;

namespace MintPlayer.AspNetCore.XsrfForSpas.Demo;

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
		services.AddControllersWithViews();
		services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");
		// In production, the Angular files will be served from this directory
		services.AddSpaStaticFilesImproved(configuration =>
		{
			configuration.RootPath = "ClientApp/dist/client-app/browser";
		});
	}

	// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
	public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
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
		app.UseAntiforgery();

		if (!env.IsDevelopment())
		{
			app.UseSpaStaticFilesImproved();
		}

		app.UseRouting();

		app.UseEndpoints(endpoints =>
		{
			endpoints.MapStaticAssets();
			endpoints.MapControllerRoute(
				name: "default",
				pattern: "{controller}/{action=Index}/{id?}");
		});

		app.UseSpaImproved(spa =>
		{
			// To learn more about options for serving an Angular SPA from ASP.NET Core,
			// see https://go.microsoft.com/fwlink/?linkid=864501

			spa.Options.SourcePath = "ClientApp";

			if (env.IsDevelopment())
			{
				spa.UseProxyToSpaDevelopmentServer("http://localhost:4200");
				spa.UseAngularCliServer(npmScript: "start");
			}
		});
	}
}
