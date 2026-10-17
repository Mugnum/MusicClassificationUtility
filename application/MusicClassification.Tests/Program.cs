using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Analysis;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Export;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Media;
using Mugnum.MusicClassificationUtility.Core.Models;
using Mugnum.MusicClassificationUtility.Core.Runtime;
using Mugnum.MusicClassificationUtility.Tests.TestDoubles;
using Mugnum.MusicClassificationUtility.Tests.Runtime;

namespace Mugnum.MusicClassificationUtility.Tests;

/// <summary>
/// Runs deterministic core checks and optional local model integration checks.
/// </summary>
internal static class Program
{
	/// <summary>
	/// Number of successful validation assertions.
	/// </summary>
	private static int PassedCount;

	/// <summary>
	/// Runs core assertions and explicitly requested model checks.
	/// </summary>
	/// <param name="args"> Command-line arguments. </param>
	/// <returns> Operation completion. </returns>
	public static async Task Main(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
		if (args.Contains("--process-fixture"))
		{
			Console.WriteLine("process fixture ready");
			if (args.Contains("--fail"))
			{
				Console.Error.WriteLine("process fixture failure");
				Environment.ExitCode = 7;
				return;
			}

			await Task.Delay(Timeout.Infinite);
			return;
		}

		await NativeSetupChecks.RunAsync(Check);
		Check(MetadataReader.ResolveDisplayName("C:/music/file.flac", null, " Artist ", " Title ") == "Artist - Title", "artist/title metadata");
		Check(MetadataReader.ResolveDisplayName("C:/music/file.flac", null, "", "Title") == "Title", "title-only metadata");
		Check(MetadataReader.ResolveDisplayName("C:/music/file.flac", null, "Artist", " ") == "file.flac", "filename fallback");

		if (Option(args, "--metadata-fixtures") is { } metadataFixtures)
		{
			var metadataSettings = new AppSettings { FfmpegDirectory = Option(args, "--ffmpeg") ?? string.Empty };
			var fixtures = new[]
			{
				("track-artist.opus", "Track artist - Test title"),
				("album-artist.ogg", "Album artist - Test title"),
				("title-only.ogg", "Test title"),
				("artist-only.ogg", "artist-only.ogg"),
				("track-artist.flac", "Track artist - Test title"),
				("container-priority.mka", "Container artist - Container title"),
				("stream-album-artist.mka", "Album artist - Container title")
			};
			foreach (var (fileName, expected) in fixtures)
			{
				var metadata = await MetadataReader.ReadAsync(Path.Combine(metadataFixtures, fileName), metadataSettings, CancellationToken.None);
				Check(metadata.Name == expected, $"FFprobe metadata fallback: {fileName} (received {metadata.Name})");
			}
		}

		if (Option(args, "--metadata-file") is { } metadataFile)
		{
			var expected = Option(args, "--metadata-name") ?? throw new ArgumentException("--metadata-name is required with --metadata-file.");
			var metadataSettings = new AppSettings { FfmpegDirectory = Option(args, "--ffmpeg") ?? string.Empty };
			var metadata = await MetadataReader.ReadAsync(metadataFile, metadataSettings, CancellationToken.None);
			Check(metadata.Name == expected, $"Real-file metadata: {metadata.Name}");
		}

		var requestJson = JsonSerializer.Serialize(new AnalysisRequest("request-id", "song.flac"), JsonDefaults.ProtocolOptions);
		using var requestDocument = JsonDocument.Parse(requestJson);
		Check(!requestJson.Contains('\n')
				&& requestDocument.RootElement.GetProperty("type").GetString() == "analyze"
				&& requestDocument.RootElement.GetProperty("id").GetString() == "request-id",
			"request DTO retains the compact snake_case protocol");

		var decoded = JsonSerializer.Deserialize<AnalysisResult>("""{"duration_seconds":32.25,"overall":[{"label":"Rock---Industrial","score":0.7}],"segments":[{"start_seconds":30,"end_seconds":32.25,"valid_duration_seconds":2.25,"repeated_padding_seconds":27.75}]}""", JsonDefaults.Options)!;
		Check(decoded.DurationSeconds == 32.25
			&& decoded.Segments[0].ValidDurationSeconds == 2.25
			&& decoded.Overall[0].Score == .7, "Python snake_case result contract");
		Check(decoded.Segments[0].Range == "00:30–00:32", "window range formatting");
		await using (var fake = new FakeBackend())
		{
			var queue = new AnalysisQueue(fake);
			var a = new Track("one.wav");
			var b = new Track("two.wav");
			queue.Enqueue([a, a, b]);
			await queue.WhenIdle;
			Check(fake is { Calls: 2, Peak: 1 } && a.Status == TrackStatus.Done && b.Status == TrackStatus.Done, "sequential queue and duplicate scheduling");
		}

		await using (var fake = new FakeBackend
		{
			Fail = true
		})
		{
			var queue = new AnalysisQueue(fake);
			var a = new Track("one.wav");
			var b = new Track("two.wav");
			string failure = null;

			queue.Failed += value => failure = value;
			queue.Enqueue([a, b]);
			await queue.WhenIdle;
			Check(a.Status == TrackStatus.Error && b.Status == TrackStatus.NotAnalyzed && fake.Calls == 1 && failure != null, "an error stops the queue and releases pending tracks");
			fake.Fail = false;
			queue.Enqueue([a, b]);
			await queue.WhenIdle;
			Check(a.Status == TrackStatus.Done && b.Status == TrackStatus.Done, "queue can restart after an error");
		}

		await using (var fake = new FakeBackend
		{
			Delay = 10000
		}

		)
		{
			var queue = new AnalysisQueue(fake);
			var a = new Track("one.wav");
			var b = new Track("two.wav");
			queue.Enqueue([a, b]);
			await EventuallyAsync(() => fake.Calls == 1);
			await queue.CancelAsync();
			Check(a.Status == TrackStatus.Cancelled && b.Status == TrackStatus.NotAnalyzed && !queue.IsRunning, "cancellation releases active and pending tracks");
			fake.Delay = 1;
			queue.Enqueue([a, b]);
			await queue.WhenIdle;
			Check(a.Status == TrackStatus.Done && b.Status == TrackStatus.Done, "queue can restart after cancellation");
		}

		var trackForExport = new Track("test.wav")
		{
			Result = decoded,
			Status = TrackStatus.Done
		};
		var text = ResultExporter.Create([trackForExport]);
		Check(text.Contains("Weight: 2")
				&& text.Contains("27")
				&& text.Contains("Industrial")
				&& text.Contains(trackForExport.FullPath),
			"export includes weighted windows, padding, scores and full path");

		Check(MetadataReader.ResolveDisplayName("song.flac", "Album artist", "Track artist", "Title") == "Album artist - Title",
			"album artist takes precedence over track artist");

		Check(MetadataReader.ResolveDisplayName("song.flac", " ", "Track artist", "Title") == "Track artist - Title",
			"blank album artist falls back to track artist");

		Check(!trackForExport.CanAnalyze, "successful tracks cannot be analyzed again");
		Check(!text[..text.IndexOf("DETAILED RESULTS", StringComparison.Ordinal)].Contains("| Done", StringComparison.Ordinal),
			"export summary omits completion status");
		var normalized = new AppSettings { Threads = int.MaxValue, ModelIdleRetentionMinutes = int.MaxValue };
		normalized.NormalizeValues();
		Check(normalized.Threads == AppSettings.AvailableThreads
			&& normalized.ModelIdleRetentionMinutes == AppSettings.MaximumIdleRetentionMinutes,
			"numeric preferences clamp to processor and timer limits");
		normalized.Threads = -1;
		normalized.ModelIdleRetentionMinutes = -1;
		normalized.NormalizeValues();
		Check(normalized.Threads == 1 && normalized.ModelIdleRetentionMinutes == 0, "negative numeric preferences clamp to supported minimums");
		Check(new AppSettings().Threads == Math.Clamp(Environment.ProcessorCount, 1, AppSettings.MaximumThreads),
			"default CPU threads follow available logical processors");
		var readinessFolder = Path.Combine(Path.GetTempPath(), "mcu-readiness-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(readinessFolder);
		try
		{
			var readinessSettings = new AppSettings { RuntimeDirectory = readinessFolder };
			Check(!RuntimeReadiness.IsReady(readinessSettings), "missing completion file means not ready");
			var completion = Path.Combine(readinessFolder, RuntimeReadiness.CompletionFileName);
			File.WriteAllText(completion, JsonSerializer.Serialize(new Dictionary<string, string> { ["model_directory"] = readinessSettings.Models }));
			Check(RuntimeReadiness.IsReady(readinessSettings), "completion record is sufficient for the quick readiness check");
			readinessSettings.ModelDirectory = Path.Combine(readinessFolder, "other-models");
			Check(!RuntimeReadiness.IsReady(readinessSettings), "changing model folders invalidates readiness");
			File.WriteAllText(completion, "invalid JSON");
			Check(!RuntimeReadiness.IsReady(readinessSettings), "damaged completion record means not ready");
		}
		finally
		{
			Directory.Delete(readinessFolder, recursive: true);
		}

		var defaults = new AppSettings();
		Check(!defaults.AnalyzeOnImport && defaults.OpenExportAfterSaving && defaults.ModelIdleRetentionMinutes == 5, "new preference defaults");
		var preferences = defaults.Copy();
		preferences.AnalyzeOnImport = true;
		preferences.ModelIdleRetentionMinutes = 60;
		preferences.OpenExportAfterSaving = false;
		var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(preferences, JsonDefaults.Options), JsonDefaults.Options)!;
		Check(roundTrip.AnalyzeOnImport && roundTrip.ModelIdleRetentionMinutes == 60 && !roundTrip.OpenExportAfterSaving, "new preferences survive JSON persistence");
		Check(new AppSettings().Copy().OpenExportAfterSaving && !new AppSettings().AnalyzeOnImport, "reset snapshot restores default preferences");

		var invalidPreferences = new AppSettings
		{
			ModelIdleRetentionMinutes = -1
		};
		try
		{
			invalidPreferences.Validate();
			throw new Exception("Invalid retention accepted.");
		}
		catch (ArgumentOutOfRangeException)
		{
			Check(true, "invalid retention is rejected");
		}

		await using (var fake = new FakeBackend())
		{
			await using (var retained = new RetainedAnalysisBackend(fake, TimeSpan.FromMilliseconds(100)))
			{
				await retained.AnalyzeAsync("song.wav", new Progress<BackendProgress>(), CancellationToken.None);
				await EventuallyAsync(() => fake.Stops == 1);
				Check(fake.Stops == 1, "idle retention releases the worker");
				await retained.AnalyzeAsync("song.wav", new Progress<BackendProgress>(), CancellationToken.None);
				Check(fake.ModelStarts == 2, "analysis reloads the worker after idle retirement");
			}
		}

		await using (var fake = new FakeBackend())
		{
			await using (var retained = new RetainedAnalysisBackend(fake, TimeSpan.Zero))
			{
				await retained.AnalyzeAsync("song.wav", new Progress<BackendProgress>(), CancellationToken.None);
				await Task.Delay(200);
				Check(fake.Stops == 0, "zero retention keeps the model loaded until exit");
			}
		}

		await using (var fake = new FakeBackend
		{
			Delay = 30
		})
		{
			await using (var retained = new RetainedAnalysisBackend(fake, TimeSpan.FromMilliseconds(200)))
			{
				await retained.AnalyzeAsync("one.wav", new Progress<BackendProgress>(), CancellationToken.None);
				fake.Delay = 350;
				await retained.AnalyzeAsync("two.wav", new Progress<BackendProgress>(), CancellationToken.None);
				Check(fake.Stops == 0, "a new request cancels idle retirement throughout active analysis");
				await EventuallyAsync(() => fake.Stops == 1);
				Check(fake.ActiveStops == 0, "retirement never stops an active inference");
			}
		}

		Check(text.IndexOf(trackForExport.GenreSummary, StringComparison.Ordinal)
				< text.IndexOf("DETAILED RESULTS", StringComparison.Ordinal),
			"export starts with the same per-song summary as the grid");

		if (Option(args, "--setup-runtime") is { } setupRuntime)
		{
			var setupSettings = new AppSettings
			{
				RuntimeDirectory = setupRuntime,
				FfmpegDirectory = Option(args, "--ffmpeg") ?? string.Empty,
				Threads = Math.Min(4, AppSettings.AvailableThreads)
			};
			var application = Option(args, "--setup-application") ?? throw new ArgumentException("--setup-application is required.");
			await RuntimeSetup.RunAsync(setupSettings, application, new InlineProgress<string>(Console.WriteLine), CancellationToken.None);
			Check(RuntimeReadiness.IsReady(setupSettings), "GUI setup runner completes and produces a valid readiness record");
		}

		if (Option(args, "--runtime") is { } runtime)
		{
			var settings = new AppSettings
			{
				RuntimeDirectory = runtime,
				FfmpegDirectory = Option(args, "--ffmpeg") ?? ""
			};
			var python = Option(args, "--python-source") ?? throw new ArgumentException("--python-source is required.");
			var song = Option(args, "--song") ?? throw new ArgumentException("--song is required.");
			await using var backend = new BackendClient(settings, python);
			using (var cancellation = new CancellationTokenSource(100))
			{
				try
				{
					await backend.AnalyzeAsync(song, new Progress<BackendProgress>(), cancellation.Token);
					throw new Exception("Cancellation was ignored.");
				}
				catch (OperationCanceledException)
				{
					Check(true, "real worker cancellation during startup");
				}
			}

			var updates = new System.Collections.Concurrent.ConcurrentQueue<BackendProgress>();
			var result = await backend.AnalyzeAsync(song, new InlineProgress<BackendProgress>(updates.Enqueue), CancellationToken.None);
			Check(result.Overall.Count == 10 && result.OverallScores.Length == 519 && result.Segments.Count > 0, "real worker restarts and returns complete model output");
			Check(updates.Any(p => p.Percent is > 0 and <= 99), "real worker emits window progress");
			var again = await backend.AnalyzeAsync(song, new InlineProgress<BackendProgress>(updates.Enqueue), CancellationToken.None);
			Check(result.OverallScores.SequenceEqual(again.OverallScores), "persistent worker returns repeatable inference");
			using (var cancelWindow = new CancellationTokenSource())
			{
				try
				{
					await backend.AnalyzeAsync(song, new InlineProgress<BackendProgress>(p =>
					{
						if (p.Percent > 0)
						{
							cancelWindow.Cancel();
						}
					}), cancelWindow.Token);
					throw new Exception("Window cancellation was ignored.");
				}
				catch (OperationCanceledException)
				{
					Check(true, "real worker cancellation after a completed window");
				}
			}

			try
			{
				await backend.AnalyzeAsync(song + ".missing", new Progress<BackendProgress>(), CancellationToken.None);
				throw new Exception("Missing file accepted.");
			}
			catch (InvalidOperationException exc)
			{
				Check(exc.Message.Contains("missing"), "real worker reports missing-file errors");
			}

			if (Option(args, "--metadata-song") is { } tagged)
			{
				var metadata = await MetadataReader.ReadAsync(tagged, settings, CancellationToken.None);
				Check(metadata.Name == "Album artist - Test title", "FFprobe album artist import from a Unicode/bracket path");
			}

			if (Option(args, "--bad-runtime") is { } badRuntime)
			{
				await using var broken = new BackendClient(new AppSettings { RuntimeDirectory = badRuntime, FfmpegDirectory = settings.FfmpegDirectory }, python);
				try
				{
					await broken.AnalyzeAsync(song, new Progress<BackendProgress>(), CancellationToken.None);
					throw new Exception("Broken runtime was accepted.");
				}
				catch (InvalidOperationException exc)
				{
					Check(exc.Message.Contains("No module named"), "missing Python dependency reaches the caller as a diagnostic");
				}
			}
		}

		Console.WriteLine($"{PassedCount} checks passed.");
	}

	/// <summary>
	/// Reports a passed assertion or fails the validation run.
	/// </summary>
	/// <param name="condition"> Validation condition. </param>
	/// <param name="message"> Validation description. </param>
	private static void Check(bool condition, string message)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}

		PassedCount++;
		Console.WriteLine("PASS " + message);
	}

	/// <summary>
	/// Waits for an asynchronous lifecycle condition within a bounded interval.
	/// </summary>
	/// <param name="condition"> Validation condition. </param>
	/// <returns> Operation completion. </returns>
	private static async Task EventuallyAsync(Func<bool> condition)
	{
		ArgumentNullException.ThrowIfNull(condition);
		const int TimeoutMilliseconds = 5000;
		const int PollMilliseconds = 10;
		using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
		while (!condition())
		{
			await Task.Delay(PollMilliseconds, timeout.Token);
		}
	}

	/// <summary>
	/// Resolves a command-line option without persisting overrides.
	/// </summary>
	/// <param name="args"> Command-line arguments. </param>
	/// <param name="name"> Requested command-line option name. </param>
	/// <returns> Option value when present. </returns>
	private static string Option(string[] args, string name)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		var index = Array.IndexOf(args, name);
		return index >= 0 && index + 1 < args.Length
			? args[index + 1]
			: null;
	}
}
