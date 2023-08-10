using System;
using System.Linq;
using System.Collections.Generic;

namespace MintPlayer.AspNetCore.SpaServices.Routing.Extensions;

internal static class IEnumerable
{
	internal static IEnumerable<T> Flatten<T>(this IEnumerable<T> e, Func<T, IEnumerable<T>> f)
	{
		return e.SelectMany(c => f(c).Flatten(f)).Concat(e);
	}
}
