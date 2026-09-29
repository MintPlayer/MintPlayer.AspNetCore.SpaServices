// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace MintPlayer.AspNetCore.NodeServices;

internal static class TaskExtensions
{
	// Both continuations rethrow through GetAwaiter().GetResult(): a faulted task has to surface as its own
	// exception, the same as awaiting it directly would. A bare "_ => { }" would turn a fault into success,
	// and "t => t.Result" would wrap it in an AggregateException.

	public static Task OrThrowOnCancellation(this Task task, CancellationToken cancellationToken)
	{
		return task.IsCompleted
			? task // If the task is already completed, no need to wrap it in a further layer of task
			: task.ContinueWith(
				t => t.GetAwaiter().GetResult(), // If the task completes, allow execution to continue (or rethrow its fault)
				cancellationToken,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
	}

	public static Task<T> OrThrowOnCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
	{
		return task.IsCompleted
			? task // If the task is already completed, no need to wrap it in a further layer of task
			: task.ContinueWith(
				t => t.GetAwaiter().GetResult(), // If the task completes, pass through its result (or rethrow its fault)
				cancellationToken,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
	}
}
