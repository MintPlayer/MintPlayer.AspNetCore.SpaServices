using Microsoft.AspNetCore.Http;
using Spa.SpaRoutes.CurrentSpaRoute.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Spa.SpaRoutes.CurrentSpaRoute
{
    internal class CurrentSpaRoute : ICurrentSpaRoute
    {
        public CurrentSpaRoute(SpaRouteBuilder routeBuilder)
        {
            this.routeBuilder = routeBuilder;
        }

        private SpaRouteBuilder routeBuilder;

        public SpaRoute GetCurrentRoute(HttpContext httpContext)
        {
            var allRoutes = routeBuilder.Build();
            var match = allRoutes.FirstOrDefault(r => IsMatch(httpContext.Request.Path, r.FullPath));

            if (match == null)
            {
                return null;
            }
            else if (!string.IsNullOrEmpty(match.FullPath))
            {
                // Get parameter names
                var rgx_keys = @"(?<=\{)[a-zA-Z0-9]+(?=\})";
                var parameter_keys = Regex.Matches(match.FullPath, rgx_keys).Select(m => m.Value).ToList(); // [id, ...]

                var rgx_values = PlaceholderString2WildcardString(match.FullPath);
                var parameter_match = Regex.Match(httpContext.Request.Path, rgx_values);
                if (!parameter_match.Success) throw new System.Exception("Unexpected exception: parameter match should be successful");

                var parameter_values = parameter_match.Groups.Where(g => g.GetType() == typeof(Group)).Select(g => g.Value).ToList();
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

        private bool IsMatch(string path, string route)
        {
            var formatted_route = PlaceholderString2WildcardString(route);
            return Regex.IsMatch(path, $"^/{formatted_route}$");
        }

        private string PlaceholderString2WildcardString(string input)
        {
            var rgx = @"\{[a-zA-Z0-9]+\}";
            var replace = "(.*)";
            var wildcardString = Regex.Replace(input, rgx, replace);
            return wildcardString;
        }
    }
}
