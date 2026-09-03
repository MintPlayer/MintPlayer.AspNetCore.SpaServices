// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using MintPlayer.AspNetCore.NodeServices;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

/// <summary>
/// Extension methods for configuring prerendering of a Single Page Application.
/// </summary>
public static class SpaPrerenderingExtensions
{
	/// <summary>
	/// Enables server-side prerendering middleware for a Single Page Application.
	/// </summary>
	/// <param name="spaBuilder">The <see cref="Core.ISpaBuilder"/>.</param>
	/// <param name="configuration">Supplies configuration for the prerendering middleware.</param>
	public static IApplicationBuilder UseSpaPrerendering(
		this Abstractions.ISpaBuilder spaBuilder,
		Action<SpaPrerenderingOptions> configuration)
	{
		// This is not an extension method on ISpaBuilder, but our own ISpaBuilder
		// This way applications won't take the wrong extension method, but always use this one instead
		if (spaBuilder == null)
		{
			throw new ArgumentNullException(nameof(spaBuilder));
		}

		if (configuration == null)
		{
			throw new ArgumentNullException(nameof(configuration));
		}

		var options = new SpaPrerenderingOptions();
		configuration.Invoke(options);

		var capturedBootModulePath = options.BootModulePath;
		if (string.IsNullOrEmpty(capturedBootModulePath))
		{
			throw new InvalidOperationException($"To use {nameof(UseSpaPrerendering)}, you " +
				$"must set a nonempty value on the ${nameof(SpaPrerenderingOptions.BootModulePath)} " +
				$"property on the ${nameof(SpaPrerenderingOptions)}.");
		}

		// The server bundle is built once, on the first request that needs it, and every request
		// awaits that same build.
		//
		// This used to be a bool set to true *before* awaiting the build, which meant a build that
		// failed or timed out was never observed by anything except the first request: every later
		// request skipped straight past it and prerendered against a bundle that was missing or
		// half-written, reporting some downstream symptom instead of the build error. Sharing the
		// task instead means all requests see the same outcome.
		//
		// Note this deliberately differs from AngularCliMiddleware, which gives each request its
		// own timeout around a shared startup task so a later request can still succeed. A build
		// that hangs or fails will not fix itself on retry, and retrying would spawn another npm
		// process, so here a failure is final and is reported with the build's own output.
		var bootModuleBuildTask = options.BootModuleBuilder == null
			? null
			: new Lazy<Task>(
				() => options.BootModuleBuilder.Build(spaBuilder),
				LazyThreadSafetyMode.ExecutionAndPublication);

		// Get all the necessary context info that will be used for each prerendering call
		var applicationBuilder = spaBuilder.ApplicationBuilder;
		var serviceProvider = applicationBuilder.ApplicationServices;
		var nodeServices = GetNodeServices(serviceProvider, opts => opts.NodePath = options.NodePath);
		var applicationStoppingToken = serviceProvider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
		var applicationBasePath = serviceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
		var moduleExport = new JavaScriptModuleExport(capturedBootModulePath);
		var excludePathStrings = (options.ExcludeUrls ?? Array.Empty<string>())
			.Select(url => new PathString(url))
			.ToArray();
		var logger = Internals.LoggerFinder.GetOrCreateLogger(applicationBuilder, nameof(UseSpaPrerendering));

		applicationBuilder.Use(async (context, next) =>
		{
			context.Response.OnStarting(async () =>
			{
				if (options.OnPrepareResponse != null)
				{
					await options.OnPrepareResponse(context);
				}
			});

			// If this URL is excluded, skip prerendering.
			// This is typically used to ensure that static client-side resources
			// (e.g., /dist/*.css) are served normally or through SPA development
			// middleware, and don't return the prerendered index.html page.
			foreach (var excludePathString in excludePathStrings)
			{
				if (context.Request.Path.StartsWithSegments(excludePathString))
				{
					await next();
					return;
				}
			}

			if (bootModuleBuildTask != null)
			{
				if (!bootModuleBuildTask.IsValueCreated)
				{
					logger.LogInformation("Building server BootModule");
				}

				await bootModuleBuildTask.Value;
			}

			// It's no good if we try to return a 304. We need to capture the actual
			// HTML content so it can be passed as a template to the prerenderer.
			RemoveConditionalRequestHeaders(context.Request);

			// Make sure we're not capturing compressed content, because then we'd have
			// to decompress it. Since this sub-request isn't leaving the machine, there's
			// little to no benefit in having compression on it.
			var originalAcceptEncodingValue = GetAndRemoveAcceptEncodingHeader(context.Request);

			// Capture the non-prerendered responses, which in production will typically only
			// be returning the default SPA index.html page (because other resources will be
			// served statically from disk). We will use this as a template in which to inject
			// the prerendered output.
			using (var outputBuffer = new MemoryStream())
			{
				var originalResponseStream = context.Response.Body;
				context.Response.Body = outputBuffer;

				try
				{
					await next();
					outputBuffer.Seek(0, SeekOrigin.Begin);
				}
				finally
				{
					context.Response.Body = originalResponseStream;

					if (!string.IsNullOrEmpty(originalAcceptEncodingValue))
					{
						context.Request.Headers[HeaderNames.AcceptEncoding] = originalAcceptEncodingValue;
					}
				}

				// If the client has gone, there's no point prerendering for it. This is not just an
				// optimisation: the middleware downstream of us swallows the cancellation (see
				// StaticFileContext.SendAsync, which catches OperationCanceledException and only
				// logs it), so from here an aborted request is indistinguishable from a successful
				// one - 200, text/html, ContentLength set - except that the body was never written.
				// Prerendering that empty template is what surfaces as Angular's NG05104.
				//
				// The buffer is copied out rather than dropped because the abort can also land
				// *after* the body was fully captured, in which case it holds the complete page and
				// discarding it would be pointless. When the buffer is empty the copy is free:
				// MemoryStream.CopyToAsync returns immediately when there is nothing to read.
				//
				// No cancellation token is passed on purpose. MemoryStream.CopyToAsync checks the
				// token up front and would return a cancelled task, so passing RequestAborted here
				// would throw out of the middleware on every aborted request. Kestrel discards
				// writes on an aborted connection instead of throwing, so the copy is safe as-is.
				if (context.RequestAborted.IsCancellationRequested)
				{
					logger.LogDebug(
						"Skipping prerendering of {Path}: the request was aborted. Captured {CapturedBytes} of {DeclaredBytes} declared bytes.",
						context.Request.Path,
						outputBuffer.Length,
						context.Response.ContentLength);

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// If it isn't an HTML page that we can use as the template for prerendering,
				//  - ... because it's not text/html
				//  - ... or because it's an error
				// then prerendering doesn't apply to this request, so just pass through the
				// response as-is. Note that the non-text/html case is not an error: this is
				// typically how the SPA dev server responses for static content are returned
				// in development mode.
				var canPrerender = IsSuccessStatusCode(context.Response.StatusCode)
					&& IsHtmlContentType(context.Response.ContentType);
					//&& IsNotRedirect(context.Response.StatusCode);
				if (!canPrerender)
				{
					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Most prerendering logic will want to know about the original, unprerendered
				// HTML that the client would be getting otherwise. Typically this is used as
				// a template from which the fully prerendered page can be generated.
				var originalHtml = ReadCapturedHtml(outputBuffer);

				// An empty template is never something the prerenderer can work with, and handing
				// it to Node is what produces an unhelpful NG05104 instead of a diagnosable
				// message. There is no known benign cause, so this is worth a warning.
				if (string.IsNullOrWhiteSpace(originalHtml))
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: the captured response was empty ({CapturedBytes} of {DeclaredBytes} declared bytes), so there is no template to prerender.",
						context.Request.Path,
						outputBuffer.Length,
						context.Response.ContentLength);

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				var customData = new Dictionary<string, object>
				{
					{ "originalHtml", originalHtml }
				};

				// If the developer wants to use custom logic to pass arbitrary data to the
				// prerendering JS code (e.g., to pass through cookie data), now's their chance
				var spaPrerenderingService = context.RequestServices.GetService<Services.ISpaPrerenderingService>();
				if (spaPrerenderingService != null)
				{
					await spaPrerenderingService.OnSupplyData(context, customData);
				}

				// Don't do SSR when we have a redirect. Note that a status code assigned from
				// inside a Response.OnStarting callback is not visible yet, which is why
				// SkipPrerendering() exists - see PrerenderingHttpContextExtensions.
				if (!IsSuccessStatusCode(context.Response.StatusCode) || context.IsPrerenderingSkipped())
				{
					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Stop the render when either the client gives up or the host shuts down. Node is
				// not told to stop - the RPC protocol has no way to say so - so this releases the
				// request thread rather than reclaiming the render.
				using var prerenderCts = CancellationTokenSource.CreateLinkedTokenSource(
					context.RequestAborted,
					applicationStoppingToken);

				var (unencodedAbsoluteUrl, unencodedPathAndQuery) = GetUnencodedUrlAndPathQuery(context);
				var renderResult = await Prerenderer.RenderToString(
					applicationBasePath,
					nodeServices,
					applicationStoppingToken,
					moduleExport,
					unencodedAbsoluteUrl,
					unencodedPathAndQuery,
					customDataParameter: customData,
					timeoutMilliseconds: options.TimeoutMilliseconds,
					requestPathBase: context.Request.PathBase.ToString(),
					requestCancellationToken: prerenderCts.Token);

				await ServePrerenderResult(context, renderResult);
			}
		});
		return applicationBuilder;
	}

	/// <summary>
	/// Writes the captured response through to the client unchanged, reconciling a declared
	/// <see cref="HttpResponse.ContentLength"/> with what was actually captured.
	/// </summary>
	/// <remarks>
	/// The reconciliation matters when the request's abort token is cancelled while the connection
	/// is still alive - an application-level request timeout, or a linked token, rather than a real
	/// client disconnect. Downstream sets `ContentLength` before writing the body, skips the write
	/// on its own cancellation check, and Kestrel then fails the response with "Response
	/// Content-Length mismatch: too few bytes written". On a genuine socket abort that check is
	/// suppressed and this is unreachable, so it is cheap insurance rather than a hot path.
	/// A length that is already absent is left absent: adding one to a chunked response would
	/// change how the response is framed.
	/// </remarks>
	private static async Task PassThroughAsync(HttpContext context, MemoryStream outputBuffer)
	{
		if (context.Response.ContentLength.HasValue && context.Response.ContentLength != outputBuffer.Length)
		{
			context.Response.ContentLength = outputBuffer.Length;
		}

		await outputBuffer.CopyToAsync(context.Response.Body);
	}

	/// <summary>
	/// Decodes the captured response body into the HTML template handed to the prerenderer.
	/// </summary>
	/// <remarks>
	/// Reads through <see cref="MemoryStream.TryGetBuffer"/> rather than
	/// <see cref="MemoryStream.GetBuffer"/>: the latter returns the whole internal array, whose
	/// length is the stream's <c>Capacity</c>, so decoding it without bounds appended however many
	/// bytes the stream had grown but never used - thousands of NUL characters on a response over
	/// 16 KB. <c>TryGetBuffer</c> hands back offset *and* count, so there is no arithmetic here to
	/// get wrong, and unlike <c>ToArray()</c> it does not copy the page on every request.
	/// A UTF-8 byte order mark is skipped, because <see cref="Encoding.UTF8"/> decodes it into a
	/// leading U+FEFF that would sit in front of the doctype.
	/// UTF-8 is assumed rather than read from the response's charset: the write side of this
	/// middleware is unconditionally UTF-8, so honouring a different charset on the read side alone
	/// would turn visible mojibake into silently wrong bytes on the wire.
	/// </remarks>
	private static string ReadCapturedHtml(MemoryStream outputBuffer)
	{
		if (!outputBuffer.TryGetBuffer(out var buffer))
		{
			// Only reachable for a MemoryStream constructed to hide its buffer, which this
			// middleware never does. Falling back to a copy is still better than returning
			// nothing, which would look exactly like the empty-template defect.
			var copy = outputBuffer.ToArray();
			return DecodeSkippingBom(copy, 0, copy.Length);
		}

		return DecodeSkippingBom(buffer.Array!, buffer.Offset, buffer.Count);
	}

	private static string DecodeSkippingBom(byte[] bytes, int offset, int count)
	{
		if (count >= 3 && bytes[offset] == 0xEF && bytes[offset + 1] == 0xBB && bytes[offset + 2] == 0xBF)
		{
			offset += 3;
			count -= 3;
		}

		return Encoding.UTF8.GetString(bytes, offset, count);
	}

	private static bool IsHtmlContentType(string? contentType)
	{
		// Media types are case-insensitive (RFC 9110 8.3.1), and optional whitespace is allowed
		// before the ';' that starts the parameters. Comparing Ordinal against a lowercase literal
		// meant "TEXT/HTML" and "text/html ; charset=utf-8" silently skipped prerendering.
		if (contentType == null)
		{
			return false;
		}

		var separator = contentType.IndexOf(';');
		var mediaType = separator < 0 ? contentType : contentType[..separator];

		return string.Equals(mediaType.Trim(), "text/html", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsSuccessStatusCode(int statusCode)
		=> statusCode >= 200 && statusCode < 300;

	private static void RemoveConditionalRequestHeaders(HttpRequest request)
	{
		request.Headers.Remove(HeaderNames.IfMatch);
		request.Headers.Remove(HeaderNames.IfModifiedSince);
		request.Headers.Remove(HeaderNames.IfNoneMatch);
		request.Headers.Remove(HeaderNames.IfUnmodifiedSince);
		request.Headers.Remove(HeaderNames.IfRange);
	}

	private static string GetAndRemoveAcceptEncodingHeader(HttpRequest request)
	{
		var headers = request.Headers;
		var value = (string)null;

		if (headers.ContainsKey(HeaderNames.AcceptEncoding))
		{
			value = headers[HeaderNames.AcceptEncoding];
			headers.Remove(HeaderNames.AcceptEncoding);
		}

		return value;
	}

	private static (string, string) GetUnencodedUrlAndPathQuery(HttpContext httpContext)
	{
		// This is a duplicate of code from Prerenderer.cs in the SpaServices package.
		// Once the SpaServices.Extension package implementation gets merged back into
		// SpaServices, this duplicate can be removed. To remove this, change the code
		// above that calls Prerenderer.RenderToString to use the internal overload
		// that takes an HttpContext instead of a url/path+query pair.
		var requestFeature = httpContext.Features.Get<IHttpRequestFeature>();
		var unencodedPathAndQuery = requestFeature.RawTarget;
		var request = httpContext.Request;
		var unencodedAbsoluteUrl = $"{request.Scheme}://{request.Host}{unencodedPathAndQuery}";
		return (unencodedAbsoluteUrl, unencodedPathAndQuery);
	}

	private static async Task ServePrerenderResult(HttpContext context, RenderToStringResult renderResult)
	{
		context.Response.Clear();

		// The Globals property exists for back-compatibility but is meaningless for prerendering
		// that returns complete HTML pages. Checked before the redirect branch too, so that a result
		// carrying both a RedirectUrl and Globals reports the problem instead of silently dropping
		// the Globals.
		if (renderResult.Globals != null)
		{
			throw new InvalidOperationException($"{nameof(renderResult.Globals)} is not " +
				$"supported when prerendering via {nameof(UseSpaPrerendering)}(). Instead, " +
				$"your prerendering logic should return a complete HTML page, in which you " +
				$"embed any information you wish to return to the client.");
		}

		if (!string.IsNullOrEmpty(renderResult.RedirectUrl))
		{
			// 308 is the permanent counterpart of 307 and, like 301, must survive as a permanent
			// redirect. Treating only 301 as permanent quietly downgraded a 308 to 302, losing both
			// the permanence and the method preservation the prerenderer asked for.
			var statusCode = renderResult.StatusCode.GetValueOrDefault();
			var permanentRedirect = statusCode is 301 or 308;
			context.Response.Redirect(renderResult.RedirectUrl, permanentRedirect);
		}
		else
		{
			// Without this the null reaches Response.WriteAsync and surfaces as
			// "ArgumentNullException (Parameter 'text')", which says nothing about prerendering.
			if (renderResult.Html == null)
			{
				throw new InvalidOperationException($"Prerendering returned no HTML. Your " +
					$"prerendering logic should set {nameof(renderResult.Html)} to a complete HTML " +
					$"page, or set {nameof(renderResult.RedirectUrl)} to redirect instead.");
			}

			if (renderResult.StatusCode.HasValue)
			{
				context.Response.StatusCode = renderResult.StatusCode.Value;
			}

			context.Response.ContentType = "text/html";
			await context.Response.WriteAsync(renderResult.Html);
		}
	}

	private static INodeServices GetNodeServices(IServiceProvider serviceProvider, Action<NodeServicesOptions> optionAction)
	{
		// Use the registered instance, or create a new private instance if none is registered
		var instance = serviceProvider.GetService<INodeServices>();
		if (instance == null)
		{
			// Will always be this case
			var opts = new NodeServicesOptions(serviceProvider);
			optionAction(opts);
			var result = NodeServicesFactory.CreateNodeServices(opts);
			return result;
		}
		else
		{
			// Will never be called for the moment
			return instance;
		}
	}
}
