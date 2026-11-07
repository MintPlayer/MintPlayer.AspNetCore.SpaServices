using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MintPlayer.AspNetCore.NodeServices.HostingModels;

/// <summary>
/// The JSON serializer of the .NET to Node RPC channel (System.Text.Json; Newtonsoft.Json until 2026-11).
/// </summary>
/// <remarks>
/// <para>
/// Matches what Newtonsoft's <c>CamelCasePropertyNamesContractResolver</c> produced: camelCase property names
/// <em>and</em> dictionary keys on the way out - so <c>SupplyData</c> keys keep reaching JavaScript camel-cased -
/// and case-insensitive names on the way in.
/// </para>
/// <para>
/// Invocation arguments and results are caller-chosen types, so this uses the reflection-based resolver. Apps
/// that publish trimmed or native-AOT must keep the types they pass to and receive from Node, as with any
/// reflection-based System.Text.Json use.
/// </para>
/// </remarks>
internal static class NodeRpcJson
{
	internal static readonly JsonSerializerOptions Options = CreateOptions();

	// A method rather than a field initializer, so the suppression has somewhere to go: otherwise a
	// trimmed or native-AOT publish of a consuming app reports the resolver's constructor.
	[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Arguments and results are caller-chosen types; documented on the class.")]
	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Arguments and results are caller-chosen types; documented on the class.")]
	private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
	{
		DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
	};

	/// <summary>
	/// Serializes the invocation to UTF-8 JSON.
	/// </summary>
	/// <remarks>
	/// Asynchronous on purpose: only the async serializer enumerates an <see cref="IAsyncEnumerable{T}"/>, and an
	/// Entity Framework query (<c>IQueryable</c>) is one. Handed to <c>SupplyData</c> as it is, the synchronous
	/// serializer throws NotSupportedException ("can only be serialized using async serialization methods"),
	/// where Newtonsoft used to enumerate it.
	/// </remarks>
	[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Arguments are caller-supplied types; documented on the class.")]
	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Arguments are caller-supplied types; documented on the class.")]
	internal static async Task<byte[]> SerializeAsync(NodeInvocationInfo invocationInfo, CancellationToken cancellationToken)
	{
		using var buffer = new MemoryStream();
		await JsonSerializer.SerializeAsync(buffer, invocationInfo, Options, cancellationToken);
		return buffer.ToArray();
	}

	[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Results are caller-chosen types; documented on the class.")]
	[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Results are caller-chosen types; documented on the class.")]
	internal static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
