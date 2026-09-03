// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using MintPlayer.AspNetCore.NodeServices;

namespace MintPlayer.AspNetCore.SpaServices.Prerendering;

/// <summary>
/// Performs server-side prerendering by invoking code in Node.js.
/// </summary>
internal static class Prerenderer
{
	private static readonly object CreateNodeScriptLock = new object();

	private static StringAsTempFile NodeScript;

	/// <summary>
	/// Performs server-side prerendering by invoking code in Node.js.
	/// </summary>
	/// <param name="applicationBasePath">The root path to your application. This is used when resolving project-relative paths.</param>
	/// <param name="nodeServices">The instance of <see cref="INodeServices"/> that will be used to invoke JavaScript code.</param>
	/// <param name="applicationStoppingToken">A token that indicates when the host application is stopping.</param>
	/// <param name="bootModule">The path to the JavaScript file containing the prerendering logic.</param>
	/// <param name="requestAbsoluteUrl">The URL of the currently-executing HTTP request. This is supplied to the prerendering code.</param>
	/// <param name="requestPathAndQuery">The path and query part of the URL of the currently-executing HTTP request. This is supplied to the prerendering code.</param>
	/// <param name="customDataParameter">An optional JSON-serializable parameter to be supplied to the prerendering code.</param>
	/// <param name="timeoutMilliseconds">The maximum duration to wait for prerendering to complete.</param>
	/// <param name="requestPathBase">The PathBase for the currently-executing HTTP request.</param>
	/// <param name="requestCancellationToken">
	/// A token that cancels this single render - typically the request's abort token linked with
	/// <paramref name="applicationStoppingToken"/>. Kept separate from
	/// <paramref name="applicationStoppingToken"/> on purpose: that one governs the lifetime of the
	/// process-wide temp file created by <see cref="GetNodeScriptFilename"/>, so a request-scoped
	/// token passed there would delete the shared prerenderer script as soon as the first request
	/// finished, breaking every render afterwards.
	/// </param>
	/// <returns></returns>
	public static Task<RenderToStringResult> RenderToString(
		string applicationBasePath,
		INodeServices nodeServices,
		CancellationToken applicationStoppingToken,
		JavaScriptModuleExport bootModule,
		string requestAbsoluteUrl,
		string requestPathAndQuery,
		object customDataParameter,
		int timeoutMilliseconds,
		string requestPathBase,
		CancellationToken requestCancellationToken)
	{
		// Note the leading-token overload: the one without it invokes with CancellationToken.None,
		// and because both end in "params object[] args" picking the wrong one compiles silently.
		return nodeServices.InvokeExportAsync<RenderToStringResult>(
			requestCancellationToken,
			GetNodeScriptFilename(applicationStoppingToken),
			"renderToString",
			applicationBasePath,
			bootModule,
			requestAbsoluteUrl,
			requestPathAndQuery,
			customDataParameter,
			timeoutMilliseconds,
			requestPathBase);
	}

	private static string GetNodeScriptFilename(CancellationToken applicationStoppingToken)
	{
		lock (CreateNodeScriptLock)
		{
			if (NodeScript == null)
			{
				var script = EmbeddedResourceReader.Read(typeof(Prerenderer), "/Content/Node/prerenderer.js");
				NodeScript = new StringAsTempFile(script, applicationStoppingToken); // Will be cleaned up on process exit
			}
		}

		return NodeScript.FileName;
	}
}
