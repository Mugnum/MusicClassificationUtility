using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Configuration;

namespace Mugnum.MusicClassificationUtility.Core.Runtime;

/// <summary>
/// Checks the completion record written after successful runtime setup.
/// </summary>
public static class RuntimeReadiness
{
	/// <summary>
	/// Single runtime file used for inexpensive readiness checks.
	/// </summary>
	public const string CompletionFileName = ".setup-complete.json";

	/// <summary>
	/// Checks whether setup completed for the selected runtime and model folders.
	/// </summary>
	/// <param name="settings"> Runtime folders to check. </param>
	/// <returns> Whether the selected configuration has a successful setup record. </returns>
	public static bool IsReady(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		settings.Validate();
		try
		{
			var path = Path.Combine(settings.RuntimeDirectory, CompletionFileName);
			var record = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
			return record != null
				&& record.TryGetValue("model_directory", out var models)
				&& string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(models)),
					Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Models)), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
		{
			return false;
		}
	}
}

