// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.SpaServices.Prerendering;

namespace MintPlayer.AspNetCore.Builder;

/// <summary>
/// Represents options for the SPA prerendering middleware.
/// </summary>
public class SpaPrerenderingOptions
{
	/// <summary>
	/// Gets or sets an <see cref="ISpaPrerendererBuilder"/> that the prerenderer will invoke before
	/// looking for the boot module file.
	/// 
	/// This is only intended to be used during development as a way of generating the JavaScript boot
	/// file automatically when the application runs. This property should be left as <c>null</c> in
	/// production applications.
	/// </summary>
	public MintPlayer.AspNetCore.SpaServices.Prerendering.ISpaPrerendererBuilder? BootModuleBuilder { get; set; }

	/// <summary>
	/// Gets or sets the path, relative to your application root, of the JavaScript file
	/// containing prerendering logic.
	/// </summary>
	public string BootModulePath { get; set; }

	/// <summary>
	/// Gets or sets an array of URL prefixes for which prerendering should not run.
	/// </summary>
	public string[] ExcludeUrls { get; set; }

	/// <summary>
	/// This method is called after the prerendering logic completes, and before the next middleware is called.
	/// </summary>
	public Func<HttpContext, Task> OnPrepareResponse { get; set; }
}
