// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
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
		// The full type name, not "UseSpaPrerendering": a bare method name is not reachable through
		// the conventional Logging:LogLevel:<namespace> configuration, so a consumer could not turn
		// this middleware's Debug lines on without turning on everything.
		var logger = Internals.LoggerFinder.GetOrCreateLogger(applicationBuilder, typeof(SpaPrerenderingExtensions).FullName!);

		// Latches the first "template does not look like a whole document" warning. Scoped to this
		// UseSpaPrerendering call rather than static, which is the right lifetime, and kept as an
		// int for Interlocked. A fragment template is legitimate, so warning on every request would
		// be the same mistake as warning on every HEAD.
		var structureWarningIssued = 0;

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

			// Prerendering only makes sense for GET. A HEAD carries no body to capture, so it used
			// to reach the empty-template guard and log a warning for entirely healthy traffic;
			// anything else either never reaches a template at all or would be prerendered into a
			// response where a rendered page is meaningless.
			//
			// Placed before the build await on purpose: without that ordering a POST or an OPTIONS
			// to a SPA route blocks on `ng build` before being turned away downstream. Placed after
			// the exclude loop so the skip is not logged for static asset paths, and after
			// Response.OnStarting so OnPrepareResponse still runs for every method.
			if (!HttpMethods.IsGet(context.Request.Method))
			{
				logger.LogDebug(
					"Skipping prerendering of {Path}: prerendering applies to GET requests, and this is a {Method}.",
					context.Request.Path,
					context.Request.Method);

				await next();
				return;
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
				if (!IsHtmlContentType(context.Response.ContentType))
				{
					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Beyond "is it HTML", the capture has to be the *whole* document. Being 2xx and
				// text/html does not establish that, which is how a 206 range response - a
				// one-byte slice of index.html - was accepted as a template.
				//
				// Note the status must be exactly 200 rather than any 2xx. That fails closed on
				// statuses nobody has considered yet, where "2xx except the ones we know about"
				// fails open. A 206 is rejected even when its range covers the entire file, because
				// its Content-Range framing cannot survive a body we rewrite.
				// A status that carries no body at all is a benign reason to have nothing to
				// prerender, so it is reported at Debug. Warning is reserved for a response that
				// was supposed to be a document and is not - otherwise this category teaches
				// consumers to ignore it, which is the mistake the HEAD warning made.
				if (!CanHaveResponseBody(context))
				{
					logger.LogDebug(
						"Skipping prerendering of {Path}: status {StatusCode} carries no response body, so there is no template.",
						context.Request.Path,
						context.Response.StatusCode);

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				if (context.Response.StatusCode != StatusCodes.Status200OK
					|| context.Response.Headers.ContainsKey(HeaderNames.ContentRange))
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: the captured response is a partial representation ({Method}, status {StatusCode}, Content-Range \"{ContentRange}\").",
						context.Request.Path,
						context.Request.Method,
						context.Response.StatusCode,
						context.Response.Headers[HeaderNames.ContentRange].ToString());

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Accept-Encoding is stripped from the request above so that we capture plain text,
				// but nothing verified the result. A capture that is still encoded would be decoded
				// as UTF-8 and hand the prerenderer compressed bytes.
				if (!IsIdentityContentEncoding(context.Response.Headers[HeaderNames.ContentEncoding]))
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: the captured response is encoded as \"{ContentEncoding}\", which cannot be used as a template.",
						context.Request.Path,
						context.Response.Headers[HeaderNames.ContentEncoding].ToString());

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// A declared length that does not match what was actually written means the body
				// was truncated or never flushed, whatever the status says. Only a short capture is
				// rejected: a *longer* one cannot be a truncation, and a response-transforming
				// middleware that legitimately shrinks the body (HTML minification, for instance)
				// updates ContentLength as it goes - if it did not, every response on every route
				// that this middleware never touches would already fail Kestrel's own
				// Content-Length verification.
				if (context.Response.ContentLength.HasValue && outputBuffer.Length < context.Response.ContentLength)
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: only {CapturedBytes} of the {DeclaredBytes} declared bytes were captured, so the template is incomplete.",
						context.Request.Path,
						outputBuffer.Length,
						context.Response.ContentLength);

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Bytes that are not valid UTF-8 cannot be a template. Checked on the bytes rather
				// than by inspecting the decoded string for U+FFFD, which cannot tell corrupt input
				// apart from a template legitimately containing a replacement character, and rather
				// than decoding with throwOnInvalidBytes, which would throw on the request path and
				// leave nothing to log.
				if (!IsValidUtf8(outputBuffer))
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: the captured {CapturedBytes} bytes are not valid UTF-8 (Content-Type \"{ContentType}\"), so they cannot be a template.",
						context.Request.Path,
						outputBuffer.Length,
						context.Response.ContentType);

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Most prerendering logic will want to know about the original, unprerendered
				// HTML that the client would be getting otherwise. Typically this is used as
				// a template from which the fully prerendered page can be generated.
				var originalHtml = ReadCapturedHtml(outputBuffer);

				// An empty template is never something the prerenderer can work with, and handing
				// it to Node is what produces an unhelpful NG05104 instead of a diagnosable
				// message. The one benign producer of an empty capture - a HEAD request, which
				// carries headers but no body - is turned away before the capture is installed, so
				// reaching this point on a GET means the body genuinely was never written. It also
				// covers a chunked empty response, where the declared-length check above is silent.
				if (string.IsNullOrWhiteSpace(originalHtml))
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: the captured response was empty ({Method}, status {StatusCode}, {CapturedBytes} of {DeclaredBytes} declared bytes), so there is no template to prerender.",
						context.Request.Path,
						context.Request.Method,
						context.Response.StatusCode,
						outputBuffer.Length,
						context.Response.ContentLength);

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// A NUL cannot appear in a conforming HTML document - the HTML parser replaces it -
				// so its presence means the bytes were not the text they claimed to be. The case
				// this catches is a non-UTF-8 charset: UTF-16 markup is byte-wise valid UTF-8 for
				// its ASCII half and decodes to "<\0!\0d\0...", which is neither empty nor
				// whitespace and so passed every earlier check straight into NG05104.
				if (originalHtml.Contains('\0'))
				{
					logger.LogWarning(
						"Skipping prerendering of {Path}: the captured {CapturedBytes} bytes decoded to text containing NUL characters (Content-Type \"{ContentType}\"), which is not a usable template. Template begins: {TemplateHead}",
						context.Request.Path,
						outputBuffer.Length,
						context.Response.ContentType,
						DescribeTemplateHead(originalHtml));

					await PassThroughAsync(context, outputBuffer);
					return;
				}

				// Diagnostic only, never a rejection. A fragment template is a legitimate if
				// unusual deployment - the renderer normalizes a bare "<app-root></app-root>" into
				// a full document - so failing the request here would break a working application.
				// Warned once and then only at Debug: a fragment deployment must not emit a warning
				// per request forever, but a consumer whose template is silently wrong needs to see
				// something at default level at least once.
				//
				// Only the opening <html tag is looked for, never a closing </html>. Those end tags
				// are optional per the HTML standard *and* are removed by every mainstream HTML
				// minifier, so requiring one warns about perfectly healthy documents - see the
				// remarks on LooksLikeWholeDocument for the detail.
				if (!LooksLikeWholeDocument(originalHtml))
				{
					const string structureMessage =
						"The prerender template for {Path} has no <html> element ({TemplateLength} characters). This is supported, but if prerendering is failing it is the first thing to check. Template begins: {TemplateHead}";

					if (Interlocked.Exchange(ref structureWarningIssued, 1) == 0)
					{
						logger.LogWarning(structureMessage, context.Request.Path, originalHtml.Length, DescribeTemplateHead(originalHtml));
					}
					else
					{
						logger.LogDebug(structureMessage, context.Request.Path, originalHtml.Length, DescribeTemplateHead(originalHtml));
					}
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
		// Only for a response that is allowed to carry a body. "Zero bytes written contradicts the
		// declared length" is only true when a body was expected: a HEAD reports the length the
		// equivalent GET would return and deliberately sends nothing, and 204/205/304 have no body
		// by definition. Rewriting those to 0 discards correct metadata - which is exactly what
		// this did to every HEAD.
		if (CanHaveResponseBody(context)
			&& context.Response.ContentLength.HasValue
			&& context.Response.ContentLength != outputBuffer.Length)
		{
			context.Response.ContentLength = outputBuffer.Length;
		}

		await outputBuffer.CopyToAsync(context.Response.Body);
	}

	private static bool CanHaveResponseBody(HttpContext context)
		=> !HttpMethods.IsHead(context.Request.Method)
			&& context.Response.StatusCode is not (StatusCodes.Status204NoContent
				or StatusCodes.Status205ResetContent
				or StatusCodes.Status304NotModified);

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

	/// <summary>
	/// Whether the captured response was left unencoded, so that it can be decoded as text.
	/// </summary>
	/// <remarks>
	/// An absent header is the normal case, since <c>Accept-Encoding</c> is stripped from the
	/// request before the capture. An explicit <c>identity</c> is honoured; anything else is a
	/// content coding this middleware cannot undo, and a multi-value header means at least one
	/// coding was applied even if one of them is <c>identity</c>.
	/// </remarks>
	private static bool IsIdentityContentEncoding(StringValues contentEncoding)
	{
		if (contentEncoding.Count == 0)
		{
			return true;
		}

		if (contentEncoding.Count > 1)
		{
			return false;
		}

		var value = contentEncoding[0];

		return string.IsNullOrEmpty(value)
			|| string.Equals(value.Trim(), "identity", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Whether the captured bytes are valid UTF-8, checked without decoding them.
	/// </summary>
	private static bool IsValidUtf8(MemoryStream outputBuffer)
	{
		if (!outputBuffer.TryGetBuffer(out var buffer))
		{
			return System.Text.Unicode.Utf8.IsValid(outputBuffer.ToArray());
		}

		return System.Text.Unicode.Utf8.IsValid(buffer.AsSpan());
	}

	/// <summary>
	/// Whether the template looks like a complete HTML document. A heuristic, used only to decide
	/// whether to log - never to reject a template.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Only the opening tag is checked, deliberately. This originally required a closing
	/// <c>&lt;/html&gt;</c> as well, which turned out to be a false positive on any template that had
	/// been through an HTML minifier - it reported a perfectly healthy document, one that visibly
	/// starts with <c>&lt;html lang=en&gt;</c>, as having no <c>&lt;html&gt;</c> element at all.
	/// </para>
	/// <para>
	/// There are two independent reasons a valid document may have no <c>&lt;/html&gt;</c>. First,
	/// the HTML Living Standard makes the end tags for <c>html</c> and <c>body</c> *omissible* - an
	/// <c>html</c> end tag may be omitted when it is not immediately followed by a comment - so a
	/// document ending at <c>&lt;/div&gt;</c> is not malformed, and the parser closes them
	/// implicitly. Second, and this is what was actually observed, removing those optional end tags
	/// is a standard minifier optimisation: WebMarkupMin has <c>RemoveOptionalEndTags</c> and
	/// html-minifier-terser has <c>removeOptionalTags</c>, and a minifier registered *inside* the
	/// SPA callback runs downstream of this capture. In the demo that turns a 547-byte
	/// <c>index.html</c> ending in <c>&lt;/body&gt;&lt;/html&gt;</c> into a 456-character template
	/// with neither.
	/// </para>
	/// <para>
	/// The opening tag survives all of that: no build tool emits a document without one, and no
	/// minifier strips it. It carries the whole signal on its own - a genuine fragment template
	/// (<c>&lt;app-root&gt;&lt;/app-root&gt;</c>) has no <c>&lt;html</c> anywhere, and neither does a
	/// mid-document byte range.
	/// </para>
	/// </remarks>
	private static bool LooksLikeWholeDocument(string html)
		=> html.Contains("<html", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The first 200 characters of a template, on a single line, for a log message.
	/// </summary>
	private static string DescribeTemplateHead(string html)
	{
		var head = html.Length <= 200 ? html : html[..200];

		return head.ReplaceLineEndings(" ");
	}

	private static void RemoveConditionalRequestHeaders(HttpRequest request)
	{
		request.Headers.Remove(HeaderNames.IfMatch);
		request.Headers.Remove(HeaderNames.IfModifiedSince);
		request.Headers.Remove(HeaderNames.IfNoneMatch);
		request.Headers.Remove(HeaderNames.IfUnmodifiedSince);
		request.Headers.Remove(HeaderNames.IfRange);

		// Range, for the same reason as the conditional headers above: we need the whole document
		// to use as a template, and a Range request makes StaticFileMiddleware answer 206 with a
		// slice of it. `Range: bytes=0-0` yielded a one-byte template - the "<" of "<!doctype html>".
		//
		// Removing If-Range while keeping Range was actively harmful, not merely incomplete:
		// StaticFileContext.ComputeIfRange is the only code that can cancel an already-parsed
		// range, so stripping If-Range guaranteed the range was always honoured.
		//
		// Unlike Accept-Encoding this is NOT restored afterwards. That restore is functionally
		// required - it happens before ServePrerenderResult writes, and upstream compression
		// middleware reads the header at first write - whereas nothing downstream of the capture
		// reads Range. A prerendered body cannot satisfy a byte range in any case.
		request.Headers.Remove(HeaderNames.Range);
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
