// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.AspNetCore.SpaServices.Utils;

/// <summary>
/// Wraps a <see cref="StreamReader"/> to expose an evented API, issuing notifications
/// when the stream emits partial lines, completed lines, or finally closes.
/// </summary>
internal sealed partial class EventedStreamReader
{
	public delegate void OnReceivedChunkHandler(ArraySegment<char> chunk);
	public delegate void OnReceivedLineHandler(string line);
	public delegate void OnStreamClosedHandler();

	public event OnReceivedChunkHandler? OnReceivedChunk;
	public event OnReceivedLineHandler? OnReceivedLine;
	public event OnStreamClosedHandler? OnStreamClosed;

	/// <summary>
	/// How many emitted lines are kept for <see cref="WaitForMatch"/> to look back over. A dev server
	/// emits output for as long as it runs, so the history has to be bounded; this is generous enough
	/// that a caller would have to be a thousand lines late to miss its line.
	/// </summary>
	private const int MaxRememberedLines = 1000;

	private readonly StreamReader _streamReader;
	private readonly StringBuilder _linesBuffer;
	private readonly CancellationToken _cancellationToken;

	private readonly Lock _historyLock = new();
	private readonly List<string> _emittedLines = [];
	private int _matchCursor;
	private bool _streamClosed;

	public EventedStreamReader(StreamReader streamReader)
		: this(streamReader, CancellationToken.None)
	{
	}

	/// <param name="cancellationToken">
	/// Stops the read loop. Without one it runs until the stream ends, which for a dev server means
	/// for the lifetime of the process - outliving whatever created it.
	/// </param>
	public EventedStreamReader(StreamReader streamReader, CancellationToken cancellationToken)
	{
		_streamReader = streamReader ?? throw new ArgumentNullException(nameof(streamReader));
		_linesBuffer = new StringBuilder();
		_cancellationToken = cancellationToken;

		// Release waiters directly off the token rather than relying on the read loop noticing.
		// StreamReader.ReadAsync does not reliably observe a token - a process pipe parked in a read
		// stays parked - so without this, cancelling would leave every pending WaitForMatch hanging,
		// which is the failure this whole change exists to remove.
		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(OnClosed);
		}

		Task.Factory.StartNew(Run, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
	}

	/// <summary>
	/// Completes with the next line matching <paramref name="regex"/>, or faults with
	/// <see cref="EndOfStreamException"/> if the stream ends first.
	/// </summary>
	/// <remarks>
	/// "Next" means the next line after whatever a previous <see cref="WaitForMatch"/> consumed - not
	/// the next line after this call subscribes, which is what it used to mean.
	/// <para>
	/// The read loop emits every line of a chunk back to back. A caller issuing a second
	/// <see cref="WaitForMatch"/> from the continuation of the first therefore raced that loop, and
	/// when it lost, its line - and the stream-closed notification behind it - had already fired. The
	/// returned task then never completed at all: the caller hung rather than failed.
	/// </para>
	/// <para>
	/// That was not hypothetical. <c>AngularPrerendererBuilder</c> waits for its finished-regex
	/// <c>occurrences</c> times in sequence, so a build whose matching lines arrived in one read hung
	/// the prerenderer until its startup timeout. Scanning the already-emitted lines first removes the
	/// race; the closed check turns "wait forever" into an immediate, diagnosable failure.
	/// </para>
	/// </remarks>
	public Task<Match> WaitForMatch(Regex regex)
	{
		var tcs = new TaskCompletionSource<Match>();
		var completionLock = new object();

		OnReceivedLineHandler? onReceivedLineHandler = null;
		OnStreamClosedHandler? onStreamClosedHandler = null;

		void ResolveIfStillPending(Action applyResolution)
		{
			lock (completionLock)
			{
				if (!tcs.Task.IsCompleted)
				{
					OnReceivedLine -= onReceivedLineHandler;
					OnStreamClosed -= onStreamClosedHandler;
					applyResolution();
				}
			}
		}

		onReceivedLineHandler = line =>
		{
			var cleanedLine = AnsiCharacterRegex().Replace(line, string.Empty);
			var match = regex.Match(cleanedLine);
			if (match.Success)
			{
				ResolveIfStillPending(() =>
				{
					lock (_historyLock)
					{
						// Everything up to and including this line is consumed.
						_matchCursor = _emittedLines.Count;
					}
					tcs.SetResult(match);
				});
			}
		};

		onStreamClosedHandler = () =>
		{
			ResolveIfStillPending(() => tcs.SetException(new EndOfStreamException()));
		};

		lock (_historyLock)
		{
			// Did the line already go past? Only consider what no earlier WaitForMatch consumed.
			for (var i = _matchCursor; i < _emittedLines.Count; i++)
			{
				var cleanedLine = AnsiCharacterRegex().Replace(_emittedLines[i], string.Empty);
				var match = regex.Match(cleanedLine);
				if (match.Success)
				{
					_matchCursor = i + 1;
					return Task.FromResult(match);
				}
			}

			// Nothing further can arrive, so subscribing would mean waiting forever.
			if (_streamClosed)
			{
				return Task.FromException<Match>(new EndOfStreamException());
			}

			OnReceivedLine += onReceivedLineHandler;
			OnStreamClosed += onStreamClosedHandler;
		}

		return tcs.Task;
	}

	[GeneratedRegex(@"\x1B\[[0-9;]*[ -/]*[@-~]")]
	private partial Regex AnsiCharacterRegex();

	private async Task Run()
	{
		var buf = new char[8 * 1024];
		try
		{
			while (true)
			{
				var chunkLength = await _streamReader.ReadAsync(buf.AsMemory(), _cancellationToken);
				if (chunkLength == 0)
				{
					if (_linesBuffer.Length > 0)
					{
						OnCompleteLine(_linesBuffer.ToString());
						_linesBuffer.Clear();
					}

					break;
				}

				OnChunk(new ArraySegment<char>(buf, 0, chunkLength));

				int lineBreakPos;
				var startPos = 0;

				// get all the newlines
				while ((lineBreakPos = Array.IndexOf(buf, '\n', startPos, chunkLength - startPos)) >= 0 && startPos < chunkLength)
				{
					var length = lineBreakPos + 1 - startPos;
					_linesBuffer.Append(buf, startPos, length);
					OnCompleteLine(_linesBuffer.ToString());
					_linesBuffer.Clear();
					startPos = lineBreakPos + 1;
				}

				// get the rest
				if (lineBreakPos < 0 && startPos < chunkLength)
				{
					_linesBuffer.Append(buf, startPos, chunkLength - startPos);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Shutting down is not a failure. Fall through to OnClosed so anything still waiting on a
			// match is released rather than left hanging.
		}
		finally
		{
			OnClosed();
		}
	}

	private void OnChunk(ArraySegment<char> chunk)
	{
		var dlg = OnReceivedChunk;
		dlg?.Invoke(chunk);
	}

	private void OnCompleteLine(string line)
	{
		lock (_historyLock)
		{
			_emittedLines.Add(line);
			if (_emittedLines.Count > MaxRememberedLines)
			{
				var drop = _emittedLines.Count - MaxRememberedLines;
				_emittedLines.RemoveRange(0, drop);
				_matchCursor = Math.Max(0, _matchCursor - drop);
			}
		}

		// Raised outside the lock: a handler resolving a match can resume an inline continuation that
		// calls straight back into WaitForMatch, which takes the same lock.
		var dlg = OnReceivedLine;
		dlg?.Invoke(line);
	}

	private void OnClosed()
	{
		lock (_historyLock)
		{
			if (_streamClosed)
			{
				return;
			}

			_streamClosed = true;
		}

		var dlg = OnStreamClosed;
		dlg?.Invoke();
	}
}
