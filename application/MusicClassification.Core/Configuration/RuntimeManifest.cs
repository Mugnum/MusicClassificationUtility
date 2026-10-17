using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Configuration;

/// <summary>
/// Loads the same release manifest used by the Python model adapter.
/// </summary>
public sealed class RuntimeManifest
{
	/// <summary>
	/// Pinned upstream implementation archive.
	/// </summary>
	public RuntimeSource Source { get; set; } = new();

	/// <summary>
	/// Pinned pretrained model checkpoint.
	/// </summary>
	public RuntimeModel Model { get; set; } = new();

	/// <summary>
	/// Reads and validates the runtime manifest shipped with the Python adapter.
	/// </summary>
	/// <param name="pythonDirectory"> Application-owned Python adapter directory. </param>
	/// <returns> Manifest with validated URLs, checksums and local file names. </returns>
	public static RuntimeManifest Load(string pythonDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pythonDirectory);
		var manifest = JsonSerializer.Deserialize<RuntimeManifest>(
			File.ReadAllText(Path.Combine(pythonDirectory, "runtime-manifest.json")), JsonDefaults.Options)
			?? throw new InvalidDataException("The runtime manifest is empty.");
		ArgumentNullException.ThrowIfNull(manifest.Source);
		ArgumentNullException.ThrowIfNull(manifest.Model);
		ValidateAsset(manifest.Source.Url, manifest.Source.Archive, manifest.Source.Sha256);
		ValidateAsset(manifest.Model.Url, manifest.Model.Filename, manifest.Model.Sha256);
		ValidateFileName(manifest.Source.Directory);
		return manifest;
	}

	/// <summary>
	/// Validates the network location and integrity information for a runtime asset.
	/// </summary>
	/// <param name="url"> Asset download URL. </param>
	/// <param name="name"> Local asset file name. </param>
	/// <param name="checksum"> Expected SHA-256 digest. </param>
	private static void ValidateAsset(string url, string name, string checksum)
	{
		const int Sha256HexLength = 64;
		ValidateFileName(name);
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
		{
			throw new InvalidDataException("Runtime download URLs must use HTTP or HTTPS.");
		}

		if (checksum == null || checksum.Length != Sha256HexLength || checksum.Any(character => !Uri.IsHexDigit(character)))
		{
			throw new InvalidDataException("Runtime checksums must contain a SHA-256 hexadecimal digest.");
		}
	}

	/// <summary>
	/// Rejects paths where the manifest should provide only a single file or folder name.
	/// </summary>
	/// <param name="name"> Manifest file or folder name. </param>
	private static void ValidateFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
			|| name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name)
		{
			throw new InvalidDataException("Runtime manifest paths must be single file or folder names.");
		}
	}
}

