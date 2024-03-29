// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace MintPlayer.AspNetCore.NodeServices;

/// <summary>
/// Supplies INodeServices instances.
/// </summary>
public static class NodeServicesFactory
{
	/// <summary>
	/// Create an <see cref="INodeServices"/> instance according to the supplied options.
	/// </summary>
	/// <param name="options">Options for creating the <see cref="INodeServices"/> instance.</param>
	/// <returns>An <see cref="INodeServices"/> instance.</returns>
	public static INodeServices CreateNodeServices(NodeServicesOptions options)
	{
		if (options == null)
		{
			throw new ArgumentNullException(nameof(options));
		}

		return new NodeServicesImpl(options.NodeInstanceFactory);
	}
}
