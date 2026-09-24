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

			// Sequential, deliberately not a finally. This depends on SetDoNotCacheHeaders being the
			// last thing GetAndStoreTokens does, so a throw leaves the application's headers
			// untouched and there is nothing to restore - but the dependency buys something rather
			// than merely costing something. On the failure path no token cookie is written, so a
			// finally would run TryMakePrivate over a response that has nothing to protect and
			// downgrade its shared-cacheability for no reason. Leaving the application's value
			// exactly as it set it is the correct outcome precisely because the mint failed.
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
			// "The application expressed a caching policy" means it set either header. A response
			// that expressed none keeps the antiforgery system's no-store, untouched.
			if (!active || (!hadCacheControl && !hadPragma))
			{
				return;
			}

			if (hadCacheControl)
			{
				if (!TryMakePrivate(cacheControl, out var restored))
				{
					// The value cannot be made safe, so nothing is restored - not even the Pragma,
					// which would leave the pair inconsistent. See TryMakePrivate.
					return;
				}

				response.Headers[HeaderNames.CacheControl] = restored;
			}

			// Restored independently of Cache-Control. An application that set only Pragma still
			// expressed a policy, and overwriting it because it happened not to set the other
			// header would be the same clobbering this snapshot exists to undo.
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
		/// Produces <paramref name="value"/> with <c>private</c> guaranteed and <c>public</c>
		/// removed, because the response carries a <c>Set-Cookie</c> holding this user's token.
		/// Returns <see langword="false"/> when the value cannot be parsed, and therefore cannot be
		/// made safe.
		/// </summary>
		/// <remarks>
		/// An unparseable value is deliberately <em>not</em> restored. Prefixing <c>private</c> onto
		/// text that cannot be parsed can emit <c>private, public, …</c> — RFC 9111 does not define
		/// which directive wins and caches differ, so the one path where the threat is real (a
		/// shared cache handing one user's token to the next) is the path that would carry the
		/// ambiguity. Editing the text instead is no better: if the value cannot be parsed, neither
		/// can its directive boundaries be found reliably — <c>public</c> may sit inside a quoted
		/// extension value. A malformed <c>Cache-Control</c> is not a policy worth preserving, so
		/// the antiforgery system's <c>no-store</c> stays in place.
		/// </remarks>
		private static bool TryMakePrivate(StringValues value, out StringValues result)
		{
			if (!CacheControlHeaderValue.TryParse(value.ToString(), out var parsed))
			{
				result = default;
				return false;
			}

			if (parsed!.Private && !parsed.Public)
			{
				result = value;
				return true;
			}

			parsed.Public = false;
			parsed.Private = true;
			result = parsed.ToString();
			return true;
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
	/// <para>
	/// Register it before whatever serves the SPA's HTML entry point, so the cookie is issued on the
	/// navigation that loads the app - Angular sends no antiforgery header until the cookie exists,
	/// and the first mutating request would otherwise be rejected.
	/// </para>
	/// <para>
	/// It does <em>not</em> have to come before <c>UseStaticFiles</c>. Static-file middleware
	/// short-circuits for a file it can serve, so anything registered after it is skipped for those
	/// responses - which is usually what you want: fingerprinted assets keep their
	/// <c>public, max-age=…</c> and stay shared-cacheable, instead of being given a per-user
	/// <c>Set-Cookie</c> and downgraded to <c>private</c>.
	/// </para>
	/// <para>
	/// <c>services.AddAntiforgery(...)</c> must be registered too; the middleware resolves
	/// <see cref="IAntiforgery"/> per request.
	/// </para>
	/// </remarks>
	public static IApplicationBuilder UseAntiforgeryGenerator(this IApplicationBuilder builder)
		=> builder.UseAntiforgeryGenerator(static _ => { });

	/// <summary>
	/// Adds a middleware to the http-request pipeline that generates an XSRF-token for the current user
	/// and stores it in a script-readable cookie, configured by <paramref name="configure"/>.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The configured cookie could not work: it is <c>HttpOnly</c> (the SPA could not read it), or it
	/// is <c>SameSite=None</c> without <c>Secure</c> (browsers reject it).
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
		// The name needs no check. XsrfOptions.Cookie is get-only and starts out named, and
		// CookieBuilder.Name rejects null and empty itself - so there is no way to reach this code
		// with an unusable name, and a guard for it would be unreachable rather than defensive.
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
