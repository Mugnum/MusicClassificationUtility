using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;

namespace Mugnum.MusicClassificationUtility.Core.Configuration;

/// <summary>
/// Reads release-owned setup options from beside the application executable.
/// </summary>
public sealed class RuntimeSetupOptions
{
	/// <summary>
	/// Default maximum duration of a runtime asset download.
	/// </summary>
	public const int DefaultDownloadTimeoutMinutes = 60;

	/// <summary>
	/// Python version or executable requested from uv.
	/// </summary>
	public string Python { get; set; } = "3.12";

	/// <summary>
	/// Optional uv executable path, resolved relative to the application folder.
	/// </summary>
	public string UvExecutable { get; set; } = string.Empty;

	/// <summary>
	/// Optional wheel cache path, otherwise stored inside the selected runtime.
	/// </summary>
	public string UvCacheDirectory { get; set; } = string.Empty;

	/// <summary>
	/// Whether downloads are disabled and only cached prerequisites may be used.
	/// </summary>
	public bool Offline { get; set; }

	/// <summary>
	/// Maximum elapsed time allowed for each download.
	/// </summary>
	public int DownloadTimeoutMinutes { get; set; } = DefaultDownloadTimeoutMinutes;

	/// <summary>
	/// Loads and validates the optional application-local setup configuration.
	/// </summary>
	/// <param name="applicationDirectory"> Application installation folder. </param>
	/// <returns> Validated setup options with defaults for omitted fields. </returns>
	public static RuntimeSetupOptions Load(string applicationDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
		var path = Path.Combine(applicationDirectory, "appsettings.json");
		var options = new RuntimeSetupOptions();
		if (File.Exists(path))
		{
			using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true
			});
			if (document.RootElement.TryGetProperty("runtime_setup", out var section))
			{
				options = section.Deserialize<RuntimeSetupOptions>(JsonDefaults.Options)
					?? throw new InvalidDataException("runtime_setup must contain setup options.");
			}
		}

		options.Validate();
		return options;
	}

	/// <summary>
	/// Rejects invalid setup option values before downloading or changing the runtime.
	/// </summary>
	public void Validate()
	{
		const int MinimumTimeoutMinutes = 1;
		const int MaximumTimeoutMinutes = 1440;
		ArgumentException.ThrowIfNullOrWhiteSpace(Python);
		ArgumentNullException.ThrowIfNull(UvExecutable);
		ArgumentNullException.ThrowIfNull(UvCacheDirectory);
		ArgumentOutOfRangeException.ThrowIfLessThan(DownloadTimeoutMinutes, MinimumTimeoutMinutes);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(DownloadTimeoutMinutes, MaximumTimeoutMinutes);
	}
}

