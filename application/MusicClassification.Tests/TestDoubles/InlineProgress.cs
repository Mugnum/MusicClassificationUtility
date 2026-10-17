namespace Mugnum.MusicClassificationUtility.Tests.TestDoubles;

/// <summary>
/// Delivers deterministic protocol progress during integration checks.
/// </summary>
/// <typeparam name="T"> Progress payload type. </typeparam>
internal sealed class InlineProgress<T> : IProgress<T>
{
	/// <summary>
	/// Validated synchronous progress receiver.
	/// </summary>
	private readonly Action<T> _callback;

	/// <summary>
	/// Creates a validated inline progress instance.
	/// </summary>
	/// <param name="callback"> Progress receiver callback. </param>
	public InlineProgress(Action<T> callback)
	{
		ArgumentNullException.ThrowIfNull(callback);
		_callback = callback;
	}

	/// <summary>
	/// Delivers a progress value directly to its validated callback.
	/// </summary>
	/// <param name="value"> Progress payload. </param>
	public void Report(T value)
	{
		ArgumentNullException.ThrowIfNull(value);
		_callback(value);
	}
}
