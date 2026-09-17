namespace MintPlayer.AspNetCore.SpaServices.Routing.Data;

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

	/// <summary>
	/// Joins a child segment onto this item's path.
	/// </summary>
	/// <remarks>
	/// <c>FullPath</c> is stored WITHOUT a leading slash: both <c>GenerateUrl</c> and the matcher
	/// prepend one themselves. Joining with a bare <c>$"{FullPath}/{path}"</c> broke that invariant
	/// whenever the parent's path was empty - a group declared as <c>Group("", "bare", ...)</c> left
	/// <c>FullPath</c> empty, so its children came out as <c>"/thing"</c> and then rendered as
	/// <c>"//thing"</c>. The matcher built <c>"^//thing$"</c> from the same value, so such a route
	/// could never match a real request either.
	/// </remarks>
	private string CombinePath(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return FullPath;
		}

		return string.IsNullOrEmpty(FullPath) ? path : $"{FullPath}/{path}";
	}

	public Prerendering.Services.ISpaRouteBuilder Route(string path, string name)
	{
		var route = new SpaRouteItem
		{
			Path = path,
			Name = name,
			FullName = $"{FullName}-{name}",
			FullPath = CombinePath(path)
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
			FullPath = CombinePath(path)
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
