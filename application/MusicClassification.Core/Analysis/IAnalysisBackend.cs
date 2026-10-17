using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Analysis;

/// <summary>
/// Defines an asynchronous local classifier lifecycle.
/// </summary>
public interface IAnalysisBackend : IAsyncDisposable
{
	/// <summary>
	/// Analyzes a local file while reporting completed audio progress.
	/// </summary>
	/// <param name="path"> Local input path. </param>
	/// <param name="progress"> Progress receiver. </param>
	/// <param name="cancellation"> Request cancellation token. </param>
	/// <returns> Complete classification result. </returns>
	Task<AnalysisResult> AnalyzeAsync(string path, IProgress<BackendProgress> progress, CancellationToken cancellation);

	/// <summary>
	/// Stops the model worker and its child processes.
	/// </summary>
	/// <returns> Worker shutdown completion. </returns>
	Task StopAsync();
}
