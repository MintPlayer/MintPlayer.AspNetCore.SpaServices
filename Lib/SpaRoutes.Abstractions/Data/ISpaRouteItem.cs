using System.Collections.Generic;

namespace Spa.SpaRoutes.Abstractions
{
    public interface ISpaRouteItem
    {
        string Name { get; set; }
        string FullName { get; set; }
        string Path { get; set; }
        string FullPath { get; set; }
        List<ISpaRouteItem> Routes { get; set; }
    }
}
