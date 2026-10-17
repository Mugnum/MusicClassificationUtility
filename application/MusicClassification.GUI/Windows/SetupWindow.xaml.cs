using System.ComponentModel;
using System.Windows;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Runtime;

namespace Mugnum.MusicClassificationUtility.GUI.Windows;

/// <summary>
/// Displays asynchronous runtime setup output and cancellation controls.
/// </summary>
public partial class SetupWindow : Window
{
	/// <summary>
	/// Installation configuration independent of unsaved field edits.
	/// </summary>
	private readonly AppSettings _settings;

	/// <summary>
	/// Cancels setup when requested by the user.
	/// </summary>
	private readonly CancellationTokenSource _cancellation = new();

	/// <summary>
	/// Whether setup is currently running.
	/// </summary>
	private bool _running = true;

	/// <summary>
	/// Creates the modal setup progress window.
	/// </summary>
	/// <param name="settings"> Runtime installation configuration. </param>
	public SetupWindow(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		_settings = settings.Copy();
		InitializeComponent();
	}

	/// <summary>
	/// Starts the observed setup task after the window becomes visible.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnLoaded(object sender, RoutedEventArgs eventArgs)
	{
		_ = InitializeAsync();
	}

	/// <summary>
	/// Runs setup and presents completion, cancellation or failure in the window.
	/// </summary>
	/// <returns> Completion after all setup output has been handled. </returns>
	private async Task InitializeAsync()
	{
		try
		{
			SetupStatusText.Text = "Initializing runtime…";
			AppendLog($"Runtime folder: {_settings.RuntimeDirectory}");
			AppendLog($"Model folder: {_settings.Models}");
			var progress = new Progress<string>(AppendLog);
			await RuntimeSetup.RunAsync(_settings, AppContext.BaseDirectory, progress, _cancellation.Token);
			SetupStatusText.Text = "Runtime initialized successfully.";
		}
		catch (OperationCanceledException)
		{
			SetupStatusText.Text = "Initialization cancelled. You can initialize again to continue.";
		}
		catch (Exception exception)
		{
			SetupStatusText.Text = "Initialization failed.";
			AppendLog(exception.Message);
		}
		finally
		{
			_running = false;
			SetupProgress.IsIndeterminate = false;
			CloseButton.IsEnabled = true;
			CloseButton.Content = "Close";
			_cancellation.Dispose();
		}
	}

	/// <summary>
	/// Appends a bounded output log and keeps the newest line visible.
	/// </summary>
	/// <param name="line"> Setup output line. </param>
	private void AppendLog(string line)
	{
		const int MaximumLogCharacters = 200000;
		if (SetupLog.Text.Length > MaximumLogCharacters)
		{
			SetupLog.Text = SetupLog.Text[^MaximumLogCharacters..];
		}

		SetupLog.AppendText(line + Environment.NewLine);
		SetupLog.ScrollToEnd();
	}

	/// <summary>
	/// Cancels a running setup or closes the completed progress window.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
	{
		if (_running)
		{
			CloseButton.IsEnabled = false;
			SetupStatusText.Text = "Cancelling initialization…";
			_cancellation.Cancel();
			return;
		}

		Close();
	}

	/// <summary>
	/// Keeps the progress window open until cancellation releases child processes.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Window closing event. </param>
	private void OnClosing(object sender, CancelEventArgs eventArgs)
	{
		if (_running)
		{
			eventArgs.Cancel = true;
			_cancellation.Cancel();
		}
	}
}

