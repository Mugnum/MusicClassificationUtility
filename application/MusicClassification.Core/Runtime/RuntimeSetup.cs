using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;

namespace Mugnum.MusicClassificationUtility.Core.Runtime;

/// <summary>
/// Installs and validates the per-user Python runtime without a shell script.
/// </summary>
public static class RuntimeSetup
{
	/// <summary>
	/// Initializes the selected runtime using native downloads and direct tool execution.
	/// </summary>
	/// <param name="settings"> Selected runtime folders and processor preferences. </param>
	/// <param name="applicationDirectory"> Folder containing appsettings.json and the Python adapter. </param>
	/// <param name="progress"> Receives download and child-process progress. </param>
	/// <param name="cancellation"> Cancels initialization and child processes. </param>
	/// <returns> Completion after model validation and readiness recording. </returns>
	public static async Task RunAsync(AppSettings settings, string applicationDirectory,
		IProgress<string> progress, CancellationToken cancellation)
	{
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
		ArgumentNullException.ThrowIfNull(progress);
		settings.Validate();
		cancellation.ThrowIfCancellationRequested();
		applicationDirectory = Path.GetFullPath(applicationDirectory);
		var options = RuntimeSetupOptions.Load(applicationDirectory);
		var pythonDirectory = Path.Combine(applicationDirectory, "python");
		var manifest = RuntimeManifest.Load(pythonDirectory);
		var uv = FindUv(options.UvExecutable, applicationDirectory);
		var ffmpeg = settings.FindTool("ffmpeg");
		var ffprobe = settings.FindTool("ffprobe");
		var runtime = Path.GetFullPath(settings.RuntimeDirectory);
		var models = Path.GetFullPath(settings.Models);
		var downloads = Path.Combine(runtime, "downloads");
		var vendor = Path.Combine(runtime, "vendor");
		var environment = Path.Combine(runtime, "environment");
		var cache = string.IsNullOrWhiteSpace(options.UvCacheDirectory)
			? Path.Combine(runtime, "cache", "uv")
			: Path.GetFullPath(options.UvCacheDirectory, applicationDirectory);
		Directory.CreateDirectory(runtime);
		await using var setupLock = new FileStream(Path.Combine(runtime, ".setup.lock"), FileMode.OpenOrCreate,
			FileAccess.ReadWrite, FileShare.None);
		foreach (var folder in new[] { models, downloads, vendor, environment, cache })
		{
			Directory.CreateDirectory(folder);
		}

		var completion = Path.Combine(runtime, RuntimeReadiness.CompletionFileName);
		File.Delete(completion);
		progress.Report("MAEST source and weights retain their upstream license terms.");
		using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicClassificationUtility/0.1");
		var downloader = new VerifiedDownload(client);
		var archive = Path.Combine(downloads, manifest.Source.Archive);
		await DownloadAssetAsync(downloader, manifest.Source.Url, archive, manifest.Source.Sha256,
			options, progress, cancellation).ConfigureAwait(false);
		var source = Path.Combine(vendor, manifest.Source.Directory);
		if (!File.Exists(Path.Combine(source, "models", "maest.py")))
		{
			progress.Report("Extracting MAEST source…");
			await Task.Run(() => ZipFile.ExtractToDirectory(archive, vendor, overwriteFiles: true), cancellation).ConfigureAwait(false);
		}

		foreach (var name in new[] { "pyproject.toml", "uv.lock" })
		{
			File.Copy(Path.Combine(pythonDirectory, name), Path.Combine(environment, name), overwrite: true);
		}

		var sync = CreateStartInfo(uv, applicationDirectory, runtime, models, source);
		sync.Environment["UV_PROJECT_ENVIRONMENT"] = Path.Combine(runtime, ".venv");
		sync.Environment["UV_CACHE_DIR"] = cache;
		foreach (var argument in new[] { "sync", "--project", environment, "--locked", "--no-build", "--python", options.Python })
		{
			sync.ArgumentList.Add(argument);
		}

		if (options.Offline)
		{
			sync.ArgumentList.Add("--offline");
		}

		progress.Report("Preparing the Python environment…");
		await ProcessRunner.RunAsync(sync, progress, cancellation).ConfigureAwait(false);
		await DownloadAssetAsync(downloader, manifest.Model.Url, Path.Combine(models, manifest.Model.Filename),
			manifest.Model.Sha256, options, progress, cancellation).ConfigureAwait(false);
		var validation = CreateStartInfo(settings.PythonExecutable, applicationDirectory, runtime, models, source);
		foreach (var argument in new[]
		{
			"-u", Path.Combine(pythonDirectory, "run_analysis.py"), "check", "--load-model", "--threads",
			settings.Threads.ToString(CultureInfo.InvariantCulture), "--ffmpeg", ffmpeg, "--ffprobe", ffprobe
		})
		{
			validation.ArgumentList.Add(argument);
		}

		progress.Report("Validating the runtime and loading the model…");
		await ProcessRunner.RunAsync(validation, progress, cancellation).ConfigureAwait(false);
		cancellation.ThrowIfCancellationRequested();
		var temporary = completion + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary,
				JsonSerializer.Serialize(new Dictionary<string, string> { ["model_directory"] = models }), cancellation).ConfigureAwait(false);
			cancellation.ThrowIfCancellationRequested();
			File.Move(temporary, completion, overwrite: true);
		}
		finally
		{
			File.Delete(temporary);
		}

		progress.Report("Runtime initialized successfully.");
	}

	/// <summary>
	/// Applies the configured timeout to download, streaming and verification work.
	/// </summary>
	/// <param name="downloader"> Checksum-verifying asset downloader. </param>
	/// <param name="url"> Remote asset URL. </param>
	/// <param name="destination"> Final asset path. </param>
	/// <param name="checksum"> Expected SHA-256 digest. </param>
	/// <param name="options"> Setup timeout and offline preferences. </param>
	/// <param name="progress"> Setup progress recipient. </param>
	/// <param name="cancellation"> User cancellation token. </param>
	/// <returns> Completion after the asset is verified. </returns>
	private static async Task DownloadAssetAsync(VerifiedDownload downloader, string url, string destination,
		string checksum, RuntimeSetupOptions options, IProgress<string> progress, CancellationToken cancellation)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(TimeSpan.FromMinutes(options.DownloadTimeoutMinutes));
		try
		{
			await downloader.EnsureAsync(url, destination, checksum, options.Offline, progress, timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
		{
			throw new TimeoutException($"Download or verification timed out: {Path.GetFileName(destination)}.");
		}
	}

	/// <summary>
	/// Creates an isolated redirected process configuration without changing application environment variables.
	/// </summary>
	/// <param name="executable"> Child executable path. </param>
	/// <param name="applicationDirectory"> Child working directory. </param>
	/// <param name="runtime"> Selected runtime directory. </param>
	/// <param name="models"> Selected model directory. </param>
	/// <param name="source"> Pinned upstream implementation directory. </param>
	/// <returns> Process configuration ready for explicit arguments. </returns>
	private static ProcessStartInfo CreateStartInfo(string executable, string applicationDirectory, string runtime, string models, string source)
	{
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			WorkingDirectory = applicationDirectory
		};
		start.Environment["MCU_RUNTIME_DIR"] = runtime;
		start.Environment["MCU_MODEL_DIR"] = models;
		start.Environment["MCU_SOURCE_DIR"] = source;
		start.Environment["PYTHONUTF8"] = "1";
		start.Environment["PYTHONIOENCODING"] = "utf-8";
		start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
		start.Environment["HF_HUB_OFFLINE"] = "1";
		start.Environment["NO_COLOR"] = "1";
		return start;
	}

	/// <summary>
	/// Locates uv from an explicit path, PATH or common per-user installation folders.
	/// </summary>
	/// <param name="configured"> Optional executable path from appsettings.json. </param>
	/// <param name="applicationDirectory"> Base folder for relative configured paths. </param>
	/// <returns> Existing uv executable path. </returns>
	private static string FindUv(string configured, string applicationDirectory)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			var selected = Path.GetFullPath(configured, applicationDirectory);
			return File.Exists(selected)
				? selected
				: throw new FileNotFoundException("The configured uv executable was not found.", selected);
		}

		var paths = new[]
		{
			Environment.GetEnvironmentVariable("PATH"),
			Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
			Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
		};
		var folders = paths.SelectMany(path => (path ?? string.Empty).Split(Path.PathSeparator))
			.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
			.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
		foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim('"')), "uv.exe");
			if (File.Exists(candidate))
			{
				return Path.GetFullPath(candidate);
			}
		}

		throw new FileNotFoundException("uv is required for initialization. Install it using winget install --id astral-sh.uv -e, then initialize again.");
	}
}

