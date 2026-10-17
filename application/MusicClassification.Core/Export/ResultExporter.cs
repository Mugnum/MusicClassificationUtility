using System.Text;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Export;

/// <summary>
/// Creates reports with a compact song overview before technical details.
/// </summary>
public static class ResultExporter
{
	/// <summary>
	/// Exports the supplied track snapshot.
	/// </summary>
	/// <param name="tracks"> Imported track snapshot. </param>
	/// <returns> Complete text report. </returns>
	public static string Create(IEnumerable<Track> tracks)
	{
		const string Title = "Music Classification Utility";
		const string SummaryHeading = "SONG SUMMARY";
		const string DetailsHeading = "DETAILED RESULTS";
		const int SeparatorLength = 72;

		ArgumentNullException.ThrowIfNull(tracks);
		var snapshot = tracks.ToList();

		if (snapshot.Any(track => track == null))
		{
			throw new ArgumentException("Tracks cannot contain null entries.", nameof(tracks));
		}

		var text = new StringBuilder();
		text.AppendLine(Title)
			.AppendLine()
			.AppendLine(SummaryHeading);

		foreach (var track in snapshot)
		{
			text.AppendLine($"{track.DisplayName} | {track.GenreSummary}");
		}

		text.AppendLine()
			.AppendLine(DetailsHeading)
			.AppendLine("MAEST / Discogs 519 styles")
			.AppendLine("Scores are independent model outputs, not calibrated probabilities.")
			.AppendLine("Overall scores use retained original duration. Final segments under 10 seconds are excluded.")
			.AppendLine();

		foreach (var track in snapshot)
		{
			text.AppendLine(track.DisplayName)
				.AppendLine(track.FullPath)
				.AppendLine($"Status: {track.StatusText}");

			if (!string.IsNullOrEmpty(track.Error))
			{
				text.AppendLine($"Error: {track.Error}");
			}

			if (track.Result is { } result)
			{
				AppendResult(text, result);
			}

			text.AppendLine().AppendLine(new string('─', SeparatorLength)).AppendLine();
		}

		return text.ToString();
	}

	/// <summary>
	/// Appends scores, exclusions and retained segment weights.
	/// </summary>
	/// <param name="text"> Report builder. </param>
	/// <param name="result"> Complete result. </param>
	private static void AppendResult(StringBuilder text, AnalysisResult result)
	{
		const string OverallDurationWeightedText = "Overall (duration-weighted):";
		text.AppendLine($"Audio: {result.DurationSeconds:0.00}s " +
				$"| Scored: {result.AnalyzedDurationSeconds:0.00}s " +
				$"| Ignored tail: {result.IgnoredTailSeconds:0.00}s " +
				$"| Analysis: {result.AnalysisSeconds:0.00}s")
			.AppendLine(OverallDurationWeightedText);

		foreach (var score in result.Overall)
		{
			text.AppendLine($"  {score.Display}");
		}
		foreach (var warning in result.Warnings)
		{
			text.AppendLine($"Warning: {warning}");
		}
		foreach (var segment in result.Segments)
		{
			text.AppendLine($"Window {segment.StartSeconds:0.00}–{segment.EndSeconds:0.00}s " +
				$"| Weight: {segment.ValidDurationSeconds:0.00}s " +
				$"| Repeated padding: {segment.RepeatedPaddingSeconds:0.00}s");

			foreach (var score in segment.TopGenres)
			{
				text.AppendLine($"  {score.Display}");
			}
		}
	}
}
