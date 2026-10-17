using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Diagnostics;
using Mugnum.MusicClassificationUtility.Core.Analysis;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Export;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Media;
using Mugnum.MusicClassificationUtility.Core.Models;
using Mugnum.MusicClassificationUtility.Core.Runtime;

namespace Mugnum.MusicClassificationUtility.GUI.Windows;

/// <summary>
/// Coordinates track import, queue controls, selection details and report export.
/// </summary>
public partial class MainWindow : Window
{
	/// <summary>
	/// Maximum concurrent FFprobe metadata readers.
	/// </summary>
	private const int MaximumMetadataReaders = 4;

	/// <summary>
	/// Imported tracks displayed by the grid.
	/// </summary>
	private readonly ObservableCollection<Track> _tracks = [];

	/// <summary>
	/// Case-insensitive absolute paths already imported.
	/// </summary>
	private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Bounds concurrent metadata reader processes.
	/// </summary>
	private readonly SemaphoreSlim _metadataSlots = new(MaximumMetadataReaders);

	/// <summary>
	/// Cancels metadata work when clearing or closing.
	/// </summary>
	private CancellationTokenSource _metadataCancellation = new();

	/// <summary>
	/// Command-line overrides for this application instance.
	/// </summary>
	private readonly string[] _arguments;

	/// <summary>
	/// Whether offscreen integration validation is active.
	/// </summary>
	private readonly bool _selfTest;

	/// <summary>
	/// Current validated runtime configuration and preferences.
	/// </summary>
	private AppSettings _settings = new();

	/// <summary>
	/// Restartable analysis backend.
	/// </summary>
	private RetainedAnalysisBackend _backend = null!;

	/// <summary>
	/// Sequential track analysis coordinator.
	/// </summary>
	private AnalysisQueue _queue = null!;

	/// <summary>
	/// Whether asynchronous window shutdown has started.
	/// </summary>
	private bool _closing;

	/// <summary>
	/// Whether child processes have stopped and closing can finish.
	/// </summary>
	private bool _allowClose;

	/// <summary>
	/// Whether the track list is being cleared.
	/// </summary>
	private bool _clearing;

	/// <summary>
	/// Last error that stopped queued analysis.
	/// </summary>
	private string? _lastFailure;

	/// <summary>
	/// Coordinates track import, queue controls, selection details and report export.
	/// </summary>
	/// <param name="args"> Command-line arguments. </param>
	public MainWindow(string[] args)
	{
		const int OffscreenCoordinate = -30000;
		const string SelfTestOption = "--self-test";
		const string RuntimeOption = "--runtime";
		const string FfmpegOption = "--ffmpeg";
		const string ModelsOption = "--models";
		ArgumentNullException.ThrowIfNull(args);
		_arguments = args;
		_selfTest = args.Contains(SelfTestOption);
		InitializeComponent();
		try
		{
			_settings = AppSettings.Load();
		}
		catch (Exception exc)
		{
			_settings = new AppSettings();
			StatusText.Text = $"Settings could not be read: {exc.Message}";
		}

#if DEBUG
		ApplyDevelopmentRuntimeOverrides();
#endif
		if (Option(RuntimeOption) is { } runtime)
		{
			_settings.RuntimeDirectory = runtime;
		}

		if (Option(FfmpegOption) is { } ffmpeg)
		{
			_settings.FfmpegDirectory = ffmpeg;
		}

		if (Option(ModelsOption) is { } models)
		{
			_settings.ModelDirectory = models;
		}

		_settings.Validate();
		CreateBackend();
		TracksGrid.ItemsSource = _tracks;
		if (_selfTest)
		{
			WindowStartupLocation = WindowStartupLocation.Manual;
			Left = OffscreenCoordinate;
			Top = OffscreenCoordinate;
			ShowInTaskbar = false;
			Loaded += OnSelfTestLoaded;
		}
		else
		{
			Loaded += OnApplicationLoaded;
		}

		CheckReadiness();
	}

	/// <summary>
	/// Resolves a command-line option without persisting overrides.
	/// </summary>
	/// <param name="name"> Requested command-line option name. </param>
	/// <returns> Option value when present. </returns>
	private string? Option(string name)
	{
		var index = Array.IndexOf(_arguments, name);
		return index >= 0 && index + 1 < _arguments.Length
			? _arguments[index + 1]
			: null;
	}

	/// <summary>
	/// Creates a restartable worker with the configured idle retention.
	/// </summary>
	private void CreateBackend()
	{
		const string PythonDirectoryName = "python";
		const string AnalysisStoppedText = "Analysis stopped. ";
		const string AnalysisStoppedText2 = "Analysis stopped";
		_backend = new RetainedAnalysisBackend(new BackendClient(_settings.Copy(), Path.Combine(AppContext.BaseDirectory, PythonDirectoryName)), _settings.IdleRetention);
		_queue = new AnalysisQueue(_backend);
		_queue.PropertyChanged += (_, _) => UpdateControls();
		_queue.Updated += () =>
		{
			UpdateControls();
			RefreshDetails();
		};
		_queue.Failed += message =>
		{
			_lastFailure = message;
			StatusText.Text = AnalysisStoppedText + message.Split('\n')[0];
			if (!_selfTest)
			{
				MessageBox.Show(this, message, AnalysisStoppedText2, MessageBoxButton.OK, MessageBoxImage.Error);
			}
		};
	}

	/// <summary>
	/// Imports unique file paths, resolves metadata once and optionally queues analysis.
	/// </summary>
	/// <param name="paths"> Local paths to import. </param>
	/// <returns> Operation completion. </returns>
	private async Task ImportAsync(IEnumerable<string> paths)
	{
		const string MetadataUnavailableText = "Metadata unavailable";
		var added = new List<Track>();
		foreach (var supplied in paths)
		{
			var path = string.Empty;
			try
			{
				path = Path.GetFullPath(supplied);
			}
			catch (Exception exc) when (exc is ArgumentException or NotSupportedException or PathTooLongException)
			{
				continue;
			}

			if (Directory.Exists(path) || !_imported.Add(path))
			{
				continue;
			}

			var track = new Track(path);
			track.PropertyChanged += OnTrackChanged;
			_tracks.Add(track);
			added.Add(track);
		}

		if (TracksGrid.SelectedItem == null && _tracks.Count > 0)
		{
			TracksGrid.SelectedIndex = 0;
		}

		UpdateControls();
		if (IsRuntimeReady && _settings.AnalyzeOnImport && added.Count > 0)
		{
			_lastFailure = null;
			_queue.Enqueue(added);
			UpdateControls();
		}

		var token = _metadataCancellation.Token;
		await Task.WhenAll(added.Select(async track =>
		{
			var acquired = false;
			try
			{
				await _metadataSlots.WaitAsync(token);
				acquired = true;
				var value = await MetadataReader.ReadAsync(track.FullPath, _settings.Copy(), token);
				if (token.IsCancellationRequested)
				{
					return;
				}

				track.DisplayName = value.Name;
				track.Description = value.Description;
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception)
			{
				track.Description = MetadataUnavailableText;
			}
			finally
			{
				if (acquired)
				{
					_metadataSlots.Release();
				}
			}
		}));
		RefreshDetails();
	}

	/// <summary>
	/// Refreshes details when the selected track changes.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnTrackChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (ReferenceEquals(sender, TracksGrid.SelectedItem) && eventArgs.PropertyName is nameof(Track.Status) or nameof(Track.Stage) or nameof(Track.Result) or nameof(Track.Error) or nameof(Track.DisplayName))
		{
			RefreshDetails();
		}
	}

	/// <summary>
	/// Imports multiple files selected by the user.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnImportClickAsync(object sender, RoutedEventArgs eventArgs)
	{
		const string ImportSongsText = "Import songs";
		const string AudioFilesMp3FlacWavM4aAacOggOpusText = "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.ape;*.wv;*.alac;*.mp4;*.mka|All files|*.*";
		var dialog = new OpenFileDialog
		{
			Multiselect = true,
			Title = ImportSongsText,
			Filter = AudioFilesMp3FlacWavM4aAacOggOpusText
		};
		if (dialog.ShowDialog(this) == true)
		{
			await ImportAsync(dialog.FileNames);
		}
	}

	/// <summary>
	/// Accepts standard Windows file-drop data.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnTracksDragOver(object sender, DragEventArgs eventArgs)
	{
		eventArgs.Effects = eventArgs.Data.GetDataPresent(DataFormats.FileDrop)
			? DragDropEffects.Copy
			: DragDropEffects.None;
		eventArgs.Handled = true;
	}

	/// <summary>
	/// Imports paths supplied by a Windows file drop.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnTracksDropAsync(object sender, DragEventArgs eventArgs)
	{
		if (eventArgs.Data.GetData(DataFormats.FileDrop) is string[] files)
		{
			await ImportAsync(files);
		}

		eventArgs.Handled = true;
	}

	/// <summary>
	/// Queues analysis of the selected row's track.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnAnalyzeClick(object sender, RoutedEventArgs eventArgs)
	{
		if (_clearing || _closing || !IsRuntimeReady)
		{
			return;
		}

		if ((sender as FrameworkElement)?.DataContext is Track track && track.CanAnalyze)
		{
			_lastFailure = null;
			_queue.Enqueue([track]);
			UpdateControls();
		}
	}

	/// <summary>
	/// Queues all unfinished tracks.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnRunClick(object sender, RoutedEventArgs eventArgs)
	{
		if (!IsRuntimeReady)
		{
			return;
		}

		_lastFailure = null;
		_queue.Enqueue(_tracks.Where(track => track.Status is TrackStatus.NotAnalyzed or TrackStatus.Cancelled or TrackStatus.Error).ToList());
		UpdateControls();
	}

	/// <summary>
	/// Cancels active and pending analysis requests.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnCancelClickAsync(object sender, RoutedEventArgs eventArgs)
	{
		const string CancelledRemainingTracksCanBeQueuedAgainText = "Cancelled. Remaining tracks can be queued again.";
		CancelButton.IsEnabled = false;
		await _queue.CancelAsync();
		StatusText.Text = CancelledRemainingTracksCanBeQueuedAgainText;
		UpdateControls();
	}

	/// <summary>
	/// Cancels processing and clears imported tracks.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnClearClickAsync(object sender, RoutedEventArgs eventArgs)
	{
		const string ReadyText = "Ready";
		_clearing = true;
		UpdateControls();
		await _metadataCancellation.CancelAsync();
		await _queue.CancelAsync();
		foreach (var track in _tracks)
		{
			track.PropertyChanged -= OnTrackChanged;
		}

		_tracks.Clear();
		_imported.Clear();
		_metadataCancellation.Dispose();
		_metadataCancellation = new CancellationTokenSource();
		_clearing = false;
		_lastFailure = null;
		StatusText.Text = ReadyText;
		UpdateControls();
		RefreshDetails();
	}

	/// <summary>
	/// Saves a report and optionally opens its associated editor.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnExportClickAsync(object sender, RoutedEventArgs eventArgs)
	{
		const string ReportFilter = "Text report|*.txt";
		const string DefaultReportName = "Music classification results.txt";
		const string ReportExtension = ".txt";
		const string ExportErrorTitle = "Export failed";
		const string EditorErrorTitle = "Report saved";
		const string ExportedText = "Exported ";
		var dialog = new SaveFileDialog
		{
			Filter = ReportFilter,
			FileName = DefaultReportName,
			DefaultExt = ReportExtension
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}

		try
		{
			await File.WriteAllTextAsync(dialog.FileName, ResultExporter.Create(_tracks), new UTF8Encoding(false));
			StatusText.Text = ExportedText + dialog.FileName;
		}
		catch (Exception exception)
		{
			MessageBox.Show(this, exception.Message, ExportErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}

		if (!_settings.OpenExportAfterSaving)
		{
			return;
		}

		try
		{
			Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
		}
		catch (Exception exception)
		{
			MessageBox.Show(this, $"The report was saved, but its default editor could not be opened.\n{exception.Message}", EditorErrorTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>
	/// Persists accepted preferences and recreates the worker lifecycle.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnSettingsClickAsync(object sender, RoutedEventArgs eventArgs)
	{
		const string SettingsSavedText = "Settings saved.";
		const string SettingsCouldNotBeSavedText = "Settings could not be saved";
		var dialog = new SettingsWindow(_settings, _backend.StopAsync)
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true)
		{
			CheckReadiness();
			return;
		}

		try
		{
			dialog.Result.Save();
			await _backend.DisposeAsync();
			_settings = dialog.Result;
			CreateBackend();
			CheckReadiness();
			if (IsRuntimeReady)
			{
				StatusText.Text = SettingsSavedText;
			}
		}
		catch (Exception exc)
		{
			MessageBox.Show(this, exc.Message, SettingsCouldNotBeSavedText);
		}
	}

	/// <summary>
	/// Displays details for the newly selected track.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs) => RefreshDetails();

	/// <summary>
	/// Displays scores, exclusions and retained windows for the current selection.
	/// </summary>
	private void RefreshDetails()
	{
		const string SelectATrackText = "Select a track";
		const string LineSeparator = "\n";
		if (TracksGrid.SelectedItem is not Track track)
		{
			DetailTitle.Text = SelectATrackText;
			DetailPath.Text = string.Empty;
			DetailStatus.Text = string.Empty;
			ResultPanel.Visibility = Visibility.Collapsed;
			return;
		}

		DetailTitle.Text = track.DisplayName;
		DetailPath.Text = track.FullPath;
		DetailStatus.Text = string.IsNullOrEmpty(track.Error)
			? track.StatusText
			: track.Error;
		if (track.Result is not { } result)
		{
			ResultPanel.Visibility = Visibility.Collapsed;
			return;
		}

		ResultPanel.Visibility = Visibility.Visible;
		DetailStatus.Text += $" · {TimeSpan.FromSeconds(result.DurationSeconds):mm\\:ss} audio\n{result.Segments.Count} windows · {result.AnalyzedDurationSeconds:0.0}s scored · {result.AnalysisSeconds:0.0}s analysis";
		OverallList.ItemsSource = result.Overall;
		WindowsList.ItemsSource = result.Segments;
		WarningsText.Text = string.Join(LineSeparator, result.Warnings);
	}

	/// <summary>
	/// Synchronizes queue buttons, track counts and status text.
	/// </summary>
	private void UpdateControls()
	{
		const string ProcessingOneSongAtATimeYouCanKeepText = "Processing one song at a time. You can keep importing or queue individual tracks.";
		const string AnalysisStoppedText = "Analysis stopped. ";
		const string AnalysisCompleteSelectATrackToExploreItsResultsText = "Analysis complete. Select a track to explore its results.";
		CountText.Text = $"{_tracks.Count(t => t.Status == TrackStatus.Done)}/{_tracks.Count} analyzed";
		EmptyText.Visibility = _tracks.Count == 0
			? Visibility.Visible
			: Visibility.Collapsed;
		SettingsButton.IsEnabled = !_queue.IsRunning && !_clearing;
		CancelButton.IsEnabled = _queue.IsRunning && !_clearing;
		ClearButton.IsEnabled = _tracks.Count > 0 && !_clearing;
		ExportButton.IsEnabled = _tracks.Count > 0 && !_clearing;
		RunButton.IsEnabled = IsRuntimeReady && !_clearing && _tracks.Any(t => t.Status is TrackStatus.NotAnalyzed or TrackStatus.Cancelled or TrackStatus.Error);
		if (_queue.IsRunning)
		{
			StatusText.Text = ProcessingOneSongAtATimeYouCanKeepText;
		}
		else if (_lastFailure != null)
		{
			StatusText.Text = AnalysisStoppedText + _lastFailure.Split('\n')[0];
		}
		else if (_tracks.Count > 0 && _tracks.All(t => t.Status == TrackStatus.Done))
		{
			StatusText.Text = AnalysisCompleteSelectATrackToExploreItsResultsText;
		}
	}

	/// <summary>
	/// Stops child processes before scheduling the final window close.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	/// <returns> Operation completion. </returns>
	private async Task OnWindowClosingAsync(object? sender, CancelEventArgs eventArgs)
	{
		if (_allowClose)
		{
			return;
		}

		eventArgs.Cancel = true;
		if (_closing)
		{
			return;
		}

		_closing = true;
		IsEnabled = false;
		_metadataCancellation.Cancel();
		await _queue.CancelAsync();
		await _backend.DisposeAsync();
		_allowClose = true;
		_ = Dispatcher.BeginInvoke(new Action(Close));
	}

	/// <summary>
	/// Applies development runtime paths while preserving saved user preferences.
	/// </summary>
	private void ApplyDevelopmentRuntimeOverrides()
	{
		const string DevelopmentOverridesFileName = "runtime.local.json";
		var local = Path.Combine(AppContext.BaseDirectory, DevelopmentOverridesFileName);
		if (File.Exists(local))
		{
			var overrides = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(local), JsonDefaults.Options) ?? new AppSettings();
			_settings.RuntimeDirectory = overrides.RuntimeDirectory;
			_settings.FfmpegDirectory = overrides.FfmpegDirectory;
			_settings.ModelDirectory = overrides.ModelDirectory;
		}
	}
}

