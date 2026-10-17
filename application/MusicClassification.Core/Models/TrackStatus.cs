namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Describes the analysis state of an imported track.
/// </summary>
public enum TrackStatus
{
	/// <summary>
	/// The track has not been queued.
	/// </summary>
	NotAnalyzed,

	/// <summary>
	/// The track is waiting for the worker.
	/// </summary>
	Queued,

	/// <summary>
	/// The worker is processing the track.
	/// </summary>
	Analyzing,

	/// <summary>
	/// The track has a completed result.
	/// </summary>
	Done,

	/// <summary>
	/// Processing was cancelled.
	/// </summary>
	Cancelled,

	/// <summary>
	/// Processing failed.
	/// </summary>
	Error
}

