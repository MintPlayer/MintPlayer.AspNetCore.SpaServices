using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.AspNetCore.SpaServices.Xsrf;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");
// In production, the Angular files will be served from this directory
builder.Services.AddSpaStaticFilesImproved(configuration =>
{
	// angular.json sets "outputPath": "dist"; the default, dist/<project name>, is "ClientApp", whose
	// casing differed from what this said and broke on case-sensitive file systems.
	configuration.RootPath = "ClientApp/dist/browser";
});

var app = builder.Build();

// WebApplication adds the developer exception page in Development itself.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
	// The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
	app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgeryGenerator();

if (!app.Environment.IsDevelopment())
{
	app.UseSpaStaticFilesImproved();
}

app.UseRouting();

app.MapStaticAssets();
app.MapControllerRoute(
	name: "default",
	pattern: "{controller}/{action=Index}/{id?}");

// Explicit, so the endpoints run here - before the SPA middleware below, which answers every request it
// reaches. Without this call WebApplication runs them at the very end of the pipeline, too late.
app.UseEndpoints(_ => { });

app.UseSpaImproved(spa =>
{
	// To learn more about options for serving an Angular SPA from ASP.NET Core,
	// see https://go.microsoft.com/fwlink/?linkid=864501

	spa.Options.SourcePath = "ClientApp";

	if (app.Environment.IsDevelopment())
	{
		// The default regexes match both Vite's "Local:" line and the older webpack dev server.
		spa.UseAngularCliServer(npmScript: "start");
	}
});

app.Run();
