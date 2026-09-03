// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MintPlayer.AspNetCore.SpaServices.Utils;

internal static class TaskTimeoutExtensions
{
    public static async Task WithTimeout(this Task task, TimeSpan timeoutDelay, string message)
    {
        if (task == await Task.WhenAny(task, Task.Delay(timeoutDelay)))
        {
            // await, not Wait(): Wait() wraps the fault in an AggregateException, which turned the
            // Angular CLI's own diagnostics ("Ensure that 'npm' is installed... Current PATH is...")
            // into "One or more errors occurred." by the time a caller or the error page saw it.
            await task;
        }
        else
        {
            throw new TimeoutException(message);
        }
    }

    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeoutDelay, string message)
    {
        if (task == await Task.WhenAny(task, Task.Delay(timeoutDelay)))
        {
            // await, not .Result, for the same reason as the non-generic overload above.
            return await task;
        }
        else
        {
            throw new TimeoutException(message);
        }
    }
}
