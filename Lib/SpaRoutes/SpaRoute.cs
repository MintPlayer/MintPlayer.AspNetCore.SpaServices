using System.Collections.Generic;

namespace MintPlayer.AspNetCore.SpaServices.Routing
{
    public class SpaRoute
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
    }
}
