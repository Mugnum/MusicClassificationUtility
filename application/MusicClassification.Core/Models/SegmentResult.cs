using System.Text.Json.Serialization;

namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Contains scores for one retained audio segment.
/// </summary>
public sealed class SegmentResult
{
	/// <summary>
	/// Segment start in source seconds.
	/// </summary>
	public double StartSeconds
	{
		get; set;
	}

	/// <summary>
	/// Segment end in source seconds.
	/// </summary>
	public double EndSeconds
	{
		get; set;
	}

	/// <summary>
	/// Original audio duration used as the aggregation weight.
	/// </summary>
	public double ValidDurationSeconds
	{
		get; set;
	}

	/// <summary>
	/// Repeated audio added to fill the model input.
	/// </summary>
	public double RepeatedPaddingSeconds
	{
		get; set;
	}

	/// <summary>
	/// Whether the backend reported limited original context.
	/// </summary>
	public bool LimitedSourceContext
	{
		get; set;
	}

	/// <summary>
	/// Highest-ranked complete genre labels.
	/// </summary>
	public List<GenreScore> TopGenres { get; set; } = [];

	/// <summary>
	/// Complete model score vector.
	/// </summary>
	public double[] Scores { get; set; } = [];

	/// <summary>
	/// Formatted source time interval.
	/// </summary>
	[JsonIgnore]
	public string Range
	{
		get
		{
			const string TimeFormat = @"mm\:ss";
			return $"{TimeSpan.FromSeconds(StartSeconds).ToString(TimeFormat)}–{TimeSpan.FromSeconds(EndSeconds).ToString(TimeFormat)}";
		}
	}

	/// <summary>
	/// Human-readable repeated-padding description.
	/// </summary>
	[JsonIgnore]
	public string PaddingText => RepeatedPaddingSeconds > 0
		? $"Repeated padding: {RepeatedPaddingSeconds:0.00}s"
		: string.Empty;
}

