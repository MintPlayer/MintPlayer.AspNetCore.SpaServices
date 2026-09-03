namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

/// <summary>
/// Extension methods that let request-scoped code opt out of server-side prerendering.
/// </summary>
public static class PrerenderingHttpContextExtensions
{
	internal const string SkipPrerenderingKey = "MintPlayer.SpaServices.Prerendering.Skip";

	/// <summary>
	/// Marks the current request so that <see cref="SpaPrerenderingExtensions.UseSpaPrerendering"/>
	/// passes the captured response through unchanged instead of prerendering it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This exists because the prerendering middleware decides whether to prerender by looking at
	/// <see cref="HttpResponse.StatusCode"/>, and a status code assigned from within a
	/// <see cref="HttpResponse.OnStarting(Func{Task})"/> callback is not yet visible at that point —
	/// the callback runs later, when the response actually starts. Code that defers its status that
	/// way (a redirect, for example) therefore has to say so explicitly, or the request is
	/// prerendered and the rendered body is thrown away.
	/// </para>
	/// <para>
	/// Deferred status changes made by code that does not call this method remain undetectable to
	/// the middleware; there is no general way to observe them in time.
	/// </para>
	/// </remarks>
	/// <param name="context">The current <see cref="HttpContext"/>.</param>
	public static void SkipPrerendering(this HttpContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.Items[SkipPrerenderingKey] = true;
	}

	/// <summary>
	/// Returns whether <see cref="SkipPrerendering"/> was called for the current request.
	/// </summary>
	/// <param name="context">The current <see cref="HttpContext"/>.</param>
	public static bool IsPrerenderingSkipped(this HttpContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		return context.Items.TryGetValue(SkipPrerenderingKey, out var value) && value is true;
	}
}
