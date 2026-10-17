using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Runtime;
using Mugnum.MusicClassificationUtility.Tests.TestDoubles;

namespace Mugnum.MusicClassificationUtility.Tests.Runtime;

/// <summary>
/// Verifies native setup integrity, local configuration and process cleanup.
/// </summary>
internal static class NativeSetupChecks
{
	/// <summary>
	/// Runs setup checks without downloading real models or requiring external tools.
	/// </summary>
	/// <param name="check"> Assertion result reporter. </param>
	/// <returns> Completion after deterministic native setup checks. </returns>
	public static async Task RunAsync(Action<bool, string> check)
	{
		ArgumentNullException.ThrowIfNull(check);
		var folder = Path.Combine(Path.GetTempPath(), "mcu-native-setup-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			check(RuntimeSetupOptions.Load(folder).Python == "3.12", "native setup options default without a local config file");
			var config = Path.Combine(folder, "appsettings.json");
			File.WriteAllText(config, """{"runtime_setup":{"python":"3.12.8","offline":true,"uv_cache_directory":"cache","download_timeout_minutes":2}}""");
			var options = RuntimeSetupOptions.Load(folder);
			check(options.Python == "3.12.8" && options.Offline && options.UvCacheDirectory == "cache"
				&& options.DownloadTimeoutMinutes == 2, "native setup options load from the application folder");
			File.WriteAllText(config, """{"runtime_setup":{"download_timeout_minutes":0}}""");
			try
			{
				RuntimeSetupOptions.Load(folder);
				throw new InvalidOperationException("Invalid download timeout was accepted.");
			}
			catch (ArgumentOutOfRangeException)
			{
				check(true, "invalid download timeout is rejected before setup");
			}

			var payload = Encoding.UTF8.GetBytes("verified native download fixture");
			var checksum = Convert.ToHexString(SHA256.HashData(payload));
			using var client = new HttpClient(new DownloadHandler(payload));
			var downloader = new VerifiedDownload(client);
			var progress = new InlineProgress<string>(static _ => { });
			var destination = Path.Combine(folder, "asset.bin");
			await downloader.EnsureAsync("https://fixture.invalid/asset", destination, checksum, false, progress, CancellationToken.None);
			check(File.ReadAllBytes(destination).SequenceEqual(payload) && !File.Exists(destination + ".part"),
				"native download publishes verified bytes and removes its temporary file");
			await downloader.EnsureAsync("https://fixture.invalid/asset", destination, checksum, true, progress, CancellationToken.None);
			check(true, "offline setup reuses a verified local asset");
			var rejected = Path.Combine(folder, "rejected.bin");
			try
			{
				await downloader.EnsureAsync("https://fixture.invalid/asset", rejected, new string('0', 64), false, progress, CancellationToken.None);
				throw new InvalidOperationException("Mismatched downloaded bytes were accepted.");
			}
			catch (InvalidDataException)
			{
				check(!File.Exists(rejected) && !File.Exists(rejected + ".part"), "checksum failure never publishes an asset or leaves a partial file");
			}

			try
			{
				await downloader.EnsureAsync("https://fixture.invalid/asset", destination, new string('0', 64), true, progress, CancellationToken.None);
				throw new InvalidOperationException("Mismatched existing bytes were accepted.");
			}
			catch (InvalidDataException)
			{
				check(File.ReadAllBytes(destination).SequenceEqual(payload), "checksum failure preserves an existing local file for inspection");
			}

			try
			{
				await downloader.EnsureAsync("https://fixture.invalid/asset", Path.Combine(folder, "missing.bin"), checksum, true, progress, CancellationToken.None);
				throw new InvalidOperationException("Offline download was allowed.");
			}
			catch (FileNotFoundException)
			{
				check(true, "offline setup refuses missing assets without making a network request");
			}

			using var pendingClient = new HttpClient(new DownloadHandler(payload, waitForCancellation: true));
			using var downloadCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
			try
			{
				await new VerifiedDownload(pendingClient).EnsureAsync("https://fixture.invalid/asset", rejected, checksum,
					false, progress, downloadCancellation.Token);
				throw new InvalidOperationException("Download cancellation was ignored.");
			}
			catch (OperationCanceledException)
			{
				check(!File.Exists(rejected) && !File.Exists(rejected + ".part"), "cancelled downloads leave no published or partial asset");
			}

			await VerifyProcessAsync(check);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Verifies cancellation and failure reporting against an actual disposable child process.
	/// </summary>
	/// <param name="check"> Assertion result reporter. </param>
	/// <returns> Completion after child-process cleanup checks. </returns>
	private static async Task VerifyProcessAsync(Action<bool, string> check)
	{
		var start = new ProcessStartInfo(Environment.ProcessPath!)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			start.ArgumentList.Add(typeof(NativeSetupChecks).Assembly.Location);
		}

		start.ArgumentList.Add("--process-fixture");
		var messages = new ConcurrentQueue<string>();
		var progress = new InlineProgress<string>(messages.Enqueue);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		try
		{
			await ProcessRunner.RunAsync(start, progress, cancellation.Token);
			throw new InvalidOperationException("Process cancellation was ignored.");
		}
		catch (OperationCanceledException)
		{
			check(messages.Contains("process fixture ready"), "native process cancellation drains output and stops the child before disposal");
		}

		start.ArgumentList.Add("--fail");
		try
		{
			await ProcessRunner.RunAsync(start, progress, CancellationToken.None);
			throw new InvalidOperationException("Child process failure was ignored.");
		}
		catch (InvalidOperationException exception) when (exception.Message.Contains("exit code 7", StringComparison.Ordinal))
		{
			check(exception.Message.Contains("process fixture failure", StringComparison.Ordinal), "native setup process failures preserve useful stderr diagnostics");
		}
	}
}

