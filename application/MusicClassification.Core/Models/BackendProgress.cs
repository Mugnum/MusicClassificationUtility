namespace Mugnum.MusicClassificationUtility.Core.Models;

/// <summary>
/// Describes completed audio progress and the current worker stage.
/// </summary>
public sealed class BackendProgress
{
	/// <summary>
	/// Creates a validated progress update.
	/// </summary>
	/// <param name="percent"> Completed audio percentage, or null while unknown. </param>
	/// <param name="message"> Current worker stage. </param>
	public BackendProgress(double? percent, string message)
	{
		const int MaximumPercentage = 100;
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		if (percent.HasValue && (!double.IsFinite(percent.Value) || percent.Value < 0 || percent.Value > MaximumPercentage))
		{
			throw new ArgumentOutOfRangeException(nameof(percent));
		}

		Percent = percent;
		Message = message;
	}

	/// <summary>
	/// Completed audio percentage, or null while unknown.
	/// </summary>
	public double? Percent
	{
		get;
	}

	/// <summary>
	/// Current worker stage.
	/// </summary>
	public string Message
	{
		get;
	}
}

