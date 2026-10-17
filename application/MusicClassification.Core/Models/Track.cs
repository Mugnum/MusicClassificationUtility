using Mugnum.MusicClassificationUtility.Core.Infrastructure;

namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Stores an imported track and its observable analysis state.
/// </summary>
public sealed class Track : Observable
{
	/// <summary>
	/// Resolved metadata display name.
	/// </summary>
	private string _displayName = string.Empty;

	/// <summary>
	/// Additional source description.
	/// </summary>
	private string _description = string.Empty;

	/// <summary>
	/// Current queue state.
	/// </summary>
	private TrackStatus _status;

	/// <summary>
	/// Current worker stage.
	/// </summary>
	private string _stage = string.Empty;

	/// <summary>
	/// Completed audio percentage.
	/// </summary>
	private double _progress;

	/// <summary>
	/// Whether progress currently lacks a measurable duration.
	/// </summary>
	private bool _isIndeterminate;

	/// <summary>
	/// Last analysis error.
	/// </summary>
	private string _error = string.Empty;

	/// <summary>
	/// Last completed analysis result.
	/// </summary>
	private AnalysisResult? _result;

	/// <summary>
	/// Creates a track from a local path.
	/// </summary>
	/// <param name="path"> Source file path. </param>
	public Track(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		FullPath = Path.GetFullPath(path);
		DisplayName = FileName;
	}

	/// <summary>
	/// Absolute source file path.
	/// </summary>
	public string FullPath
	{
		get;
	}

	/// <summary>
	/// Source file name including its extension.
	/// </summary>
	public string FileName => Path.GetFileName(FullPath);

	/// <summary>
	/// Resolved metadata name or file name.
	/// </summary>
	public string DisplayName
	{
		get => _displayName; set => Set(ref _displayName, value);
	}

	/// <summary>
	/// Source duration or metadata availability text.
	/// </summary>
	public string Description
	{
		get => _description; set => Set(ref _description, value);
	}

	/// <summary>
	/// Current analysis state.
	/// </summary>
	public TrackStatus Status
	{
		get => _status;
		set
		{
			Set(ref _status, value);
			Changed(nameof(CanAnalyze));
			Changed(nameof(StatusText));
		}
	}

	/// <summary>
	/// Current worker stage text.
	/// </summary>
	public string Stage
	{
		get => _stage;
		set
		{
			Set(ref _stage, value);
			Changed(nameof(StatusText));
		}
	}

	/// <summary>
	/// Human-readable analysis state.
	/// </summary>
	public string StatusText
	{
		get
		{
			const string NotAnalyzedText = "Not analyzed";
			const string QueuedText = "Queued";
			const string AnalyzingText = "Analyzing";
			const string DoneText = "Done";
			const string CancelledText = "Cancelled";
			const string ErrorText = "Error";
			return Status switch
			{
				TrackStatus.NotAnalyzed => NotAnalyzedText,
				TrackStatus.Queued => QueuedText,
				TrackStatus.Analyzing => string.IsNullOrEmpty(Stage)
					? AnalyzingText
					: Stage,
				TrackStatus.Done => DoneText,
				TrackStatus.Cancelled => CancelledText,
				_ => ErrorText
			};
		}
	}

	/// <summary>
	/// Whether a new analysis request can be queued.
	/// </summary>
	public bool CanAnalyze => Status is TrackStatus.NotAnalyzed or TrackStatus.Cancelled or TrackStatus.Error;

	/// <summary>
	/// Completed audio percentage.
	/// </summary>
	public double Progress
	{
		get => _progress; set => Set(ref _progress, value);
	}

	/// <summary>
	/// Whether progress should use an indeterminate indicator.
	/// </summary>
	public bool IsIndeterminate
	{
		get => _isIndeterminate; set => Set(ref _isIndeterminate, value);
	}

	/// <summary>
	/// Last processing error.
	/// </summary>
	public string Error
	{
		get => _error; set => Set(ref _error, value);
	}

	/// <summary>
	/// Last completed classification result.
	/// </summary>
	public AnalysisResult? Result
	{
		get => _result;
		set
		{
			Set(ref _result, value);
			Changed(nameof(GenreSummary));
		}
	}

	/// <summary>
	/// Highest-ranked distinct styles shown in the grid and export summary.
	/// </summary>
	public string GenreSummary
	{
		get
		{
			const int SummaryStyleCount = 3;
			const string StyleSeparator = " · ";
			const string UnavailableSummary = "—";
			return Result == null
				? UnavailableSummary
				: string.Join(StyleSeparator, Result.Overall.Select(score => score.Style).Distinct().Take(SummaryStyleCount));
		}
	}
}

