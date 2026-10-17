namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Represents one compact JSON-lines analysis request.
/// </summary>
public sealed class AnalysisRequest
{
	/// <summary>
	/// Creates a validated request for a local file.
	/// </summary>
	/// <param name="id"> Unique request identifier. </param>
	/// <param name="path"> Local source path. </param>
	public AnalysisRequest(string id, string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Id = id;
		Path = System.IO.Path.GetFullPath(path);
	}

	/// <summary>
	/// Unique request identifier echoed by worker responses.
	/// </summary>
	public string Id
	{
		get;
	}

	/// <summary>
	/// Protocol operation name.
	/// </summary>
	public string Type
	{
		get
		{
			const string AnalyzeOperation = "analyze";
			return AnalyzeOperation;
		}
	}

	/// <summary>
	/// Absolute local source path.
	/// </summary>
	public string Path
	{
		get;
	}
}

