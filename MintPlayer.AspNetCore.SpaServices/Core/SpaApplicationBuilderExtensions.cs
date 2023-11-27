// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;

namespace MintPlayer.AspNetCore.SpaServices.Extensions;

/// <summary>
/// Provides extension methods used for configuring an application to
/// host a client-side Single Page Application (SPA).
/// </summary>
public static class SpaApplicationBuilderExtensions
{
	/// <summary>
	/// Handles all requests from this point in the middleware chain by returning
	/// the default page for the Single Page Application (SPA).
	///
	/// This middleware should be placed late in the chain, so that other middleware
	/// for serving static files, MVC actions, etc., takes precedence.
	/// </summary>
	/// <param name="app">The <see cref="IApplicationBuilder"/>.</param>
	/// <param name="configuration">
	/// This callback will be invoked so that additional middleware can be registered within
	/// the context of this SPA.
	/// </param>
	public static void UseSpaImproved(this IApplicationBuilder app, Action<Core.ISpaBuilder> configuration)
	{
		// Using our own ISpaBuilder, in order to always use the correct extension methods.
		ArgumentNullException.ThrowIfNull(configuration);

		// Use the options configured in DI (or blank if none was configured). We have to clone it
		// otherwise if you have multiple UseSpa calls, their configurations would interfere with one another.
		var optionsProvider = app.ApplicationServices.GetService<IOptions<Core.SpaOptions>>()!;
		var options = new Core.SpaOptions(optionsProvider.Value);

		var spaBuilder = new Internal.DefaultSpaBuilder(app, options);
		configuration.Invoke(spaBuilder);
        Internal.SpaDefaultPageMiddleware.Attach(spaBuilder);
	}
}
