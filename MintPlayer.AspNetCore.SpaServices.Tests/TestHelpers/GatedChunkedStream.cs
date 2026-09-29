using System.Text;

namespace MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;

/// <summary>
/// Like <see cref="GatedStream"/>, but hands its content out one scripted read at a time, the way a
/// pipe delivers whatever the child has flushed so far. A line can therefore be split across two
/// reads, which a single in-memory buffer never does.
/// </summary>
internal sealed class GatedChunkedStream(params string[] reads) : Stream
{
	private readonly Queue<byte[]> pending = new(reads.Select(Encoding.UTF8.GetBytes));
	private readonly SemaphoreSlim gate = new(0, 1);
	private bool opened;
	private byte[] current = [];
	private int consumed;

	public void Release() => gate.Release();

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (!opened)
		{
			await gate.WaitAsync(cancellationToken);
			opened = true;
		}

		return ReadNext(buffer.Span);
	}

	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (!opened)
		{
			gate.Wait();
			opened = true;
		}

		return ReadNext(buffer.AsSpan(offset, count));
	}

	/// <summary>Returns the rest of the current scripted read, never running into the next one.</summary>
	private int ReadNext(Span<byte> buffer)
	{
		while (consumed == current.Length)
		{
			if (!pending.TryDequeue(out var next))
			{
				return 0;
			}

			current = next;
			consumed = 0;
		}

		var count = Math.Min(buffer.Length, current.Length - consumed);
		current.AsSpan(consumed, count).CopyTo(buffer);
		consumed += count;
		return count;
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();

	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	public override void Flush() { }
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			gate.Dispose();
		}
		base.Dispose(disposing);
	}
}
