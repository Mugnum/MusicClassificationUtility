using System.Globalization;
using System.Text.Json.Serialization;

namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Associates a Discogs label with an independent model score.
/// </summary>
public sealed class GenreScore
{
	/// <summary>
	/// Separator used by the model's parent genre and style vocabulary.
	/// </summary>
	private const string LabelSeparator = "---";

	/// <summary>
	/// Full model label.
	/// </summary>
	public string Label { get; set; } = string.Empty;

	/// <summary>
	/// Independent sigmoid score.
	/// </summary>
	public double Score
	{
		get; set;
	}

	/// <summary>
	/// Style name without its parent genre.
	/// </summary>
	[JsonIgnore]
	public string Style => Label.Contains(LabelSeparator, StringComparison.Ordinal)
		? Label.Split(LabelSeparator, 2)[1]
		: Label;

	/// <summary>
	/// Formatted score and complete label.
	/// </summary>
	[JsonIgnore]
	public string Display
	{
		get
		{
			const string ScoreFormat = "0.0000";
			const string DisplaySeparator = " / ";
			return $"{Score.ToString(ScoreFormat, CultureInfo.InvariantCulture)}   {Label.Replace(LabelSeparator, DisplaySeparator)}";
		}
	}
}

