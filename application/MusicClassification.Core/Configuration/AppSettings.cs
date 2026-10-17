using System.Text.Json;
using System.Text.Json.Serialization;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;

namespace Mugnum.MusicClassificationUtility.Core.Configuration;

/// <summary>
/// Stores runtime paths and persisted user preferences.
/// </summary>
public sealed class AppSettings
{
	/// <summary>
	/// Default CPU thread count.
	/// </summary>
	public static int DefaultThreads => AvailableThreads;

	/// <summary>
	/// Available logical processors within the supported thread limit.
	/// </summary>
	public static int AvailableThreads => Math.Clamp(Environment.ProcessorCount, 1, MaximumThreads);

	/// <summary>
	/// Maximum supported CPU thread count.
	/// </summary>
	public const int MaximumThreads = 64;

	/// <summary>
	/// Default idle retention in minutes.
	/// </summary>
	public const int DefaultIdleRetentionMinutes = 5;

	/// <summary>
	/// Maximum finite retention within the platform timer limit.
	/// </summary>
	public const int MaximumIdleRetentionMinutes = 43200;

	/// <summary>
	/// Runtime folder containing the environment and upstream source.
	/// </summary>
	public string RuntimeDirectory { get; set; } = GetDefaultRuntimeDirectory();

	/// <summary>
	/// Optional FFmpeg folder, with PATH as the fallback.
	/// </summary>
	public string FfmpegDirectory { get; set; } = string.Empty;

	/// <summary>
	/// Optional model folder, with runtime/models as the fallback.
	/// </summary>
	public string ModelDirectory { get; set; } = string.Empty;

	/// <summary>
	/// CPU inference thread count.
	/// </summary>
	public int Threads { get; set; } = DefaultThreads;

	/// <summary>
	/// Whether newly imported tracks are automatically queued.
	/// </summary>
	public bool AnalyzeOnImport
	{
		get; set;
	}

	/// <summary>
	/// Idle retention in minutes, with zero retaining the model until exit.
	/// </summary>
	public int ModelIdleRetentionMinutes { get; set; } = DefaultIdleRetentionMinutes;

	/// <summary>
	/// Whether exports open with their default associated editor.
	/// </summary>
	public bool OpenExportAfterSaving { get; set; } = true;

	/// <summary>
	/// Runtime interpreter executable path.
	/// </summary>
	[JsonIgnore]
	public string PythonExecutable
	{
		get
		{
			const string EnvironmentDirectoryName = ".venv";
			const string ScriptsDirectoryName = "scripts";
			const string InterpreterFileName = "python.exe";
			return Path.Combine(RuntimeDirectory, EnvironmentDirectoryName, ScriptsDirectoryName, InterpreterFileName);
		}
	}

	/// <summary>
	/// Resolved checkpoint folder.
	/// </summary>
	[JsonIgnore]
	public string Models
	{
		get
		{
			const string ModelsDirectoryName = "models";
			return string.IsNullOrWhiteSpace(ModelDirectory)
				? Path.Combine(RuntimeDirectory, ModelsDirectoryName)
				: ModelDirectory;
		}
	}

	/// <summary>
	/// Idle retention interval, with zero representing unlimited retention.
	/// </summary>
	[JsonIgnore]
	public TimeSpan IdleRetention => TimeSpan.FromMinutes(ModelIdleRetentionMinutes);

	/// <summary>
	/// Persisted user settings path.
	/// </summary>
	public static string SettingsFile
	{
		get
		{
			const string ApplicationDirectoryName = "MusicClassificationUtility";
			const string SettingsFileName = "settings.json";
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				ApplicationDirectoryName, SettingsFileName);
		}
	}

	/// <summary>
	/// Corrects numeric preferences for this machine before saving or loading.
	/// </summary>
	public void NormalizeValues()
	{
		Threads = Math.Clamp(Threads, 1, AvailableThreads);
		ModelIdleRetentionMinutes = Math.Clamp(ModelIdleRetentionMinutes, 0, MaximumIdleRetentionMinutes);
	}

	/// <summary>
	/// Validates configured paths, threads and timer limits.
	/// </summary>
	public void Validate()
	{
		ValidateDirectory(RuntimeDirectory, false);
		ValidateDirectory(FfmpegDirectory, true);
		ValidateDirectory(ModelDirectory, true);
		ArgumentOutOfRangeException.ThrowIfLessThan(Threads, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(Threads, MaximumThreads);
		ArgumentOutOfRangeException.ThrowIfNegative(ModelIdleRetentionMinutes);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(ModelIdleRetentionMinutes, MaximumIdleRetentionMinutes);
	}

	/// <summary>
	/// Resolves an executable from configuration or PATH.
	/// </summary>
	/// <param name="name"> Executable name without its extension. </param>
	/// <returns> Absolute executable path. </returns>
	public string FindTool(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		Validate();

		if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			throw new ArgumentException("The tool name must be a file name.", nameof(name));
		}

		var fileName = $"{name}.exe";

		if (!string.IsNullOrWhiteSpace(FfmpegDirectory))
		{
			var supplied = Path.Combine(FfmpegDirectory, fileName);

			return File.Exists(supplied)
				? supplied
				: throw new FileNotFoundException($"{fileName} was not found in the configured FFmpeg folder.", supplied);
		}

		foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
		{
			try
			{
				var candidate = Path.Combine(folder.Trim('"'), fileName);
				if (File.Exists(candidate))
				{
					return Path.GetFullPath(candidate);
				}
			}
			catch (ArgumentException)
			{
			}
		}

		throw new FileNotFoundException($"{fileName} was not found. Add FFmpeg to PATH or select its bin folder in Settings.");
	}

	/// <summary>
	/// Copies validated paths and preferences.
	/// </summary>
	/// <returns> Independent settings snapshot. </returns>
	public AppSettings Copy()
	{
		Validate();
		return new AppSettings
		{
			RuntimeDirectory = RuntimeDirectory,
			FfmpegDirectory = FfmpegDirectory,
			ModelDirectory = ModelDirectory,
			Threads = Threads,
			AnalyzeOnImport = AnalyzeOnImport,
			ModelIdleRetentionMinutes = ModelIdleRetentionMinutes,
			OpenExportAfterSaving = OpenExportAfterSaving
		};
	}

	/// <summary>
	/// Loads settings while preserving defaults for new preferences.
	/// </summary>
	/// <returns> Validated saved settings or defaults. </returns>
	public static AppSettings Load()
	{
		var settings = File.Exists(SettingsFile)
			? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonDefaults.Options) ?? new AppSettings()
			: new AppSettings();
		settings.NormalizeValues();
		settings.Validate();
		return settings;
	}

	/// <summary>
	/// Atomically persists validated settings.
	/// </summary>
	public void Save()
	{
		const string TemporaryExtension = ".tmp";
		Validate();
		Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
		var temporary = SettingsFile + TemporaryExtension;
		File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonDefaults.Options));
		File.Move(temporary, SettingsFile, true);
	}

	/// <summary>
	/// Builds the default per-user runtime path.
	/// </summary>
	/// <returns> Absolute runtime directory. </returns>
	private static string GetDefaultRuntimeDirectory()
	{
		const string ApplicationFolder = "MusicClassificationUtility";
		const string RuntimeFolder = "runtime";
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			ApplicationFolder, RuntimeFolder);
	}

	/// <summary>
	/// Rejects malformed or relative configured folders.
	/// </summary>
	/// <param name="path"> Configured folder. </param>
	/// <param name="optional"> Whether an empty override is allowed. </param>
	private static void ValidateDirectory(string path, bool optional)
	{
		ArgumentNullException.ThrowIfNull(path);

		if (optional && string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException("Configured folders must use absolute paths.", nameof(path));
		}

		_ = Path.GetFullPath(path);
	}
}
