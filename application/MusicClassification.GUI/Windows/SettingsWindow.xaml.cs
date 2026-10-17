using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Runtime;

namespace Mugnum.MusicClassificationUtility.GUI.Windows;

/// <summary>
/// Edits runtime paths, automation and model retention preferences.
/// </summary>
public partial class SettingsWindow : Window
{
	/// <summary>
	/// Releases a retained worker before setup can update its environment.
	/// </summary>
	private readonly Func<Task> _prepareSetup;

	/// <summary>
	/// Creates an editor with an independent settings snapshot.
	/// </summary>
	/// <param name="settings"> Current validated preferences. </param>
	/// <param name="prepareSetup"> Optional worker shutdown before initialization. </param>
	public SettingsWindow(AppSettings settings, Func<Task> prepareSetup = null)
	{
		ArgumentNullException.ThrowIfNull(settings);
		Result = settings.Copy();
		_prepareSetup = prepareSetup ?? (() => Task.CompletedTask);
		InitializeComponent();
		ThreadsBox.ToolTip = $"Use 1–{AppSettings.AvailableThreads} threads. The default uses available logical processors, up to {AppSettings.MaximumThreads}. Invalid values are corrected when saved.";
		LoadFields(Result);
	}

	/// <summary>
	/// Validated settings accepted by the Save button.
	/// </summary>
	public AppSettings Result { get; private set; }

	/// <summary>
	/// Populates editable controls without persisting changes.
	/// </summary>
	/// <param name="settings"> Values to display. </param>
	private void LoadFields(AppSettings settings)
	{
		RuntimeBox.Text = settings.RuntimeDirectory;
		FfmpegBox.Text = settings.FfmpegDirectory;
		ModelBox.Text = settings.ModelDirectory;
		ThreadsBox.Text = settings.Threads.ToString(CultureInfo.InvariantCulture);
		RetentionBox.Text = settings.ModelIdleRetentionMinutes.ToString(CultureInfo.InvariantCulture);
		AnalyzeOnImportBox.IsChecked = settings.AnalyzeOnImport;
		OpenExportBox.IsChecked = settings.OpenExportAfterSaving;
		RefreshReadiness();
	}

	/// <summary>
	/// Reads editable values and silently normalizes numeric preferences.
	/// </summary>
	/// <returns> Validated settings snapshot. </returns>
	private AppSettings ReadFields()
	{
		var result = new AppSettings
		{
			RuntimeDirectory = RuntimeBox.Text.Trim(),
			FfmpegDirectory = FfmpegBox.Text.Trim(),
			ModelDirectory = ModelBox.Text.Trim(),
			Threads = int.TryParse(ThreadsBox.Text, out var threads)
				? threads
				: AppSettings.DefaultThreads,
			ModelIdleRetentionMinutes = int.TryParse(RetentionBox.Text, out var retention)
				? retention
				: AppSettings.DefaultIdleRetentionMinutes,
			AnalyzeOnImport = AnalyzeOnImportBox.IsChecked == true,
			OpenExportAfterSaving = OpenExportBox.IsChecked == true
		};
		result.NormalizeValues();
		result.Validate();
		return result;
	}

	/// <summary>
	/// Resets every editable field, allowing cancellation before saving.
	/// </summary>
	/// <param name="sender"> Reset button. </param>
	/// <param name="eventArgs"> Click event. </param>
	private void OnResetClick(object sender, RoutedEventArgs eventArgs)
	{
		LoadFields(new AppSettings());
	}

	/// <summary>
	/// Validates and accepts the edited settings.
	/// </summary>
	/// <param name="sender"> Save button. </param>
	/// <param name="eventArgs"> Click event. </param>
	private void OnSaveClick(object sender, RoutedEventArgs eventArgs)
	{
		try
		{
			Result = ReadFields();
			DialogResult = true;
		}
		catch (ArgumentException exception)
		{
			MessageBox.Show(this, exception.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>
	/// Initializes the selected folders in a modal progress window.
	/// </summary>
	/// <param name="sender"> Initialization button. </param>
	/// <param name="eventArgs"> Click event. </param>
	private void OnInitializeClick(object sender, RoutedEventArgs eventArgs)
	{
		_ = InitializeRuntimeAsync();
	}

	/// <summary>
	/// Releases the loaded worker before initializing the selected environment.
	/// </summary>
	/// <returns> Completion after the setup window closes. </returns>
	private async Task InitializeRuntimeAsync()
	{
		if (!IsEnabled)
		{
			return;
		}

		try
		{
			var settings = ReadFields();
			IsEnabled = false;
			await _prepareSetup();
			IsEnabled = true;
			var dialog = new SetupWindow(settings) { Owner = this };
			dialog.ShowDialog();
			RefreshReadiness();
		}
		catch (Exception exception)
		{
			MessageBox.Show(this, exception.Message, "Runtime initialization", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		finally
		{
			IsEnabled = true;
		}
	}

	/// <summary>
	/// Rechecks the current paths without saving settings.
	/// </summary>
	/// <param name="sender"> Recheck button. </param>
	/// <param name="eventArgs"> Click event. </param>
	private void OnRecheckClick(object sender, RoutedEventArgs eventArgs)
	{
		RefreshReadiness();
	}

	/// <summary>
	/// Displays whether initialization has completed for the current folders.
	/// </summary>
	private void RefreshReadiness()
	{
		var isReady = false;
		try
		{
			isReady = RuntimeReadiness.IsReady(ReadFields());
			ReadinessText.Text = isReady
				? "Runtime initialized. Save to use these folders."
				: "Not ready. Initialize the runtime for these folders.";
		}
		catch (ArgumentException)
		{
			ReadinessText.Text = "Select valid absolute folders before initializing.";
		}

		UpdateInitializationAttention(isReady);
	}

	/// <summary>
	/// Rechecks setup attention when a runtime dependency path is edited.
	/// </summary>
	/// <param name="sender"> Edited path textbox. </param>
	/// <param name="eventArgs"> Text change event. </param>
	private void OnRuntimePathChanged(object sender, TextChangedEventArgs eventArgs)
	{
		if (IsInitialized)
		{
			RefreshReadiness();
		}
	}

	/// <summary>
	/// Pulses the initialization border only while the selected runtime is not ready.
	/// </summary>
	/// <param name="isReady"> Whether the selected runtime has completed setup. </param>
	private void UpdateInitializationAttention(bool isReady)
	{
		if (InitializeAttentionBorder.BorderBrush is SolidColorBrush previous && !previous.IsFrozen)
		{
			previous.BeginAnimation(SolidColorBrush.ColorProperty, null);
		}

		var brush = new SolidColorBrush(Colors.Transparent);
		InitializeAttentionBorder.BorderBrush = brush;
		if (!isReady)
		{
			var animation = new ColorAnimation(Colors.Transparent, Color.FromRgb(220, 38, 38), TimeSpan.FromSeconds(1))
			{
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever
			};
			brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
		}
	}

	/// <summary>
	/// Opens a folder picker for the selected runtime dependency.
	/// </summary>
	/// <param name="sender"> Browse button with its target tag. </param>
	/// <param name="eventArgs"> Click event. </param>
	private void OnBrowseClick(object sender, RoutedEventArgs eventArgs)
	{
		var target = ((Button)sender).Tag?.ToString() switch
		{
			"Runtime" => RuntimeBox,
			"Ffmpeg" => FfmpegBox,
			_ => ModelBox
		};
		var dialog = new OpenFolderDialog();
		if (dialog.ShowDialog(this) == true)
		{
			target.Text = dialog.FolderName;
			RefreshReadiness();
		}
	}
}

