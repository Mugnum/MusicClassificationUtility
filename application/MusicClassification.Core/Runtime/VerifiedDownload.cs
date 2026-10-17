using System.Net.Http;
using System.Security.Cryptography;

namespace Mugnum.MusicClassificationUtility.Core.Runtime;

/// <summary>
/// Downloads runtime assets and publishes them only after checksum verification.
/// </summary>
internal sealed class VerifiedDownload
{
	/// <summary>
	/// HTTP client owned by the current setup operation.
	/// </summary>
	private readonly HttpClient _client;

	/// <summary>
	/// Creates a downloader using the setup operation's HTTP client.
	/// </summary>
	/// <param name="client"> HTTP transport used for runtime downloads. </param>
	internal VerifiedDownload(HttpClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		_client = client;
	}

	/// <summary>
	/// Verifies an existing asset or downloads and verifies a missing asset.
	/// </summary>
	/// <param name="url"> Remote asset URL. </param>
	/// <param name="destination"> Final local asset path. </param>
	/// <param name="expectedChecksum"> Expected SHA-256 digest. </param>
	/// <param name="offline"> Whether network access is disabled. </param>
	/// <param name="progress"> Receives verification and download progress. </param>
	/// <param name="cancellation"> Cancels download and checksum work. </param>
	/// <returns> Completion after the final asset is verified. </returns>
	internal async Task EnsureAsync(string url, string destination, string expectedChecksum, bool offline,
		IProgress<string> progress, CancellationToken cancellation)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(url);
		ArgumentException.ThrowIfNullOrWhiteSpace(destination);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedChecksum);
		ArgumentNullException.ThrowIfNull(progress);
		cancellation.ThrowIfCancellationRequested();
		if (File.Exists(destination))
		{
			await VerifyAsync(destination, expectedChecksum, cancellation).ConfigureAwait(false);
			progress.Report($"Verified: {Path.GetFileName(destination)}");
			return;
		}

		if (offline)
		{
			throw new FileNotFoundException($"Offline mode: download {url} into {destination}, or disable offline mode in appsettings.json.", destination);
		}

		var partial = destination + ".part";
		try
		{
			progress.Report($"Downloading: {url}");
			await DownloadAsync(url, partial, progress, cancellation).ConfigureAwait(false);
			await VerifyAsync(partial, expectedChecksum, cancellation).ConfigureAwait(false);
			File.Move(partial, destination, overwrite: true);
			progress.Report($"Verified: {Path.GetFileName(destination)}");
		}
		finally
		{
			File.Delete(partial);
		}
	}

	/// <summary>
	/// Streams a remote asset to disk while reporting transferred bytes.
	/// </summary>
	/// <param name="url"> Remote asset URL. </param>
	/// <param name="partial"> Temporary file owned by this download. </param>
	/// <param name="progress"> Download progress recipient. </param>
	/// <param name="cancellation"> Cancels HTTP and disk operations. </param>
	/// <returns> Completion after the temporary file has closed. </returns>
	private async Task DownloadAsync(string url, string partial, IProgress<string> progress, CancellationToken cancellation)
	{
		const int BufferSize = 81920;
		const double BytesPerMegabyte = 1048576.0;
		const double MaximumPercentage = 100.0;
		const long ProgressIntervalBytes = 8 * 1024 * 1024;
		using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
		await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
			BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
		var buffer = new byte[BufferSize];
		var received = 0L;
		var reported = 0L;
		var length = response.Content.Headers.ContentLength;
		while (true)
		{
			var count = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false);
			if (count == 0)
			{
				break;
			}

			await target.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
			received += count;
			if (received - reported >= ProgressIntervalBytes)
			{
				reported = received;
				progress.Report(length > 0
					? $"Downloaded {received / BytesPerMegabyte:0.0}/{length / BytesPerMegabyte:0.0} MB ({received * MaximumPercentage / length:0}%)."
					: $"Downloaded {received / BytesPerMegabyte:0.0} MB.");
			}
		}
	}

	/// <summary>
	/// Compares a local file with the pinned SHA-256 digest.
	/// </summary>
	/// <param name="path"> Asset file to verify. </param>
	/// <param name="expectedChecksum"> Expected hexadecimal digest. </param>
	/// <param name="cancellation"> Cancels checksum calculation. </param>
	/// <returns> Completion when the checksum matches. </returns>
	private static async Task VerifyAsync(string path, string expectedChecksum, CancellationToken cancellation)
	{
		await using var stream = File.OpenRead(path);
		var checksum = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
		if (!Convert.ToHexString(checksum).Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Checksum mismatch: {path}. Move the file aside and initialize again.");
		}
	}
}

