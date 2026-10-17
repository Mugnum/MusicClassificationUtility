namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Identifies the pinned upstream source archive used by the runtime.
/// </summary>
public sealed class RuntimeSource
{
	/// <summary>
	/// Source archive download URL.
	/// </summary>
	public string Url { get; set; } = string.Empty;

	/// <summary>
	/// Archive file name inside runtime downloads.
	/// </summary>
	public string Archive { get; set; } = string.Empty;

	/// <summary>
	/// Source directory name inside runtime vendor files.
	/// </summary>
	public string Directory { get; set; } = string.Empty;

	/// <summary>
	/// Expected SHA-256 checksum of the source archive.
	/// </summary>
	public string Sha256 { get; set; } = string.Empty;
}

