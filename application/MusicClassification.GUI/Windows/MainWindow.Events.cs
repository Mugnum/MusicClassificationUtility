using System.ComponentModel;
using System.Windows;

namespace Mugnum.MusicClassificationUtility.GUI.Windows;

/// <summary>
/// Adapts WPF events to observable asynchronous operations.
/// </summary>
public partial class MainWindow
{
	/// <summary>
	/// Starts file import through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnImportClick(object sender, RoutedEventArgs eventArgs)
	{
		_ = RunUiActionAsync(() => OnImportClickAsync(sender, eventArgs));
	}

	/// <summary>
	/// Starts dropped-file import through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnTracksDrop(object sender, DragEventArgs eventArgs)
	{
		eventArgs.Handled = true;
		_ = RunUiActionAsync(() => OnTracksDropAsync(sender, eventArgs));
	}

	/// <summary>
	/// Starts analysis cancellation through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
	{
		_ = RunUiActionAsync(() => OnCancelClickAsync(sender, eventArgs));
	}

	/// <summary>
	/// Starts list clearing through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnClearClick(object sender, RoutedEventArgs eventArgs)
	{
		_ = RunUiActionAsync(() => OnClearClickAsync(sender, eventArgs));
	}

	/// <summary>
	/// Starts report export through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnExportClick(object sender, RoutedEventArgs eventArgs)
	{
		_ = RunUiActionAsync(() => OnExportClickAsync(sender, eventArgs));
	}

	/// <summary>
	/// Starts settings updates through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnSettingsClick(object sender, RoutedEventArgs eventArgs)
	{
		_ = RunUiActionAsync(() => OnSettingsClickAsync(sender, eventArgs));
	}

	/// <summary>
	/// Defers closing until asynchronous worker cleanup has completed.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnWindowClosing(object sender, CancelEventArgs eventArgs)
	{
		if (_allowClose)
		{
			return;
		}

		eventArgs.Cancel = true;
		_ = RunUiActionAsync(() => OnWindowClosingAsync(sender, eventArgs));
	}

	/// <summary>
	/// Starts offscreen validation through the asynchronous error boundary.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnSelfTestLoaded(object sender, RoutedEventArgs eventArgs)
	{
		_ = RunUiActionAsync(SelfTestAsync);
	}

	/// <summary>
	/// Observes UI operation failures and restores controls after an interrupted action.
	/// </summary>
	/// <param name="action"> Asynchronous UI operation. </param>
	/// <returns> Completion after the operation or its error has been handled. </returns>
	private async Task RunUiActionAsync(Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception exception)
		{
			_clearing = false;
			_closing = false;
			IsEnabled = true;
			UpdateControls();
			StatusText.Text = "Operation failed. " + exception.Message;
			if (_selfTest)
			{
				Environment.ExitCode = 1;
				Application.Current.Shutdown(1);
				return;
			}

			MessageBox.Show(this, exception.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
}
