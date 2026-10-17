using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Analysis;

/// <summary>
/// Serializes analysis requests on the caller's UI synchronization context.
/// </summary>
public sealed class AnalysisQueue : Observable
{
	/// <summary>
	/// Restartable analysis backend.
	/// </summary>
	private readonly IAnalysisBackend _backend;

	/// <summary>
	/// Serializes analysis requests on the caller's UI synchronization context.
	/// </summary>
	/// <param name="backend"> Restartable local classifier. </param>
	public AnalysisQueue(IAnalysisBackend backend)
	{
		ArgumentNullException.ThrowIfNull(backend);
		_backend = backend;
	}

	/// <summary>
	/// Tracks waiting for analysis.
	/// </summary>
	private readonly Queue<Track> _pending = new();

	/// <summary>
	/// Tracks already active or pending.
	/// </summary>
	private readonly HashSet<Track> _scheduled = [];

	/// <summary>
	/// Cancellation source for the current queue run.
	/// </summary>
	private CancellationTokenSource _cancellation;

	/// <summary>
	/// Whether queued processing is active.
	/// </summary>
	public bool IsRunning
	{
		get;
		private set => Set(ref field, value);
	}

	/// <summary>
	/// Completion of the current queue run.
	/// </summary>
	public Task WhenIdle { get; private set; } = Task.CompletedTask;

	/// <summary>
	/// Occurs when an analysis error stops the queue.
	/// </summary>
	public event Action<string> Failed;

	/// <summary>
	/// Occurs when queue state or a completed result changes.
	/// </summary>
	public event Action Updated;

	/// <summary>
	/// Queues distinct tracks and starts processing when idle.
	/// </summary>
	/// <param name="tracks"> Tracks to queue. </param>
	public void Enqueue(IEnumerable<Track> tracks)
	{
		ArgumentNullException.ThrowIfNull(tracks);
		var snapshot = tracks.ToList();

		if (snapshot.Any(track => track == null))
		{
			throw new ArgumentException("Tracks cannot contain null entries.", nameof(tracks));
		}

		foreach (var track in snapshot.Where(track => track.CanAnalyze && _scheduled.Add(track)))
		{
			track.Status = TrackStatus.Queued;
			track.Progress = 0;
			track.Error = string.Empty;
			_pending.Enqueue(track);
		}

		if (IsRunning || _pending.Count == 0)
		{
			return;
		}

		_cancellation?.Dispose();
		_cancellation = new CancellationTokenSource();
		IsRunning = true;
		WhenIdle = PumpAsync(_cancellation.Token);
	}

	/// <summary>
	/// Processes queued tracks sequentially and releases pending work on failure.
	/// </summary>
	/// <param name="token"> Operation cancellation token. </param>
	/// <returns> Operation completion. </returns>
	private async Task PumpAsync(CancellationToken token)
	{
		const int CompletedPercentage = 100;
		const int MaximumInProgressPercentage = 99;

		// Publish WhenIdle before processing can complete synchronously.
		await Task.Yield();

		try
		{
			while (_pending.TryDequeue(out var track))
			{
				if (token.IsCancellationRequested)
				{
					_scheduled.Remove(track);
					track.Status = TrackStatus.NotAnalyzed;
					break;
				}

				track.Result = null;
				track.Status = TrackStatus.Analyzing;
				track.Stage = "Starting";
				track.IsIndeterminate = true;

				try
				{
					var localTrack = track;
					var progress = new Progress<BackendProgress>(value =>
					{
						if (localTrack.Status != TrackStatus.Analyzing)
						{
							return;
						}

						localTrack.Stage = value.Message;
						localTrack.IsIndeterminate = !value.Percent.HasValue;

						if (value.Percent is { } percent)
						{
							localTrack.Progress = Math.Clamp(percent, 0, MaximumInProgressPercentage);
						}
					});
					track.Result = await _backend.AnalyzeAsync(track.FullPath, progress, token);
					track.Progress = CompletedPercentage;
					track.Status = TrackStatus.Done;
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					track.Status = TrackStatus.Cancelled;
					track.Progress = 0;
					break;
				}
				catch (Exception exc)
				{
					track.Error = exc.Message;
					track.Status = TrackStatus.Error;
					Failed?.Invoke(exc.Message);
					break;
				}
				finally
				{
					track.IsIndeterminate = false;
					_scheduled.Remove(track);
					Updated?.Invoke();
				}
			}
		}
		finally
		{
			while (_pending.TryDequeue(out var track))
			{
				track.Status = TrackStatus.NotAnalyzed;
				_scheduled.Remove(track);
			}

			IsRunning = false;
			Updated?.Invoke();
		}
	}

	/// <summary>
	/// Cancels active analysis and clears pending queue requests.
	/// </summary>
	/// <returns> Operation completion. </returns>
	public async Task CancelAsync()
	{
		if (!IsRunning)
		{
			return;
		}
		if (_cancellation != null)
		{
			await _cancellation.CancelAsync();
		}
		await WhenIdle;
	}
}
