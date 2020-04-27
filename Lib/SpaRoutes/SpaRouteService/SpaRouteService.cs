using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Spa.SpaRoutes.CurrentSpaRoute
{
    public interface ISpaRouteService
    {
        /// <summary>Returns the SPA route (if any) that matches the requested URL.</summary>
        SpaRoute GetCurrentRoute();

        /// <summary>Generates an url for a SPA route.</summary>
        /// <param name="routeName">Name of the SPA route</param>
        /// <param name="parameters">Dictionary containing the key-value mapping for the parameters of the SPA route.</param>
        string GenerateUrl(string routeName, Dictionary<string, object> parameters);

        string GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol);
        string GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host);
        string GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host, string fragment);

        /// <summary>Generates an url for a SPA route.</summary>
        /// <typeparam name="T">Some anonymous type.</typeparam>
        /// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
        /// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
        string GenerateUrl<T>(string routeName, T parameters);
        string GenerateUrl<T>(string routeName, T parameters, string protocol);
        string GenerateUrl<T>(string routeName, T parameters, string protocol, string host);
        string GenerateUrl<T>(string routeName, T parameters, string protocol, string host, string fragment);
    }

    internal class SpaRouteService : ISpaRouteService
    {
        private readonly SpaRouteBuilder routeBuilder;
        private readonly IHttpContextAccessor httpContextAccessor;
        public SpaRouteService(SpaRouteBuilder routeBuilder/*, IHttpContextAccessor httpContextAccessor*/)
        {
            this.routeBuilder = routeBuilder;
            //this.httpContextAccessor = httpContextAccessor;
        }

        #region Private
        #region Fields
        private const string rgx_keys = @"(?<=\{)[a-zA-Z0-9]+(?=\})";
        private IEnumerable<Data.ISpaRouteItem> spaRouteItems;
        #endregion
        #region Methods

        /// <summary>Ensures that the routeBuilder delegate has been executed.</summary>
        private void EnsureSpaRoutesBuilt()
        {
            if (spaRouteItems == null)
                spaRouteItems = routeBuilder.Build();
        }

        /// <summary>Tests if an url [/manage/person/3/edit] matches a placeholder-url [/manage/person/{person_id}/edit].</summary>
        /// <param name="path">The visited URL</param>
        /// <param name="route">URL of the route containing placeholders [/manage/person/{person_id}/edit]</param>
        private bool IsMatch(string path, string route)
        {
            // Remove query-string and hash-fragment before matching.
            var newPath = path.Substring(0, path.LastIndexOf('#')).Substring(path.LastIndexOf('?'));

            var formatted_route = PlaceholderString2WildcardString(route);
            return Regex.IsMatch(newPath, $"^/{formatted_route}$");
        }

        /// <summary>Converts an url with placeholders [/manage/person/{person_id}/edit] to a string ready to be used as Regex [/manage/person/(.+)/edit].</summary>
        /// <param name="input">Placeholder string</param>
        private string PlaceholderString2WildcardString(string input)
        {
            var rgx = @"\{[a-zA-Z0-9]+\}";
            var replace = @"([^\/]+)";
            var wildcardString = Regex.Replace(input, rgx, replace);
            return wildcardString;
        }

        /// <summary>Retrieves the url visited by the user.</summary>
        /// <param name="context">Http Context</param>
        private string GetCurrentPath(HttpContext context)
        {
            // For an angular app this instruction returns
            // - The correct path in Development mode
            // - index.html in Production mode
            //return context.Request.Path;

            // The RawTarget private property contains the real path visited by the user at any time.
            var fc = context.Features.GetType();
            var rt = fc.GetProperty("RawTarget");
            var url = (string)rt.GetValue(context.Features);
            return url;
        }

        /// <summary>Base method for generating an URL.</summary>
        /// <param name="routeName">Name of the route</param>
        /// <param name="parameters">Dictionary containing the paramters.</param>
        private string GenerateUrlWithDictionary(string routeName, IDictionary<string, object> parameters)
        {
            EnsureSpaRoutesBuilt();

            var route = spaRouteItems.FirstOrDefault(r => r.FullName == routeName);
            if (route == null) throw new System.Exception($"Route with name {routeName} not found.");

            var rgx_keys = new Regex(@"\{(?<key>[a-zA-Z0-9]+)\}");
            var urlWithoutQuery = rgx_keys.Replace($"/{route.FullPath}", m => parameters[m.Groups["key"].Value].ToString());
            var present_param_keys = rgx_keys.Matches(route.FullPath).Select(m => m.Groups["key"].Value);
            var excessive_param_keys = parameters.Keys.Except(present_param_keys);

            if (excessive_param_keys.Any())
            {
                return $"{urlWithoutQuery}?{string.Join('&', excessive_param_keys.Select((key) => $"{key}={parameters[key]}"))}";
            }
            else
            {
                return urlWithoutQuery;
            }
        }

        #endregion
        #endregion

        #region Generate Url
        public string GenerateUrl(string routeName, Dictionary<string, object> parameters)
        {
            return GenerateUrlWithDictionary(routeName, parameters);
        }

        public string GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol)
        {
            var url = GenerateUrl(routeName, parameters);
            return $"{protocol}://{httpContextAccessor.HttpContext.Request.Host}{url}";
        }

        public string GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host)
        {
            var url = GenerateUrl(routeName, parameters);
            return $"{protocol}://{host}{url}";
        }

        public string GenerateUrl(string routeName, Dictionary<string, object> parameters, string protocol, string host, string fragment)
        {
            var url = GenerateUrl(routeName, parameters);
            return $"{protocol}://{host}{url}#{fragment}";
        }

        /// <summary>Generates an url for a SPA route.</summary>
        /// <typeparam name="T">Some anonymous type.</typeparam>
        /// <param name="routeName">Name of the SPA route as defined in the AddSpaRoutes call.</param>
        /// <param name="parameters">Anonymous object containing the key-value mapping for the parameters of the SPA route.</param>
        public string GenerateUrl<T>(string routeName, T parameters)
        {
            var values = typeof(T).GetProperties().ToDictionary(p => p.Name, p => p.GetValue(parameters));
            return GenerateUrlWithDictionary(routeName, values);
        }

        public string GenerateUrl<T>(string routeName, T parameters, string protocol)
        {
            var url = GenerateUrl(routeName, parameters);
            return $"{protocol}://{httpContextAccessor.HttpContext.Request.Host}{url}";
        }

        public string GenerateUrl<T>(string routeName, T parameters, string protocol, string host)
        {
            var url = GenerateUrl(routeName, parameters);
            return $"{protocol}://{host}{url}";
        }

        public string GenerateUrl<T>(string routeName, T parameters, string protocol, string host, string fragment)
        {
            var url = GenerateUrl(routeName, parameters);
            return $"{protocol}://{host}{url}#{fragment}";
        }
        #endregion

        /// <summary>Returns the SPA route (if any) that matches the requested URL.</summary>
        /// <param name="httpContext">The current HTTP context</param>
        public SpaRoute GetCurrentRoute()
        {
            EnsureSpaRoutesBuilt();

            // Find the SPA route for the current request
            var match = spaRouteItems.FirstOrDefault(r => IsMatch(GetCurrentPath(httpContextAccessor.HttpContext), r.FullPath));

            if (match == null)
            {
                return null;
            }
            else if (!string.IsNullOrEmpty(match.FullPath))
            {
                // Get parameter names
                
                var parameter_keys = Regex.Matches(match.FullPath, rgx_keys).Select(m => m.Value).ToList(); // [id, ...]

                var rgx_values = PlaceholderString2WildcardString(match.FullPath);
                var parameter_match = Regex.Match(GetCurrentPath(httpContextAccessor.HttpContext), rgx_values);
                if (!parameter_match.Success) throw new System.Exception("Unexpected exception: parameter match should be successful");

                var parameter_groups = new Group[parameter_match.Groups.Count];
                parameter_match.Groups.CopyTo(parameter_groups, 0);

                var parameter_values = parameter_groups.Where(g => g.GetType() == typeof(Group)).Select(g => g.Value).ToList();
                if (parameter_keys.Count != parameter_values.Count) throw new System.Exception("Unexpected exception: number of keys and values should be equal");

                return new SpaRoute
                {
                    Name = match.FullName,
                    Path = match.FullPath,
                    Parameters = Enumerable.Range(0, parameter_keys.Count).ToDictionary(
                        (index) => parameter_keys[index],
                        (index) => parameter_values[index]
                    )
                };
            }
            else
            {
                return new SpaRoute { Name = match.FullName, Path = match.FullPath, Parameters = new Dictionary<string, string>() };
            }
        }
    }
}
