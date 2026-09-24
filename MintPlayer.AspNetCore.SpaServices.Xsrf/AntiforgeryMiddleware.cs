using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Primitives;

// Aliased rather than imported wholesale: Microsoft.Net.Http.Headers also declares SameSiteMode,
// which would collide with the Microsoft.AspNetCore.Http one the cookie options use.
using CacheControlHeaderValue = Microsoft.Net.Http.Headers.CacheControlHeaderValue;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace MintPlayer.AspNetCore.SpaServices.Xsrf;

/// <summary>
/// Issues an antiforgery request token and publishes it to the SPA in a script-readable cookie.
/// </summary>
/// <remarks>
/// <para>
/// The token is minted from a <c>Response.OnStarting</c> callback rather than on the way in. That
/// placement is the point of this middleware: the callback runs after the handler has finished, so
/// the token is bound to the principal the handler established. Minting eagerly binds the token on
/// a sign-in response to the <em>anonymous</em> principal, which makes the next mutating call fail
/// with a 400 and forces the client into a second round trip to refresh it.
/// </para>
/// <para>
/// Everything the callback does is wrapped, because a throw here is uniquely destructive. Kestrel's
/// <c>HttpProtocol.FireOnStarting</c> catches outside its loop, so one failing callback abandons
/// every callback still on the stack, and <c>ProduceEnd</c> then calls <c>SetErrorResponseHeaders</c>,
/// which resets the header collection entirely and emits a bare <c>500</c> carrying nothing but
/// <c>Date</c>. The exception never reaches the pipeline, so <c>UseExceptionHandler</c> cannot see
/// it. Degrading to "no cookie on this response", loudly, is strictly better than losing the
/// response.
/// </para>
/// </remarks>
internal sealed class Antiforgery
{
	private readonly RequestDelegate next;
	private readonly XsrfOptions options;
	private readonly IHostEnvironment environment;
	private readonly ILogger<Antiforgery> logger;

	/// <summary>0 until the insecure-cookie warning has been logged; see <see cref="WarnOnceIfInsecure"/>.</summary>
	private int insecureWarningLogged;

	public Antiforgery(RequestDelegate next, XsrfOptions options, IHostEnvironment environment, ILoggerFactory loggerFactory)
	{
		this.next = next;
		this.options = options;
		this.environment = environment;
		logger = loggerFactory.CreateLogger<Antiforgery>();
	}

	/// <remarks>
	/// <see cref="IAntiforgery"/> arrives per request rather than through the constructor. The
	/// middleware instance is built once, from the root provider, so a constructor parameter would
	/// be captured for the lifetime of the application - harmless for the framework's singleton
	/// <c>DefaultAntiforgery</c>, but a captive dependency for anyone who decorates it with a
	/// scoped service.
	/// </remarks>
	public async Task Invoke(HttpContext httpContext, IAntiforgery antiforgery)
	{
		httpContext.Response.OnStarting(static state =>
		{
			var (middleware, context, antiforgery) = ((Antiforgery, HttpContext, IAntiforgery))state;
			middleware.WriteCookie(context, antiforgery);
			return Task.CompletedTask;
		}, (this, httpContext, antiforgery));

		await next(httpContext);
	}

	private void WriteCookie(HttpContext httpContext, IAntiforgery antiforgery)
	{
		try
		{
			var response = httpContext.Response;
			var restore = CacheHeaderSnapshot.Capture(response, options.CacheHeaders);

			// Issues the request token and writes the framework's own HttpOnly cookie half. Called
			// unconditionally: the token is bound to the current principal, so skipping the mint
			// when a cookie already exists leaves a stale token in place across a sign-in and
			// breaks every mutating call that follows it.
			var tokens = antiforgery.GetAndStoreTokens(httpContext);

			restore.Apply(response);

			// Unreachable with the framework's DefaultAntiforgery, whose serializer is declared to
			// return a non-null string - the nullability comes from the internal feature type it
			// reads. Guarded anyway, because the cost of being wrong is not a NullReferenceException
			// but the wiped-headers 500 described on the class, and because a consumer may have
			// replaced IAntiforgery. Do not remove it on the grounds that the framework never
			// returns null; that is true and beside the point.
			if (tokens.RequestToken is null)
			{
				logger.LogError(
					"The antiforgery system returned a null request token, so no {CookieName} cookie was written. " +
					"The SPA cannot send an antiforgery header without it and every mutating request will be rejected. " +
					"This should not happen with the built-in IAntiforgery; check for a replacement registered in DI.",
					options.Cookie.Name);
				return;
			}

			var cookieOptions = options.Cookie.Build(httpContext);
			WarnOnceIfInsecure(httpContext, cookieOptions);

			response.Cookies.Append(options.Cookie.Name!, tokens.RequestToken, cookieOptions);
		}
		catch (Exception ex)
		{
			// Deliberately broad. Any escape from here costs the entire response, not just the
			// cookie: see the class remarks. Reachable in practice - CheckSSLConfig throws an
			// InvalidOperationException when AntiforgeryOptions.Cookie.SecurePolicy is Always and
			// the request is plain HTTP, which is what a TLS-terminating reverse proxy looks like
			// without UseForwardedHeaders, and DataProtection surfaces key-ring failures as
			// CryptographicException.
			logger.LogError(ex,
				"Failed to issue an antiforgery token, so no {CookieName} cookie was written on this response. " +
				"The rest of the response is unaffected. If this is an InvalidOperationException about SSL, the " +
				"application sets AntiforgeryOptions.Cookie.SecurePolicy = Always while the request arrived over " +
				"plain HTTP - behind a TLS-terminating proxy, enable UseForwardedHeaders so the scheme is honoured.",
				options.Cookie.Name);
		}
	}

	/// <summary>
	/// Logs once when the cookie is about to go out without <c>Secure</c> on what does not look like
	/// a development host - the silent half of <see cref="CookieSecurePolicy.SameAsRequest"/>.
	/// </summary>
	private void WarnOnceIfInsecure(HttpContext httpContext, CookieOptions cookieOptions)
	{
		if (cookieOptions.Secure || environment.IsDevelopment())
		{
			return;
		}

		if (Interlocked.Exchange(ref insecureWarningLogged, 1) != 0)
		{
			return;
		}

		logger.LogWarning(
			"The {CookieName} cookie is being written without the Secure attribute because the request arrived over " +
			"plain HTTP (scheme {Scheme}) and SecurePolicy is {SecurePolicy}. The CSRF token is travelling in clear. " +
			"Behind a TLS-terminating reverse proxy this is usually a missing UseForwardedHeaders with " +
			"ForwardedHeaders.XForwardedProto (or ASPNETCORE_FORWARDEDHEADERS_ENABLED=true), which leaves " +
			"Request.IsHttps false even though the client connected over HTTPS. Set SecurePolicy to Always to " +
			"require it unconditionally. This is logged once per process.",
			options.Cookie.Name, httpContext.Request.Scheme, options.Cookie.SecurePolicy);
	}

	/// <summary>
	/// The <c>Cache-Control</c> and <c>Pragma</c> values present before the token was issued, and
	/// the ability to put them back.
	/// </summary>
	private readonly struct CacheHeaderSnapshot
	{
		private readonly bool active;
		private readonly StringValues cacheControl;
		private readonly bool hadCacheControl;
		private readonly StringValues pragma;
		private readonly bool hadPragma;

		private CacheHeaderSnapshot(bool active, StringValues cacheControl, bool hadCacheControl, StringValues pragma, bool hadPragma)
		{
			this.active = active;
			this.cacheControl = cacheControl;
			this.hadCacheControl = hadCacheControl;
			this.pragma = pragma;
			this.hadPragma = hadPragma;
		}

		public static CacheHeaderSnapshot Capture(HttpResponse response, XsrfCacheHeaderPolicy policy)
		{
			if (policy != XsrfCacheHeaderPolicy.PreservePrivate)
			{
				return default;
			}

			var hadCacheControl = response.Headers.TryGetValue(HeaderNames.CacheControl, out var cacheControl);
			var hadPragma = response.Headers.TryGetValue(HeaderNames.Pragma, out var pragma);

			return new CacheHeaderSnapshot(true, cacheControl, hadCacheControl, pragma, hadPragma);
		}

		public void Apply(HttpResponse response)
		{
			// A response that set no caching policy of its own keeps the antiforgery system's
			// no-store. Only a value the application actually chose is worth restoring.
			if (!active || !hadCacheControl)
			{
				return;
			}

			response.Headers[HeaderNames.CacheControl] = ForcePrivate(cacheControl);

			if (hadPragma)
			{
				response.Headers[HeaderNames.Pragma] = pragma;
			}
			else
			{
				response.Headers.Remove(HeaderNames.Pragma);
			}
		}

		/// <summary>
		/// Returns <paramref name="value"/> with <c>private</c> guaranteed and <c>public</c> removed,
		/// because the response carries a <c>Set-Cookie</c> holding this user's token.
		/// </summary>
		private static StringValues ForcePrivate(StringValues value)
		{
			var text = value.ToString();

			if (CacheControlHeaderValue.TryParse(text, out var parsed))
			{
				if (parsed!.Private && !parsed.Public)
				{
					return value;
				}

				parsed.Public = false;
				parsed.Private = true;
				return parsed.ToString();
			}

			// Unparseable, so the directives cannot be edited safely. Prefixing still binds: a cache
			// that understands `private` honours it, and one that does not was never going to parse
			// the rest either.
			return text.Contains("private", StringComparison.OrdinalIgnoreCase)
				? value
				: $"private, {text}";
		}
	}
}

/// <summary>
/// Registers the middleware that publishes antiforgery tokens to a single-page application.
/// </summary>
public static class AntiforgeryExtensions
{
	/// <summary>
	/// Adds a middleware to the http-request pipeline that generates an XSRF-token for the current user
	/// and stores it in a cookie named "XSRF-TOKEN".
	/// </summary>
	/// <remarks>
	/// Register it early - before static files, routing and endpoints - so the cookie is issued on
	/// every response, including the SPA's HTML navigation. <c>services.AddAntiforgery(...)</c> must
	/// be registered too; the middleware resolves <see cref="IAntiforgery"/> per request.
	/// </remarks>
	public static IApplicationBuilder UseAntiforgeryGenerator(this IApplicationBuilder builder)
		=> builder.UseAntiforgeryGenerator(static _ => { });

	/// <summary>
	/// Adds a middleware to the http-request pipeline that generates an XSRF-token for the current user
	/// and stores it in a script-readable cookie, configured by <paramref name="configure"/>.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The configured cookie could not work: it is <c>HttpOnly</c> (the SPA could not read it), it is
	/// <c>SameSite=None</c> without <c>Secure</c> (browsers reject it), or it has no name.
	/// </exception>
	public static IApplicationBuilder UseAntiforgeryGenerator(this IApplicationBuilder builder, Action<XsrfOptions> configure)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configure);

		var options = new XsrfOptions();
		configure(options);
		Validate(options);

		return builder.UseMiddleware<Antiforgery>(options);
	}

	/// <summary>
	/// Rejects cookie configurations that cannot work, at startup, rather than emitting a cookie the
	/// browser drops or the SPA cannot read and leaving it to be diagnosed from a 400 much later.
	/// </summary>
	private const string ConfigureParameterName = "configure";

	private static void Validate(XsrfOptions options)
	{
		if (string.IsNullOrEmpty(options.Cookie.Name))
		{
			throw new ArgumentException(
				"XsrfOptions.Cookie.Name must be set. Angular's HttpClient looks for 'XSRF-TOKEN' unless " +
				"withXsrfConfiguration specifies otherwise.", ConfigureParameterName);
		}

		if (options.Cookie.HttpOnly)
		{
			throw new ArgumentException(
				"XsrfOptions.Cookie.HttpOnly must be false. The SPA reads this cookie with document.cookie and " +
				"echoes it back in a header; an HttpOnly cookie is invisible to script, so the header would never " +
				"be sent and every mutating request would be rejected. The HttpOnly half of the pair is the " +
				"framework's own antiforgery cookie, which is separate and already HttpOnly.", ConfigureParameterName);
		}

		if (options.Cookie.SameSite == SameSiteMode.None && options.Cookie.SecurePolicy != CookieSecurePolicy.Always)
		{
			throw new ArgumentException(
				"XsrfOptions.Cookie.SameSite is None, which browsers only accept together with Secure. Set " +
				"SecurePolicy to Always, or choose Lax or Strict.", ConfigureParameterName);
		}
	}
}
