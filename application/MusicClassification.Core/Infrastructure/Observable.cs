using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mugnum.MusicClassificationUtility.Core.Infrastructure;

/// <summary>
/// Provides property notifications for bound application state.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
	/// <summary>
	/// Occurs when a bound property changes.
	/// </summary>
	public event PropertyChangedEventHandler PropertyChanged;

	/// <summary>
	/// Raises a property notification.
	/// </summary>
	/// <param name="name"> Changed property name. </param>
	protected void Changed([CallerMemberName] string name = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	/// <summary>
	/// Replaces a property value and notifies its observers when necessary.
	/// </summary>
	/// <typeparam name="T"> Property value type. </typeparam>
	/// <param name="field"> Backing field. </param>
	/// <param name="value"> New value. </param>
	/// <param name="name"> Changed property name. </param>
	/// <returns> Whether the value changed. </returns>
	protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}

		field = value;
		Changed(name);
		return true;
	}
}
