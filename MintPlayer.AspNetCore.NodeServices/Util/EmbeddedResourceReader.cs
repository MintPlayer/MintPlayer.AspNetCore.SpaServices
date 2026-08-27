// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Reflection;

namespace MintPlayer.AspNetCore.NodeServices;

/// <summary>
/// Contains methods for reading embedded resources.
/// </summary>
public static class EmbeddedResourceReader
{
	/// <summary>
	/// Reads the specified embedded resource from a given assembly.
	/// </summary>
	/// <param name="assemblyContainingType">Any <see cref="Type"/> in the assembly whose resource is to be read.</param>
	/// <param name="path">The path of the resource to be read.</param>
	/// <returns>The contents of the resource.</returns>
	public static string Read(Type assemblyContainingType, string path)
	{
		var asm = assemblyContainingType.GetTypeInfo().Assembly;
		var embeddedResourceName = asm.GetName().Name + path.Replace("/", ".");

		using (var stream = asm.GetManifestResourceStream(embeddedResourceName))
		{
			// Without this the null stream reaches StreamReader and surfaces as
			// "ArgumentNullException (Parameter 'stream')", which never names the resource that
			// was missing or the assembly it was looked for in.
			if (stream == null)
			{
				throw new InvalidOperationException(
					$"Embedded resource '{embeddedResourceName}' was not found in assembly " +
					$"'{asm.GetName().Name}'. Note the resource name is built from the supplied " +
					$"path, which is expected to start with '/'.");
			}

			using (var sr = new StreamReader(stream))
			{
				return sr.ReadToEnd();
			}
		}
	}
}
