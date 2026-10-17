using System.Diagnostics;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Analysis;

/// <summary>
/// Releases an idle model without interrupting active or queued analysis requests.
/// </summary>
public sealed class RetainedAnalysisBackend : IAnalysisBackend
{
	/// <summary>
	/// Restartable analysis backend.
	/// </summary>
	private readonly IAnalysisBackend _backend;

	/// <summary>
	/// Configured interval before releasing an idle worker.
	/// </summary>
	private readonly TimeSpan _idleRetention;

	/// <summary>
	/// Serializes worker lifecycle transitions.
	/// </summary>
	private readonly SemaphoreSlim _gate = new(1);

	/// <summary>
	/// Invalidates an obsolete idle-release timer.
	/// </summary>
	private CancellationTokenSource _idleCancellation;

	/// <summary>
	/// Pending model retirement completion.
	/// </summary>
	private Task _idleTask = Task.CompletedTask;

	/// <summary>
	/// Whether the backend has been disposed.
	/// </summary>
	private bool _disposed;

	/// <summary>
	/// Creates an idle-retention wrapper around a restartable local backend.
	/// </summary>
	/// <param name="backend"> Underlying model backend. </param>
	/// <param name="idleRetention"> Idle interval, with zero retaining the model until disposal. </param>
	public RetainedAnalysisBackend(IAnalysisBackend backend, TimeSpan idleRetention)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentOutOfRangeException.ThrowIfLessThan(idleRetention, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(idleRetention,
			TimeSpan.FromMinutes(AppSettings.MaximumIdleRetentionMinutes));

		_backend = backend;
		_idleRetention = idleRetention;
	}

	/// <inheritdoc/>
	public async Task<AnalysisResult> AnalyzeAsync(string path,
		IProgress<BackendProgress> progress, CancellationToken cancellation)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(progress);
		ObjectDisposedException.ThrowIf(_disposed, this);

		await _gate.WaitAsync(cancellation).ConfigureAwait(false);
		var succeeded = false;

		try
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			CancelIdleRelease();
			var result = await _backend.AnalyzeAsync(path, progress, cancellation).ConfigureAwait(false);
			succeeded = true;
			return result;
		}
		finally
		{
			if (succeeded && _idleRetention > TimeSpan.Zero)
			{
				_idleCancellation = new CancellationTokenSource();
				_idleTask = ReleaseAfterIdleAsync(_idleCancellation.Token);
			}

			_gate.Release();
		}
	}

	/// <inheritdoc/>
	public async Task StopAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _gate.WaitAsync().ConfigureAwait(false);

		try
		{
			CancelIdleRelease();
			await _backend.StopAsync().ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Cancels idle retirement and disposes the underlying worker.
	/// </summary>
	/// <returns> Worker disposal completion. </returns>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		await _gate.WaitAsync().ConfigureAwait(false);

		try
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			CancelIdleRelease();
			await _backend.DisposeAsync().ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}

		await _idleTask.ConfigureAwait(false);
	}

	/// <summary>
	/// Stops an idle worker after confirming no new request acquired the gate.
	/// </summary>
	/// <param name="cancellation"> Idle interval cancellation token. </param>
	/// <returns> Idle release completion. </returns>
	private async Task ReleaseAfterIdleAsync(CancellationToken cancellation)
	{
		try
		{
			await Task.Delay(_idleRetention, cancellation).ConfigureAwait(false);
			await _gate.WaitAsync(cancellation).ConfigureAwait(false);

			try
			{
				cancellation.ThrowIfCancellationRequested();
				await _backend.StopAsync().ConfigureAwait(false);
			}
			finally
			{
				_gate.Release();
			}
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			Trace.TraceError("Idle model release failed: {0}", exception);
		}
	}

	/// <summary>
	/// Invalidates any pending idle release before a worker lifecycle change.
	/// </summary>
	private void CancelIdleRelease()
	{
		_idleCancellation?.Cancel();
		_idleCancellation?.Dispose();
		_idleCancellation = null;
	}
}
