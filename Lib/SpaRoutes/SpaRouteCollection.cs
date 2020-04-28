using Spa.SpaRoutes.Data;
using System.Collections.Generic;

namespace SpaRoutes
{
    public interface ISpaRouteCollection : IList<ISpaRouteItem>
    {

    }

    internal class SpaRouteCollection : List<ISpaRouteItem>, ISpaRouteCollection
    {

    }
}
