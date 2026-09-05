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
	/// This is the only way to say "serve the shell, do not render" for a response that is otherwise
	/// an ordinary 200 — the status code cannot express it, because the status is 200 in every such
	/// case. Typical uses are rendering only for crawlers and serving the unrendered shell to
	/// everyone else, a per-route decision that a page is not worth prerendering, and a kill switch
	/// for when the render backend is unhealthy.
	/// </para>
	/// <para>
	/// It is <em>not</em> needed to return a non-200 status. A status assigned in
	/// <c>ISpaPrerenderingService.OnSupplyData</c> is visible to the middleware and is honoured, so a
	/// 404 is still prerendered and the rendered "not found" page is returned with its 404, and a
	/// redirect skips the render on its own. That was not always true: the middleware used to clear
	/// the response before writing, which forced callers to defer the status to a
	/// <see cref="HttpResponse.OnStarting(Func{Task})"/> callback, and a deferred status is invisible
	/// when the middleware decides whether to render — so they had to call this as well. See
	/// <see href="https://github.com/MintPlayer/MintPlayer.AspNetCore.SpaServices/issues/81">issue #81</see>.
	/// </para>
	/// <para>
	/// That older pattern still works, and this method is still the way to declare a deferred status
	/// change: a status assigned inside an <c>OnStarting</c> callback remains undetectable to the
	/// middleware, because the callback has not run yet. Assigning it directly in
	/// <c>OnSupplyData</c> is simpler and needs neither.
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
