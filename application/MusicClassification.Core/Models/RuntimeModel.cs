namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Identifies the pinned model checkpoint required by the Python runtime.
/// </summary>
public sealed class RuntimeModel
{
	/// <summary>
	/// Checkpoint download URL.
	/// </summary>
	public string Url { get; set; } = string.Empty;

	/// <summary>
	/// Checkpoint file name inside the selected model folder.
	/// </summary>
	public string Filename { get; set; } = string.Empty;

	/// <summary>
	/// Expected SHA-256 checksum of the model checkpoint.
	/// </summary>
	public string Sha256 { get; set; } = string.Empty;
}

