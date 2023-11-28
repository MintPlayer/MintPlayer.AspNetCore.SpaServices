// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MintPlayer.AspNetCore.SpaServices.Core;

namespace MintPlayer.AspNetCore.SpaServices.Internal;

internal sealed class DefaultSpaBuilder : Abstractions.ISpaBuilder
{
	public IApplicationBuilder ApplicationBuilder { get; }

	public Abstractions.ISpaOptions Options { get; }

	public DefaultSpaBuilder(IApplicationBuilder applicationBuilder, SpaOptions options)
	{
		ApplicationBuilder = applicationBuilder
			?? throw new ArgumentNullException(nameof(applicationBuilder));

		Options = options
			?? throw new ArgumentNullException(nameof(options));
	}
}
