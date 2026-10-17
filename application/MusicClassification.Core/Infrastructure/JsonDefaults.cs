using System.Text.Json;

namespace Mugnum.MusicClassificationUtility.Core.Infrastructure;

/// <summary>
/// Provides the shared Python-compatible serialization policy.
/// </summary>
public static class JsonDefaults
{
	/// <summary>
	/// Compact serialization required by the worker's single-line request contract.
	/// </summary>
	public static readonly JsonSerializerOptions ProtocolOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
	};

	/// <summary>
	/// Serializer options for settings, protocol results and validation artifacts.
	/// </summary>
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};
}
