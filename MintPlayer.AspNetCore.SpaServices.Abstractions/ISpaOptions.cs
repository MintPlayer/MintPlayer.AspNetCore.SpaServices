namespace MintPlayer.AspNetCore.SpaServices.Abstractions;

/// <summary>
/// Describes options for hosting a Single Page Application (SPA).
/// </summary>
public interface ISpaOptions
{
	/// <summary>
	/// Gets or sets the URL of the default page that hosts your SPA user interface.
	/// The default value is <c>"/index.html"</c>.
	/// </summary>
	PathString DefaultPage { get; set; }

	/// <summary>
	/// Gets or sets the <see cref="StaticFileOptions"/> that supplies content
	/// for serving the SPA's default page.
	///
	/// If not set, a default file provider will read files from the
	/// <see cref="IHostingEnvironment.WebRootPath"/>, which by default is
	/// the <c>wwwroot</c> directory.
	/// </summary>
	StaticFileOptions? DefaultPageStaticFileOptions { get; set; }

	/// <summary>
	/// Gets or sets the path, relative to the application working directory,
	/// of the directory that contains the SPA source files during
	/// development. The directory may not exist in published applications.
	/// </summary>
	public string? SourcePath { get; set; }

	/// <summary>
	/// Controls whether the development server should be used with a dynamic or fixed port.
	/// </summary>
	public int DevServerPort { get; set; }

	/// <summary>
	/// Gets or sets the name of the package manager executable, (e.g npm,
	/// yarn) to run the SPA.
	///
	/// The default value is 'npm'.
	/// </summary>
	public string PackageManagerCommand { get; set; }

	/// <summary>
	/// Gets or sets the maximum duration that a request will wait for the SPA
	/// to become ready to serve to the client.
	/// </summary>
	public TimeSpan StartupTimeout { get; set; }
}
