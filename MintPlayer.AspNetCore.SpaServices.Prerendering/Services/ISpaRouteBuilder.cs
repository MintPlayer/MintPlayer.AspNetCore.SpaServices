namespace MintPlayer.AspNetCore.SpaServices.Prerendering.Services
{
    public interface ISpaRouteBuilder
    {
        ISpaRouteBuilder Route(string path, string name);
        ISpaRouteBuilder Group(string path, string name, Action<ISpaRouteBuilder> builder);
    }
}
