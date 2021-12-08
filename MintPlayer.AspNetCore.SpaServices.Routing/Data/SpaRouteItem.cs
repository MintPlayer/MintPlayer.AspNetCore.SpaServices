using System;
using System.Collections.Generic;

namespace MintPlayer.AspNetCore.SpaServices.Routing.Data
{
    public interface ISpaRouteItem
    {
        string Name { get; set; }
        string FullName { get; set; }
        string Path { get; set; }
        string FullPath { get; set; }
        List<ISpaRouteItem> Routes { get; set; }
    }

    internal class SpaRouteItem : ISpaRouteItem, Prerendering.Services.ISpaRouteBuilder
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

        public Prerendering.Services.ISpaRouteBuilder Route(string path, string name)
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

        public Prerendering.Services.ISpaRouteBuilder Group(string path, string name, Action<Prerendering.Services.ISpaRouteBuilder> builder)
        {
            var group = new SpaRouteItem
            {
                Path = path,
                Name = name,
                FullName = $"{FullName}-{name}",
                FullPath = string.IsNullOrEmpty(path) ? FullPath : $"{FullPath}/{path}"
            };
            builder(group);
            Routes.Add(group);
            return this;
        }

        public override string ToString()
        {
            return FullName;
        }
    }
}
