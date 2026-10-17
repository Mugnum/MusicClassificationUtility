namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Stores a track's complete duration-weighted classification result.
/// </summary>
public sealed class AnalysisResult
{
	/// <summary>
	/// Absolute input file path.
	/// </summary>
	public string File { get; set; } = string.Empty;

	/// <summary>
	/// Complete decoded source duration.
	/// </summary>
	public double DurationSeconds
	{
		get; set;
	}

	/// <summary>
	/// Original source duration contributing to the scores.
	/// </summary>
	public double AnalyzedDurationSeconds
	{
		get; set;
	}

	/// <summary>
	/// Excluded final segment duration.
	/// </summary>
	public double IgnoredTailSeconds
	{
		get; set;
	}

	/// <summary>
	/// Wall-clock inference and decoding duration.
	/// </summary>
	public double AnalysisSeconds
	{
		get; set;
	}

	/// <summary>
	/// Highest-ranked duration-weighted labels.
	/// </summary>
	public List<GenreScore> Overall { get; set; } = [];

	/// <summary>
	/// Complete duration-weighted model score vector.
	/// </summary>
	public double[] OverallScores { get; set; } = [];

	/// <summary>
	/// Retained segment results.
	/// </summary>
	public List<SegmentResult> Segments { get; set; } = [];

	/// <summary>
	/// Padding, silence and excluded-tail explanations.
	/// </summary>
	public List<string> Warnings { get; set; } = [];
}

