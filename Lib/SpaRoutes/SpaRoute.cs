using System.Collections.Generic;

namespace Spa.SpaRoutes
{
    public class SpaRoute
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
    }
}
