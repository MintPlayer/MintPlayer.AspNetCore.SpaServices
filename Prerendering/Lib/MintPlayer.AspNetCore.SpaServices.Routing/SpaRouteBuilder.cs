using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing.Data;
using MintPlayer.AspNetCore.SpaServices.Routing.Extensions;

namespace MintPlayer.AspNetCore.SpaServices.Routing;

internal class SpaRouteBuilder : ISpaRouteBuilder
{
	internal SpaRouteBuilder()
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
