// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace MintPlayer.AspNetCore.SpaServices.Utils;

internal static class RegexHelpers {

	private static readonly Regex AnsiColorRegex = new Regex("\x001b\\[[0-9;]*m", RegexOptions.None, TimeSpan.FromSeconds(1));
	internal static string StripAnsiColors(string line)
		=> AnsiColorRegex.Replace(line, string.Empty);
}
