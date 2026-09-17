using System.Net.WebSockets;
using System.Text;
using MintPlayer.AspNetCore.SpaServices.Proxying;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Proxying;

/// <summary>
/// The websocket half of the proxy. <see cref="WebSocket"/> is abstract, so the frame pump can be
/// driven with a pair of fakes - no real socket, no dev server, no network.
/// </summary>
public class WebSocketProxyTests
{
	[Theory]
	[InlineData("http://localhost:4200/", "ws://localhost:4200/")]
	[InlineData("https://localhost:4200/", "wss://localhost:4200/")]
	[InlineData("http://localhost:4200/sockjs-node", "ws://localhost:4200/sockjs-node")]
	[InlineData("https://example.com:8443/a/b?c=d", "wss://example.com:8443/a/b?c=d")]
	public void Maps_http_schemes_onto_websocket_schemes(string input, string expected)
	{
		Assert.Equal(new Uri(expected), SpaProxy.ToWebSocketScheme(new Uri(input)));
	}

	[Fact]
	public void Leaves_a_scheme_it_does_not_recognise_alone()
	{
		// Only http/https are rewritten. Anything else passes through rather than being mangled.
		var uri = new Uri("ftp://localhost:4200/");

		Assert.Equal("ftp", SpaProxy.ToWebSocketScheme(uri).Scheme);
	}

	[Fact]
	public void Rejects_a_null_uri()
	{
		Assert.Throws<ArgumentNullException>(() => SpaProxy.ToWebSocketScheme(null!));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public async Task Rejects_a_non_positive_buffer_size(int bufferSize)
	{
		using var source = new FakeWebSocket();
		using var destination = new FakeWebSocket();

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => SpaProxy.PumpWebSocket(source, destination, bufferSize, CancellationToken.None));
	}

	[Fact]
	public async Task Forwards_a_text_frame_to_the_destination()
	{
		using var source = new FakeWebSocket();
		using var destination = new FakeWebSocket();
		source.Enqueue("hello");
		source.EnqueueClose();

		await SpaProxy.PumpWebSocket(source, destination, 1024, CancellationToken.None);

		Assert.Equal(["hello"], destination.Sent);
	}

	[Fact]
	public async Task Forwards_several_frames_in_order()
	{
		using var source = new FakeWebSocket();
		using var destination = new FakeWebSocket();
		source.Enqueue("one");
		source.Enqueue("two");
		source.Enqueue("three");
		source.EnqueueClose();

		await SpaProxy.PumpWebSocket(source, destination, 1024, CancellationToken.None);

		Assert.Equal(["one", "two", "three"], destination.Sent);
	}

	[Fact]
	public async Task Closes_the_destination_when_the_source_closes()
	{
		using var source = new FakeWebSocket();
		using var destination = new FakeWebSocket();
		source.EnqueueClose(WebSocketCloseStatus.NormalClosure, "bye");

		await SpaProxy.PumpWebSocket(source, destination, 1024, CancellationToken.None);

		Assert.True(destination.CloseOutputCalled);
		Assert.Equal(WebSocketCloseStatus.NormalClosure, destination.CloseStatusSent);
		Assert.Equal("bye", destination.CloseDescriptionSent);
	}

	[Fact]
	public async Task Does_not_close_a_destination_that_is_already_closed()
	{
		using var source = new FakeWebSocket();
		using var destination = new FakeWebSocket { StateOverride = WebSocketState.Closed };
		source.EnqueueClose();

		await SpaProxy.PumpWebSocket(source, destination, 1024, CancellationToken.None);

		// CloseOutputAsync on an already-closed socket throws; the state check is what prevents it.
		Assert.False(destination.CloseOutputCalled);
	}

	[Fact]
	public async Task Returns_when_the_token_is_already_cancelled()
	{
		using var source = new FakeWebSocket();
		using var destination = new FakeWebSocket();
		source.Enqueue("never delivered");
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// ReceiveAsync does not honour the token reliably, so the pump polls for cancellation itself.
		// The contract is that it returns rather than throwing.
		await SpaProxy.PumpWebSocket(source, destination, 1024, cts.Token);

		Assert.Empty(destination.Sent);
	}

	/// <summary>
	/// A <see cref="WebSocket"/> that replays a scripted sequence of frames and records what was sent
	/// to it. Receives complete synchronously, so the pump's polling loop never actually waits.
	/// </summary>
	internal sealed class FakeWebSocket : WebSocket
	{
		private readonly Queue<Frame> inbound = new();

		public List<string> Sent { get; } = [];

		public bool CloseOutputCalled { get; private set; }

		public WebSocketCloseStatus? CloseStatusSent { get; private set; }

		public string? CloseDescriptionSent { get; private set; }

		public void Enqueue(string text, WebSocketMessageType type = WebSocketMessageType.Text)
			=> inbound.Enqueue(new Frame(Encoding.UTF8.GetBytes(text), type, null, null));

		public void EnqueueClose(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? description = null)
			=> inbound.Enqueue(new Frame([], WebSocketMessageType.Close, status, description));

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			if (inbound.Count == 0)
			{
				// Nothing scripted left: behave like a closed peer rather than hanging the test.
				return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null));
			}

			var frame = inbound.Dequeue();
			if (frame.Type == WebSocketMessageType.Close)
			{
				closeStatus = frame.CloseStatus;
				closeDescription = frame.CloseDescription;
				return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, frame.CloseStatus, frame.CloseDescription));
			}

			frame.Payload.CopyTo(buffer.Array!, buffer.Offset);
			return Task.FromResult(new WebSocketReceiveResult(frame.Payload.Length, frame.Type, true));
		}

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
			return Task.CompletedTask;
		}

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
		{
			CloseOutputCalled = true;
			CloseStatusSent = closeStatus;
			CloseDescriptionSent = statusDescription;
			return Task.CompletedTask;
		}

		// Backed by fields: an override cannot introduce a setter the base type does not declare.
		private WebSocketCloseStatus? closeStatus;
		private string? closeDescription;

		public override WebSocketCloseStatus? CloseStatus => closeStatus;

		public override string? CloseStatusDescription => closeDescription;

		public override WebSocketState State => StateOverride;

		public WebSocketState StateOverride { get; init; } = WebSocketState.Open;

		public override string? SubProtocol => null;

		public override void Abort() { }

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public override void Dispose() { }

		private readonly record struct Frame(byte[] Payload, WebSocketMessageType Type, WebSocketCloseStatus? CloseStatus, string? CloseDescription);
	}
}
