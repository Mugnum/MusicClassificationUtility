using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Export;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Models;
using Mugnum.MusicClassificationUtility.GUI.Diagnostics;

namespace Mugnum.MusicClassificationUtility.GUI.Windows;

/// <summary>
/// Coordinates track import, queue controls, selection details and report export.
/// </summary>
public partial class MainWindow
{
	/// <summary>
	/// Validates import automation, selection, classification, export and offscreen rendering.
	/// </summary>
	/// <returns> Operation completion. </returns>
	private async Task SelfTestAsync()
	{
		const int PreferredPreviewTrackIndex = 2;
		const string OutputOption = "--output";
		const string ValidationDirectoryName = "self-test";
		const string SamplesOption = "--samples";
		const string TextResultsFileName = "results.txt";
		const string JsonResultsFileName = "results.json";
		const string ScreenshotFileName = "screenshot.png";
		const string SuccessFileName = "passed.txt";
		const string FailureFileName = "failed.txt";

		var output = Option(OutputOption) ?? Path.Combine(AppContext.BaseDirectory, ValidationDirectoryName);
		Directory.CreateDirectory(output);

		try
		{
			var samples = Directory.GetFiles(Option(SamplesOption)
				?? throw new ArgumentException($"{SamplesOption} --samples is required."));

			_settings.AnalyzeOnImport = true;
			await ImportAsync(samples);

			if (!_queue.IsRunning && _tracks.Any(track => track.Status != TrackStatus.Done))
			{
				throw new InvalidOperationException("Automatic import analysis did not start.");
			}

			await ImportAsync(samples.Select(x => x.ToUpperInvariant()));

			if (_tracks.Count != samples.Length)
			{
				throw new InvalidOperationException("Import did not deduplicate paths.");
			}

			await _queue.WhenIdle;

			if (_tracks.Any(t => t.Status != TrackStatus.Done))
			{
				throw new InvalidOperationException(_lastFailure ?? "Some tracks did not complete.");
			}

			TracksGrid.SelectedIndex = Math.Min(PreferredPreviewTrackIndex, _tracks.Count - 1);
			RefreshDetails();
			await File.WriteAllTextAsync(Path.Combine(output, TextResultsFileName),
				ResultExporter.Create(_tracks));

			await File.WriteAllTextAsync(Path.Combine(output, JsonResultsFileName),
				JsonSerializer.Serialize(_tracks.Select(t => new { t.FullPath, t.DisplayName, t.StatusText, t.Progress, t.Result }),
					JsonDefaults.Options));

			await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
			UpdateLayout();

			WindowArtifactRenderer.Save(this, Path.Combine(output, ScreenshotFileName));
			ValidateSettingsDialog(output);
			await File.WriteAllTextAsync(Path.Combine(output, SuccessFileName),
				$"Passed: {_tracks.Count} real songs, automatic queuing, duplicate imports, export summary, settings reset and WPF rendering.");

			Environment.ExitCode = 0;
		}
		catch (Exception exc)
		{
			await File.WriteAllTextAsync(Path.Combine(output, FailureFileName), exc.ToString());
			Environment.ExitCode = 1;
		}

		Close();
	}

	/// <summary>
	/// Verifies settings controls and reset behavior without writing user preferences.
	/// </summary>
	/// <param name="output"> Validation artifact folder. </param>
	private void ValidateSettingsDialog(string output)
	{
		const int OffscreenCoordinate = -30000;
		const int CustomRetentionMinutes = 60;
		const string SettingsScreenshotFileName = "settings.png";
		const string ResetButtonText = "ResetButton";
		const string AnalyzeOnImportBoxText = "AnalyzeOnImportBox";
		const string OpenExportBoxText = "OpenExportBox";
		const string RetentionBoxText = "RetentionBox";
		const string SaveButtonText = "SaveButton";

		var preferences = _settings.Copy();
		preferences.AnalyzeOnImport = true;
		preferences.OpenExportAfterSaving = false;
		preferences.ModelIdleRetentionMinutes = CustomRetentionMinutes;
		var dialog = new SettingsWindow(preferences)
		{
			Owner = this,
			WindowStartupLocation = WindowStartupLocation.Manual,
			Left = OffscreenCoordinate,
			Top = OffscreenCoordinate,
			ShowInTaskbar = false
		};

		Exception failure = null;
		dialog.Loaded += (_, _) =>
		{
			try
			{
				WindowArtifactRenderer.Save(dialog, Path.Combine(output, SettingsScreenshotFileName));
				((Button)dialog.FindName(ResetButtonText))?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
				var defaultSettings = new AppSettings();

				if (((CheckBox)dialog.FindName(AnalyzeOnImportBoxText))?.IsChecked != defaultSettings.AnalyzeOnImport
					|| ((CheckBox)dialog.FindName(OpenExportBoxText))?.IsChecked != defaultSettings.OpenExportAfterSaving
					|| ((TextBox)dialog.FindName(RetentionBoxText))?.Text != defaultSettings.ModelIdleRetentionMinutes.ToString())
				{
					throw new InvalidOperationException("Reset did not restore default settings controls.");
				}

				((Button)dialog.FindName(SaveButtonText))?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			}
			catch (Exception exception)
			{
				failure = exception;
				dialog.Close();
			}
		};

		if (dialog.ShowDialog() != true || failure != null)
		{
			throw new InvalidOperationException("Settings dialog validation failed.", failure);
		}

		dialog.Result.Validate();

		if (!dialog.Result.OpenExportAfterSaving
			|| dialog.Result.AnalyzeOnImport
			|| dialog.Result.ModelIdleRetentionMinutes != AppSettings.DefaultIdleRetentionMinutes)
		{
			throw new InvalidOperationException("Settings reset did not save the default preferences.");
		}
	}
}
