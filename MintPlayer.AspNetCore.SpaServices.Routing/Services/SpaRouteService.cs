using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net;

namespace MintPlayer.AspNetCore.SpaServices.Routing;

public interface ISpaRouteService
{
	/// <summary>Returns the SPA route (if any) that matches the requested URL.</summary>
	/// <param name="httpContext">The current HTTP context</param>
	Task<SpaRoute> GetCurrentRoute(HttpContext httpContext);

	Task Redirect(HttpContext context, string routeName, Dictionary<string, object> parameters);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a k
	Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	Task<string> GenerateUrl<T>(string routeName, T parameters);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <param name="routeName">Name of the SPA route</param>
	/// <param name="parameters">Dictionary containing a key-value mapping for the parameters</param>
	/// <param name="httpContext">Current HTTP context</param>
	Task<string> GenerateUrl(string routeName, Dictionary<string, object> parameters, HttpContext httpContext);

	/// <summary>Generates an url for a SPA route.</summary>
	/// <typeparam name="T">Some anonymous type.</typeparam>
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
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
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
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
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
	/// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
	/// <param name="protocol">The protocol for the URL, such as "http" or "https"</param>
	/// <param name="host">The host name for the URL</param>
	/// <param name="fragment">The hash fragment for the URL</param>
	Task<string> GenerateUrl<T>(string routeName, T parameters, string protocol, string host, string fragment);
}

internal class SpaRouteService : ISpaRouteService
{
	private readonly Regex rgx_keys = new Regex(@"\{(?<key>[^\{]+)\}");
	private readonly IServiceProvider serviceProvider;
	public SpaRouteService(IServiceProvider serviceProvider)
	{
		this.serviceProvider = serviceProvider;
	}

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
		context.Response.StatusCode = (int)HttpStatusCode.Moved;
		var url = await GenerateUrl(routeName, parameters);

		context.Response.OnStarting(() =>
		{
			context.Response.Redirect(url);
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
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
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
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
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
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
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
	/// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
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

		var urlWithoutQuery = rgx_keys.Replace($"/{route.FullPath}", m => parameters[m.Groups["key"].Value].ToString());
		var present_param_keys = rgx_keys.Matches(route.FullPath).Select(m => m.Groups["key"].Value);
		var excessive_param_keys = parameters.Keys.Except(present_param_keys);
		var query = string.Join('&', excessive_param_keys.Select((key) => $"{key}={parameters[key]}"));

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
					(index) => parameter_values[index]
				),
				QueryParameters = query == null
					? new Dictionary<string, string>()
					: query.Split('&').Select(t =>
					{
						var split = t.Split('=', 2);
						return new
						{
							Key = split[0],
							Value = split.Length > 1 ? split[1] : null
						};
					}).ToDictionary(t => t.Key, t => t.Value)
			};
		}
		else
		{
			return new SpaRoute
			{
				Name = match.FullName,
				Path = match.FullPath,
				Parameters = new Dictionary<string, string>(),
				QueryParameters = new Dictionary<string, string>()
			};
		}
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
		var wildcardString = rgx_keys.Replace(input, @"([^\/]+)");
		return wildcardString;
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

		var queryStart = path.LastIndexOf('?');
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
