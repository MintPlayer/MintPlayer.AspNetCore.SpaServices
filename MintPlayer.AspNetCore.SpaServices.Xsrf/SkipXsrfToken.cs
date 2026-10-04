namespace MintPlayer.AspNetCore.SpaServices.Xsrf;

/// <summary>
/// Endpoint metadata that keeps a response out of
/// <see cref="AntiforgeryExtensions.UseAntiforgeryGenerator(IApplicationBuilder)"/>: no token is
/// issued, so no cookie is written and the response's own headers are left untouched.
/// </summary>
/// <remarks>
/// <para>
/// This turns off <em>issuing</em> a token, not <em>validating</em> one. Antiforgery validation is
/// governed by <c>DisableAntiforgery()</c> / <c>[RequireAntiforgeryToken]</c> and is unaffected: an
/// endpoint carrying both this metadata and a validation requirement still validates.
/// </para>
/// <para>
/// Implement it on your own metadata type, or use <see cref="SkipXsrfTokenAttribute"/> /
/// <see cref="SkipXsrfTokenExtensions.SkipXsrfToken{TBuilder}(TBuilder)"/>.
/// </para>
/// </remarks>
public interface ISkipXsrfTokenMetadata
{
}

/// <summary>
/// Keeps a controller or action out of the XSRF-token mint. See <see cref="ISkipXsrfTokenMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// Meant for public, shared-cacheable responses nobody submits a form from: badges, feeds, Open Graph
/// images, <c>.well-known</c> documents. Such a response keeps its <c>Cache-Control: public</c>,
/// gets no per-user <c>Set-Cookie</c>, and costs no DataProtection round trip. Authentication,
/// authorization, CORS, rate limiting and every other middleware still run, unlike with
/// <c>ShortCircuit()</c>.
/// </para>
/// <para>
/// Never put it on the SPA's HTML entry point or on an endpoint the SPA calls to refresh its token.
/// The SPA then has no token to send, and every mutating request after it is rejected.
/// </para>
/// <para>
/// A skipped response also loses the <c>X-Frame-Options: SAMEORIGIN</c> the antiforgery system adds
/// as a side effect. That does not matter for an image or a feed. For an HTML page, set the header yourself.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipXsrfTokenAttribute : Attribute, ISkipXsrfTokenMetadata
{
}

/// <summary>
/// Endpoint conventions for keeping minimal-API endpoints and route groups out of the XSRF-token mint.
/// </summary>
public static class SkipXsrfTokenExtensions
{
	/// <summary>
	/// Issues no XSRF token for this endpoint, or every endpoint in this group. Validation is
	/// unaffected. See <see cref="SkipXsrfTokenAttribute"/> for what skipping drops and where it must
	/// not be used.
	/// </summary>
	public static TBuilder SkipXsrfToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
	{
		ArgumentNullException.ThrowIfNull(builder);
		return builder.WithMetadata(new SkipXsrfTokenAttribute());
	}
}
