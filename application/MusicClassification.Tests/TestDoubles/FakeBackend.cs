using Mugnum.MusicClassificationUtility.Core.Analysis;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Tests.TestDoubles;

/// <summary>
/// Simulates inference, failures and worker retirement without loading a model.
/// </summary>
internal sealed class FakeBackend : IAnalysisBackend
{
	/// <summary>
	/// Number of simulated analysis requests.
	/// </summary>
	public int Calls;

	/// <summary>
	/// Maximum simultaneous simulated requests.
	/// </summary>
	public int Peak;

	/// <summary>
	/// Number of active simulated requests.
	/// </summary>
	public int Active;

	/// <summary>
	/// Simulated inference duration in milliseconds.
	/// </summary>
	public int Delay = 30;

	/// <summary>
	/// Number of simulated worker stops.
	/// </summary>
	public int Stops;

	/// <summary>
	/// Worker stops incorrectly attempted during inference.
	/// </summary>
	public int ActiveStops;

	/// <summary>
	/// Number of simulated model loads.
	/// </summary>
	public int ModelStarts;

	/// <summary>
	/// Whether simulated inference should fail.
	/// </summary>
	public bool Fail;

	/// <summary>
	/// Whether the simulated model is loaded.
	/// </summary>
	private bool _loaded;

	/// <summary>
	/// Analyzes a local input while reporting completed audio progress.
	/// </summary>
	/// <param name="path"> Local input path. </param>
	/// <param name="progress"> Progress receiver. </param>
	/// <param name="cancellation"> Operation cancellation token. </param>
	/// <returns> Complete classification result. </returns>
	public async Task<AnalysisResult> AnalyzeAsync(string path,
		IProgress<BackendProgress> progress, CancellationToken cancellation)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(progress);
		cancellation.ThrowIfCancellationRequested();

		Interlocked.Increment(ref Calls);
		var active = Interlocked.Increment(ref Active);
		Peak = Math.Max(Peak, active);

		if (!_loaded)
		{
			_loaded = true;
			ModelStarts++;
		}

		try
		{
			await Task.Delay(Delay, cancellation);
			return Fail
				? throw new FileNotFoundException("Test failure.")
				: new AnalysisResult();
		}
		finally
		{
			Interlocked.Decrement(ref Active);
		}
	}

	/// <summary>
	/// Stops the worker and releases its model resources.
	/// </summary>
	/// <returns> Operation completion. </returns>
	public Task StopAsync()
	{
		if (Active > 0)
		{
			Interlocked.Increment(ref ActiveStops);
		}

		Interlocked.Increment(ref Stops);
		_loaded = false;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Releases the worker and pending lifecycle operations.
	/// </summary>
	/// <returns> Operation completion. </returns>
	public async ValueTask DisposeAsync()
	{
		await StopAsync();
	}
}
