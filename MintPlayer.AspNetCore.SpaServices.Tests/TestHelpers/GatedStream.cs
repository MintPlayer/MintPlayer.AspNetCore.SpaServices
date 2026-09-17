using System.Text;

namespace MintPlayer.AspNetCore.SpaServices.Tests.TestHelpers;

/// <summary>
/// Holds the first read until <see cref="Release"/> is called, so handlers can be attached before
/// any data flows. <c>EventedStreamReader</c> starts consuming inside its own constructor, so
/// without this the tests would race it.
/// </summary>
/// <remarks>
/// This used to exist twice - once in the Utils suite and once in the Prerendering suite, because
/// the two packages carried duplicate copies of the reader itself. The duplication is gone, so this
/// helper is shared.
/// </remarks>
internal sealed class GatedStream(string content) : Stream
{
	private readonly MemoryStream inner = new(Encoding.UTF8.GetBytes(content));
	private readonly SemaphoreSlim gate = new(0, 1);
	private bool opened;

	public void Release() => gate.Release();

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (!opened)
		{
			await gate.WaitAsync(cancellationToken);
			opened = true;
		}
		return await inner.ReadAsync(buffer, cancellationToken);
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
		return inner.Read(buffer, offset, count);
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => inner.Length;

	public override long Position
	{
		get => inner.Position;
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
			inner.Dispose();
			gate.Dispose();
		}
		base.Dispose(disposing);
	}
}
