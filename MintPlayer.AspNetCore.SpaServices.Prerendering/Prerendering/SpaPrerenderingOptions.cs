// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Net.Http.Headers;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

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
	public MintPlayer.AspNetCore.SpaServices.Abstractions.ISpaPrerendererBuilder? BootModuleBuilder { get; set; }

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
	/// Path to the Node executable
	/// </summary>
	public string NodePath { get; set; } = "node";

	/// <summary>
	/// Max number of milliseconds to wait for a single page to be prerendered by Node.
	/// Defaults to "0" (30s).
	/// "-1" means wait indefinitely.
	/// </summary>
	/// <remarks>
	/// This is the render timeout, not a build timeout - it is passed through to the prerendering
	/// JavaScript for one <c>renderToString</c> call. The time allowed for building the server
	/// bundle is <see cref="Core.SpaOptions.StartupTimeout"/>.
	/// </remarks>
	public int TimeoutMilliseconds { get; set; } = 0;

	/// <summary>
	/// The response headers that prerendering removes before writing the rendered HTML, because they
	/// describe the captured template rather than the rendered page.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Every header <em>not</em> in this set is preserved, including headers this library knows
	/// nothing about - which is how a <c>Strict-Transport-Security</c> or
	/// <c>Content-Security-Policy</c> written by upstream middleware survives prerendering. The set
	/// is representation metadata as defined by RFC 9110 §8, plus the framing headers.
	/// </para>
	/// <para>
	/// Adjust it with <see cref="PreserveResponseHeaders"/> and <see cref="DropResponseHeaders"/>
	/// rather than expecting to replace it.
	/// </para>
	/// </remarks>
	public static IReadOnlyCollection<string> DefaultDroppedResponseHeaders { get; } =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			HeaderNames.ContentLength,
			HeaderNames.ContentType,
			HeaderNames.ContentEncoding,
			HeaderNames.ContentLanguage,
			HeaderNames.ContentRange,
			HeaderNames.ContentLocation,
			HeaderNames.ContentMD5,
			HeaderNames.AcceptRanges,
			HeaderNames.ETag,
			HeaderNames.LastModified,
			HeaderNames.TransferEncoding,
		};

	/// <summary>
	/// Response headers to keep even though <see cref="DefaultDroppedResponseHeaders"/> would remove
	/// them. Compared case-insensitively.
	/// </summary>
	/// <remarks>
	/// The framing headers <c>Content-Length</c>, <c>Transfer-Encoding</c> and <c>Content-Range</c>
	/// cannot be preserved: they describe the captured template, and emitting one alongside a
	/// different body corrupts the response framing. Adding any of them throws when the middleware
	/// is registered.
	/// </remarks>
	public ISet<string> PreserveResponseHeaders { get; } =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Response headers to remove in addition to <see cref="DefaultDroppedResponseHeaders"/>.
	/// Compared case-insensitively.
	/// </summary>
	/// <remarks>
	/// Caching headers are preserved by default, because <c>StaticFileMiddleware</c> sets none of
	/// its own and the value present is normally the one upstream middleware intended. An
	/// application that does set a caching policy on <c>index.html</c> - through
	/// <c>DefaultPageStaticFileOptions</c>, say - should add <c>Cache-Control</c> here, or that
	/// policy is applied to per-user server-rendered HTML.
	/// </remarks>
	public ISet<string> DropResponseHeaders { get; } =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
