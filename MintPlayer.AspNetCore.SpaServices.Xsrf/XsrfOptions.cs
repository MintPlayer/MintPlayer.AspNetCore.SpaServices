namespace MintPlayer.AspNetCore.SpaServices.Xsrf;

/// <summary>
/// What the middleware does about the cache headers <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgery"/>
/// writes while issuing a token.
/// </summary>
/// <remarks>
/// <para>
/// <c>DefaultAntiforgery.GetAndStoreTokens</c> stamps <c>Cache-Control: no-cache, no-store</c> and
/// <c>Pragma: no-cache</c> whenever the response has not started - and inside a
/// <c>Response.OnStarting</c> callback it never has, because Kestrel commits the headers only after
/// the callbacks have run. Kestrel also pops those callbacks last-in-first-out, and this middleware
/// is documented to be registered early, so its callback runs <em>last</em> and gets the final word
/// over every other writer in the pipeline.
/// </para>
/// <para>
/// The net effect before 11.0.0-rc.2 was that every response passing through
/// <c>UseAntiforgeryGenerator()</c> became uncacheable, silently, including responses whose caching
/// policy the application had deliberately set.
/// </para>
/// </remarks>
public enum XsrfCacheHeaderPolicy
{
	/// <summary>
	/// Leave whatever the antiforgery system wrote - <c>Cache-Control: no-cache, no-store</c> and
	/// <c>Pragma: no-cache</c> on every response. This is the behaviour of 11.0.0-rc.1 and earlier.
	/// </summary>
	NoStore,

	/// <summary>
	/// Restore the <c>Cache-Control</c> and <c>Pragma</c> the application had already set, forcing
	/// the restored directive to be <c>private</c>. The default.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A response that had no <c>Cache-Control</c> of its own keeps the antiforgery system's
	/// <c>no-store</c>, so the SPA's HTML navigation - the response that actually carries a fresh
	/// token - is still never cached. Nothing is restored that was not there to begin with.
	/// </para>
	/// <para>
	/// <c>private</c> is forced, and <c>public</c> dropped, because the response carries a
	/// <c>Set-Cookie</c>. Restoring a shared-cacheable directive verbatim would let a CDN or proxy
	/// store one user's token and hand it to the next.
	/// </para>
	/// </remarks>
	PreservePrivate,
}

/// <summary>
/// Configures the cookie written by
/// <see cref="AntiforgeryExtensions.UseAntiforgeryGenerator(IApplicationBuilder, Action{XsrfOptions})"/>.
/// </summary>
public sealed class XsrfOptions
{
	/// <summary>
	/// The SPA-visible cookie carrying the request token. Defaults to the name Angular's
	/// <c>HttpClient</c> looks for, at the site root, readable by script, <c>SameSite=Strict</c>,
	/// and <c>Secure</c> on HTTPS requests.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <see cref="CookieBuilder.HttpOnly"/> must stay <see langword="false"/>. The whole point of
	/// this cookie is that the SPA reads it with <c>document.cookie</c> and echoes it back in a
	/// header; Angular's <c>HttpXsrfCookieExtractor</c> cannot see an <c>HttpOnly</c> cookie, so
	/// setting it would disable CSRF protection rather than strengthen it.
	/// <see cref="AntiforgeryExtensions.UseAntiforgeryGenerator(IApplicationBuilder, Action{XsrfOptions})"/>
	/// rejects it at startup.
	/// </para>
	/// <para>
	/// The property is deliberately get-only. The hardened defaults live in its initialiser, so
	/// assigning a fresh <see cref="CookieBuilder"/> - the obvious-looking way to rename the cookie -
	/// would silently reset <see cref="CookieBuilder.SameSite"/> to
	/// <see cref="SameSiteMode.Unspecified"/> and hand the choice back to the browser, which is the
	/// very defect this release fixes. Configure the properties instead:
	/// <c>options.Cookie.Name = "CUSTOM-XSRF"</c>.
	/// </para>
	/// <para>
	/// <see cref="CookieSecurePolicy.SameAsRequest"/> is the default because
	/// <see cref="CookieSecurePolicy.Always"/> cannot be recovered from at runtime: a browser
	/// refuses a <c>Secure</c> cookie from a plain-HTTP origin other than <c>localhost</c>, so it
	/// would break HTTP-only intranet and LAN-address development outright. The cost is that
	/// <c>Secure</c> is silently absent behind a TLS-terminating reverse proxy, where
	/// <c>Request.IsHttps</c> is <see langword="false"/> unless <c>UseForwardedHeaders</c> is
	/// configured - so the middleware logs a warning once when that combination is detected outside
	/// of Development.
	/// </para>
	/// </remarks>
	public CookieBuilder Cookie { get; } = new()
	{
		Name = "XSRF-TOKEN",
		Path = "/",
		HttpOnly = false,
		SameSite = SameSiteMode.Strict,
		SecurePolicy = CookieSecurePolicy.SameAsRequest,
		IsEssential = true,
	};

	/// <summary>
	/// What to do about the cache headers the antiforgery system writes while issuing a token.
	/// Defaults to <see cref="XsrfCacheHeaderPolicy.PreservePrivate"/>.
	/// </summary>
	public XsrfCacheHeaderPolicy CacheHeaders { get; set; } = XsrfCacheHeaderPolicy.PreservePrivate;

	/// <summary>
	/// Decides per response whether a token is issued at all. <see langword="null"/>, the default,
	/// always issues one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// For responses that have no endpoint to carry <see cref="SkipXsrfTokenAttribute"/>, such as a
	/// custom middleware serving media or a proxied upstream. Prefer the attribute or
	/// <see cref="SkipXsrfTokenExtensions.SkipXsrfToken{TBuilder}(TBuilder)"/> where an endpoint
	/// exists: it sits next to the endpoint it describes and does not drift when a route moves.
	/// </para>
	/// <para>
	/// Evaluated when the response starts, so routing has already run and the predicate can inspect
	/// <c>GetEndpoint()</c> and the status code. It is not consulted for an endpoint that already
	/// opted out through metadata. A predicate that throws is logged at Error and no token is issued
	/// on that response. The response itself goes out normally.
	/// </para>
	/// <para>
	/// Returning <see langword="false"/> for the SPA's HTML entry point, or for its token-refresh
	/// endpoint, leaves the SPA without a token and breaks every mutating request that follows.
	/// </para>
	/// </remarks>
	public Func<HttpContext, bool>? ShouldIssue { get; set; }
}
