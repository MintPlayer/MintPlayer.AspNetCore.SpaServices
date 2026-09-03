using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.AspNetCore.SpaServices.Routing;

public interface ISpaRouteService
{
	/// <summary>Returns the SPA route (if any) that matches the requested URL.</summary>
	/// <param name="httpContext">The current HTTP context</param>
	Task<SpaRoute> GetCurrentRoute(HttpContext httpContext);

	/// <summary>Correctly sets up a redirect when used from the <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService"/></summary>
	/// <param name="context"><see cref="HttpContext"/> from the <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.OnSupplyData(HttpContext, IDictionary{string, object})"/></param>
	/// <param name="routeName">Name of the route</param>
	/// <param name="parameters">Parameters</param>
	/// <returns></returns>
	Task Redirect<T>(HttpContext context, string routeName, T parameters);

	/// <summary>Correctly sets up a redirect when used from the <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService"/></summary>
	/// <param name="context"><see cref="HttpContext"/> from the <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.OnSupplyData(HttpContext, IDictionary{string, object})"/></param>
	/// <param name="routeName">Name of the route</param>
	/// <param name="parameters">Parameters</param>
	/// <returns></returns>
	Task Redirect(HttpContext context, string routeName, Dictionary<string, object> parameters);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	Task<string> GenerateUrl<T>(string routeName, T parameters);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="httpContext">Current HTTP context</param>
	Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, HttpContext httpContext);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="httpContext">Current HTTP context</param>
	Task<string> GenerateUrl<T>(string routeName, T parameters, HttpContext httpContext);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	/// <param name="fragment">The hash fragment for the URL</param>
	Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host, string fragment);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	/// <param name="fragment">The hash fragment for the URL</param>
	Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host, string fragment);
}

[Register(typeof(ISpaRouteService), ServiceLifetime.Singleton, "AddSpaRouteServices")]
internal partial class SpaRouteService : ISpaRouteService
{
	private readonly Regex rgx_keys = new Regex(@"\{(?<key>[^\{]+)\}");
	[Inject] private readonly IServiceProvider serviceProvider;

	/// <summary>Build result</summary>
	private IEnumerable<Data.ISpaRouteItem> spaRouteItems;

	/// <summary>Ensures that the routeBuilder delegate has been executed.</summary>
	private async Task EnsureSpaRoutesBuilt()
	{
		if (spaRouteItems == null)
		{
			using (var scope = serviceProvider.CreateScope())
			{
				var routes = new SpaRouteBuilder();
				var spaPrerenderingService = scope.ServiceProvider.GetRequiredService<Prerendering.Services.ISpaPrerenderingService>();
				await spaPrerenderingService.BuildRoutes(routes);
				spaRouteItems = routes.Build();
			}
		}
	}

	public async Task Redirect(HttpContext context, string routeName, Dictionary<string, object> parameters)
	{
		var url = await GenerateUrl(routeName, parameters);

		// The middleware decides whether to prerender before this callback runs, so it cannot
		// see the status set below. Tell it explicitly, or the page is prerendered and thrown away.
		context.SkipPrerendering();

		context.Response.OnStarting(() =>
		{
			// permanent: true, because Response.Redirect defaults to 302 and would otherwise
			// overwrite a status code assigned before this callback runs.
			context.Response.Redirect(url, permanent: true);
			return Task.CompletedTask;
		});
	}

	public async Task Redirect<T>(HttpContext context, string routeName, T parameters)
	{
		var url = await GenerateUrl(routeName, parameters);

		// The middleware decides whether to prerender before this callback runs, so it cannot
		// see the status set below. Tell it explicitly, or the page is prerendered and thrown away.
		context.SkipPrerendering();

		context.Response.OnStarting(() =>
		{
			// permanent: true, because Response.Redirect defaults to 302 and would otherwise
			// overwrite a status code assigned before this callback runs.
			context.Response.Redirect(url, permanent: true);
			return Task.CompletedTask;
		});
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a k
	public async Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters)
	{
		var url = await GenerateUrlBase(routeName, parameters);
		return url;
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	public async Task<string> GenerateUrl<T>(string routeName, T parameters)
	{
		var values = typeof(T).GetProperties().ToDictionary(p => p.Name, p => p.GetValue(parameters));
		var url = await GenerateUrlBase(routeName, values);
		return url;
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="httpContext">Current HTTP context</param>
	public async Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, HttpContext httpContext)
	{
		var path = await GenerateUrl(routeName, parameters);
		return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}{path}";
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="httpContext">Current HTTP context</param>
	public async Task<string> GenerateUrl<T>(string routeName, T parameters, HttpContext httpContext)
	{
		var path = await GenerateUrl(routeName, parameters);
		return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}{path}";
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	public async Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host)
	{
		var path = await GenerateUrl(routeName, parameters);
		return $"{protocol}://{host}{path}";
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	public async Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host)
	{
		var path = await GenerateUrl(routeName, parameters);
		return $"{protocol}://{host}{path}";
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	/// <param name="fragment">The hash fragment for the URL</param>
	public async Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host, string fragment)
	{
		var path = await GenerateUrl(routeName, parameters);
		return $"{protocol}://{host}{path}#{fragment}";
	}

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route, as declared in <see cref="MintPlayer.AspNetCore.SpaServices.Prerendering.Services.ISpaPrerenderingService.BuildRoutes(ISpaRouteBuilder)"/>.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	/// <param name="fragment">The hash fragment for the URL</param>
	public async Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host, string fragment)
	{
		var path = await GenerateUrl(routeName, parameters);
		return $"{protocol}://{host}{path}#{fragment}";
	}



	private async Task<string> GenerateUrlBase(string routeName, IDictionary<string, object> parameters)
	{
		await EnsureSpaRoutesBuilt();

		var route = spaRouteItems.FirstOrDefault(r => r.FullName == routeName);
		if (route == null)
		{
			throw new Exceptions.SpaRouteNotFoundException(routeName);
		}

		// Values are percent-encoded on the way out and decoded again by GetCurrentRoute, so a value
		// containing '/', '&', '?' or a space survives a generate/parse round-trip instead of
		// silently changing the shape of the URL.
		var urlWithoutQuery = rgx_keys.Replace($"/{route.FullPath}", m => Escape(parameters[m.Groups["key"].Value]));
		var present_param_keys = rgx_keys.Matches(route.FullPath).Select(m => m.Groups["key"].Value);
		var excessive_param_keys = parameters.Keys.Except(present_param_keys);
		var query = string.Join('&', excessive_param_keys.Select((key) => $"{Uri.EscapeDataString(key)}={Escape(parameters[key])}"));

		if (excessive_param_keys.Any())
		{
			return $"{urlWithoutQuery}?{query}";
		}
		else
		{
			return urlWithoutQuery;
		}
	}

	/// <summary>Returns the SPA route (if any) that matches the requested URL.</summary>
	/// <param name="httpContext">The current HTTP context</param>
	public async Task<SpaRoute> GetCurrentRoute(HttpContext httpContext)
	{
		await EnsureSpaRoutesBuilt();

		// Find the SPA route for the current request
		var match = spaRouteItems.FirstOrDefault(r => IsMatch(GetCurrentPath(httpContext), r.FullPath));

		if (match == null)
		{
			return null;
		}
		else if (!string.IsNullOrEmpty(match.FullPath))
		{
			// Get current path
			string url, query;
			GetCurrentPath(httpContext, out url, out query);

			// Get parameter names
			var parameter_keys = rgx_keys.Matches(match.FullPath).Select(m => m.Groups["key"].Value).ToList(); // [id, ...]

			var rgx_values = PlaceholderString2WildcardString(match.FullPath);
			var parameter_match = Regex.Match(url, rgx_values);
			Debug.Assert(parameter_match.Success, "Unexpected exception: parameter match should be successful");

			var parameter_groups = new Group[parameter_match.Groups.Count];
			parameter_match.Groups.CopyTo(parameter_groups, 0);

			var parameter_values = parameter_groups.Where(g => g.GetType() == typeof(Group)).Select(g => g.Value).ToList();
			Debug.Assert(parameter_keys.Count == parameter_values.Count, "Unexpected exception: number of keys and values should be equal");

			return new SpaRoute
			{
				Name = match.FullName,
				Path = match.FullPath,
				Parameters = Enumerable.Range(0, parameter_keys.Count).ToDictionary(
					(index) => parameter_keys[index],
					(index) => Unescape(parameter_values[index])
				),
				QueryParameters = ParseQuery(query)
			};
		}
		else
		{
			// The route matched on an empty path (the root route). It has no parameters to extract,
			// but it still has a query string - reading it here keeps /?a=b consistent with every
			// other route rather than silently discarding the query.
			GetCurrentPath(httpContext, out _, out var rootQuery);

			return new SpaRoute
			{
				Name = match.FullName,
				Path = match.FullPath,
				Parameters = new Dictionary<string, string>(),
				QueryParameters = ParseQuery(rootQuery)
			};
		}
	}

	/// <summary>
	/// Decodes a percent-encoded value taken from the URL.
	/// <para>
	/// Note that '+' is left alone rather than being read as a space. GenerateUrl encodes a space as
	/// %20, so the round-trip is symmetric either way, and treating '+' as a space would corrupt a
	/// value that legitimately contains one.
	/// </para>
	/// </summary>
	private static string Unescape(string value)
		=> value == null ? null : Uri.UnescapeDataString(value);

	/// <summary>Percent-encodes a parameter value for use in a URL.</summary>
	/// <param name="value">The value to encode. A <c>null</c> encodes to an empty string.</param>
	private static string Escape(object value)
		=> value == null ? string.Empty : Uri.EscapeDataString(value.ToString());

	/// <summary>Parses a raw query string into its key/value pairs.</summary>
	/// <param name="query">The query string without its leading '?', or <c>null</c> when absent.</param>
	/// <returns>The query parameters. A key with no '=' maps to <c>null</c>.</returns>
	private static Dictionary<string, string> ParseQuery(string query)
	{
		var result = new Dictionary<string, string>();

		if (string.IsNullOrEmpty(query))
		{
			return result;
		}

		foreach (var pair in query.Split('&'))
		{
			var split = pair.Split('=', 2);

			// A repeated key is legal in a URL, so last-one-wins rather than throwing. An indexer
			// assignment is what makes this differ from ToDictionary.
			result[Unescape(split[0])] = split.Length > 1 ? Unescape(split[1]) : null;
		}

		return result;
	}

	/// <summary>Tests if an url [/manage/person/3/edit] matches a placeholder-url [/manage/person/{person_id}/edit].</summary>
	/// <param name="path">The visited URL</param>
	/// <param name="route">URL of the route containing placeholders [/manage/person/{person_id}/edit]</param>
	private bool IsMatch(string path, string route)
	{
		var formatted_route = PlaceholderString2WildcardString(route);
		return Regex.IsMatch(path, $"^/{formatted_route}$");
	}

	/// <summary>Converts an url with placeholders [/manage/person/{person_id}/edit] to a string ready to be used as Regex [/manage/person/(.+)/edit].</summary>
	/// <param name="input">Placeholder string</param>
	private string PlaceholderString2WildcardString(string input)
	{
		// Only the {placeholders} become capture groups; everything between them is literal text and
		// is escaped. Interpolating the raw text made every regex metacharacter in a route path
		// active, so a route "a.b" matched "/axb" and a route containing '(' threw at match time.
		var wildcardString = new StringBuilder();
		var literalStart = 0;

		foreach (Match placeholder in rgx_keys.Matches(input))
		{
			wildcardString.Append(Regex.Escape(input[literalStart..placeholder.Index]));
			wildcardString.Append(@"([^/]+)");
			literalStart = placeholder.Index + placeholder.Length;
		}

		wildcardString.Append(Regex.Escape(input[literalStart..]));

		return wildcardString.ToString();
	}

	/// <summary>Retrieves the url visited by the user.</summary>
	/// <param name="context">Http Context</param>
	private string GetCurrentPath(HttpContext context)
	{
		string url, query;
		GetCurrentPath(context, out url, out query);
		return url;
	}

	/// <summary>Retrieves the url visited by the user.</summary>
	/// <param name="context">Http Context</param>
	private void GetCurrentPath(HttpContext context, out string url, out string query)
	{
		// For an angular app the context.Request.Path instruction returns
		// - The correct path in Development mode
		// - index.html in Production mode

		// The RawTarget private property contains the real path visited by the user at any time.
		var path = (string)context.Features.GetType().GetProperty("RawTarget").GetValue(context.Features);

		// The query starts at the FIRST '?' (RFC 3986 3.4); any later '?' is part of the query
		// itself. Splitting on the last one put the leading part of the query inside the path, where
		// it ended up captured as a route parameter.
		var queryStart = path.IndexOf('?');
		if (queryStart == -1)
		{
			url = path;
			query = null;
		}
		else
		{
			url = path.Substring(0, queryStart);
			query = path.Substring(queryStart + 1);
		}
	}
}
