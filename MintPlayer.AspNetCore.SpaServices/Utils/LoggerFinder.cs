// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;

namespace MintPlayer.AspNetCore.SpaServices.Utils;

internal static class LoggerFinder
{
    public static ILogger GetOrCreateLogger(
        IApplicationBuilder appBuilder,
        string logCategoryName)
    {
        // If the DI system gives us a logger, use it. Otherwise, set up a default one
        var loggerFactory = appBuilder.ApplicationServices.GetService<ILoggerFactory>();
        var logger = loggerFactory != null
            ? loggerFactory.CreateLogger(logCategoryName)
            : NullLogger.Instance;
        return logger;
    }
}
