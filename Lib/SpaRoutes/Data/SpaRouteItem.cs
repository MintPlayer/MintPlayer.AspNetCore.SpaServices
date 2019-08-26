using System;
using System.Collections.Generic;
using Spa.SpaRoutes.Abstractions;

namespace Spa.SpaRoutes.Data
{
    internal class SpaRouteItem : ISpaRouteItem, ISpaRouteBuilder
    {
        public SpaRouteItem()
        {
            Routes = new List<ISpaRouteItem>();
        }

        public string Name { get; set; }
        public string FullName { get; set; }
        public string Path { get; set; }
        public string FullPath { get; set; }
        public List<ISpaRouteItem> Routes { get; set; }

        public ISpaRouteBuilder Route(string path, string name)
        {
            var route = new SpaRouteItem
            {
                Path = path,
                Name = name,
                FullName = $"{FullName}-{name}",
                FullPath = string.IsNullOrEmpty(path) ? FullPath : $"{FullPath}/{path}"
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
                FullName = $"{FullName}-{name}",
                FullPath = string.IsNullOrEmpty(path) ? FullPath : $"{FullPath}/{path}"
            };
            builder(group);
            return this;
        }

        public override string ToString()
        {
            return FullName;
        }
    }
}
