using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Media;

/// <summary>
/// Resolves metadata through the configured local FFprobe executable.
/// </summary>
public static class MetadataReader
{
	/// <summary>
	/// Applies album artist, artist, title and file-name precedence.
	/// </summary>
	/// <param name="path"> Local input path. </param>
	/// <param name="albumArtist"> Album artist tag. </param>
	/// <param name="artist"> Track artist tag. </param>
	/// <param name="title"> Track title tag. </param>
	/// <returns> Resolved track name. </returns>
	public static string ResolveDisplayName(string path, string albumArtist, string artist, string title)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		title = title?.Trim();
		albumArtist = albumArtist?.Trim();
		artist = artist?.Trim();
		if (string.IsNullOrWhiteSpace(title))
		{
			return Path.GetFileName(path);
		}

		var performer = !string.IsNullOrWhiteSpace(albumArtist)
			? albumArtist
			: artist;

		return string.IsNullOrWhiteSpace(performer)
			? title
			: $"{performer} - {title}";
	}

	/// <summary>
	/// Reads tags and duration asynchronously.
	/// </summary>
	/// <param name="path"> Local source path. </param>
	/// <param name="settings"> Tool configuration. </param>
	/// <param name="cancellation"> Import cancellation token. </param>
	/// <returns> Resolved display metadata. </returns>
	public static async Task<TrackMetadata> ReadAsync(string path, AppSettings settings, CancellationToken cancellation)
	{
		const string ProbeTool = "ffprobe";
		const int TimeoutSeconds = 15;
		const string DurationFormat = @"m\:ss";
		const string VerbosityOption = "-v";
		const string ErrorMessageType = "error";
		const string ProtocolWhitelistOption = "-protocol_whitelist";
		const string AllowedLocalProtocols = "file,pipe,data";
		const string EntriesOption = "-show_entries";
		const string MetadataEntries = "format=duration:format_tags:stream=codec_type:stream_tags";
		const string OutputFormatOption = "-of";
		const string JsonOutputFormat = "json";
		const string AlbumArtistTag = "albumartist";
		const string ArtistTag = "artist";
		const string TitleTag = "title";

		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(settings);
		cancellation.ThrowIfCancellationRequested();
		settings.Validate();

		var start = new ProcessStartInfo(settings.FindTool(ProbeTool))
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		var arguments = new[]
		{
			VerbosityOption,
			ErrorMessageType,
			ProtocolWhitelistOption,
			AllowedLocalProtocols,
			EntriesOption,
			MetadataEntries,
			OutputFormatOption,
			JsonOutputFormat,
			path
		};
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFprobe.");
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
		await using var registration = timeout.Token.Register(static state => StopProcess((Process)state!), process);
		var output = process.StandardOutput.ReadToEndAsync();
		var errors = process.StandardError.ReadToEndAsync();
		try
		{
			await process.WaitForExitAsync(timeout.Token);
			var json = await output;
			var error = await errors;

			if (process.ExitCode != 0)
			{
				throw new InvalidDataException(error.Trim());
			}

			using var document = JsonDocument.Parse(json);
			var tags = ReadTags(document.RootElement);
			var duration = ReadDuration(document.RootElement);
			var description = duration > 0
				? TimeSpan.FromSeconds(duration).ToString(DurationFormat)
				: string.Empty;
			return new TrackMetadata(ResolveDisplayName(path, tags.GetValueOrDefault(AlbumArtistTag), tags.GetValueOrDefault(ArtistTag), tags.GetValueOrDefault(TitleTag)), description);
		}
		finally
		{
			StopProcess(process);
			await process.WaitForExitAsync();
			await Task.WhenAll(output, errors);
		}
	}

	/// <summary>
	/// Reads container tags, filling missing values from audio stream tags.
	/// </summary>
	/// <param name="root"> Probe document root. </param>
	/// <returns> Normalized tag dictionary. </returns>
	private static Dictionary<string, string> ReadTags(JsonElement root)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (root.TryGetProperty("format", out var format))
		{
			MergeTags(format, result);
		}

		if (root.TryGetProperty("streams", out var streams))
		{
			foreach (var stream in streams.EnumerateArray())
			{
				if (stream.TryGetProperty("codec_type", out var codecType) && codecType.GetString() == "audio")
				{
					MergeTags(stream, result);
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Adds nonblank tags without replacing an earlier nonblank value.
	/// </summary>
	/// <param name="container"> Format or audio stream metadata object. </param>
	/// <param name="result"> Normalized tag dictionary. </param>
	private static void MergeTags(JsonElement container, Dictionary<string, string> result)
	{
		if (!container.TryGetProperty("tags", out var tags))
		{
			return;
		}

		foreach (var tag in tags.EnumerateObject())
		{
			var name = tag.Name.Replace("_", string.Empty).Replace(" ", string.Empty);
			var value = tag.Value.GetString();
			if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(result.GetValueOrDefault(name)))
			{
				result[name] = value;
			}
		}
	}

	/// <summary>
	/// Reads a finite positive duration when available.
	/// </summary>
	/// <param name="root"> Probe document root. </param>
	/// <returns> Source duration or zero. </returns>
	private static double ReadDuration(JsonElement root)
	{
		const string FormatProperty = "format";
		const string DurationProperty = "duration";

		if (root.TryGetProperty(FormatProperty, out var format)
			&& format.TryGetProperty(DurationProperty, out var duration)
			&& double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds)
			&& double.IsFinite(seconds)
			&& seconds > 0)
		{
			return seconds;
		}

		return 0;
	}

	/// <summary>
	/// Stops a cancelled or timed-out reader.
	/// </summary>
	/// <param name="process"> Reader process. </param>
	private static void StopProcess(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
		}
	}
}
