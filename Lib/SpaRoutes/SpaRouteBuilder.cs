using System;
using System.Collections.Generic;
using Spa.SpaRoutes.Data;
using Spa.SpaRoutes.Extensions;

namespace Spa.SpaRoutes
{
    public interface ISpaRouteBuilder
    {
        ISpaRouteBuilder Route(string path, string name);
        ISpaRouteBuilder Group(string path, string name, Action<ISpaRouteBuilder> builder);
    }

    public class SpaRouteBuilder : ISpaRouteBuilder
    {
        public SpaRouteBuilder()
        {
            Routes = new List<ISpaRouteItem>();
        }

        public List<ISpaRouteItem> Routes { get; private set; }

        public ISpaRouteBuilder Route(string path, string name)
        {
            var route = new SpaRouteItem
            {
                Path = path,
                Name = name,
                FullName = name,
                FullPath = path
            };
            Routes.Add(route);
            return this;
        }

        public ISpaRouteBuilder Group(string path, string name, Action<ISpaRouteBuilder> builder)
        {
            var group = new SpaRouteItem
            {
                Path = path,
                Name = name,
                FullName = name,
                FullPath = path
            };
            builder(group);
            Routes.Add(group);
            return this;
        }

        internal IEnumerable<ISpaRouteItem> Build()
        {
            var result = Routes.Flatten((item) => item.Routes);
            return result;
        }
    }
}
