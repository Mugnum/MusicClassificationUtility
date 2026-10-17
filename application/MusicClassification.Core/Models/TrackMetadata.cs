namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Contains display metadata resolved once during import.
/// </summary>
public sealed class TrackMetadata
{
	/// <summary>
	/// Creates a metadata result.
	/// </summary>
	/// <param name="name"> Resolved track name. </param>
	/// <param name="description"> Source duration description. </param>
	public TrackMetadata(string name, string description)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(description);
		Name = name;
		Description = description;
	}

	/// <summary>
	/// Resolved track name.
	/// </summary>
	public string Name
	{
		get;
	}

	/// <summary>
	/// Source duration description.
	/// </summary>
	public string Description
	{
		get;
	}
}

