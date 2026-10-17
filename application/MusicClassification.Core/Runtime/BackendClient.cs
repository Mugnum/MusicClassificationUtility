using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mugnum.MusicClassificationUtility.Core.Analysis;
using Mugnum.MusicClassificationUtility.Core.Configuration;
using Mugnum.MusicClassificationUtility.Core.Infrastructure;
using Mugnum.MusicClassificationUtility.Core.Models;

namespace Mugnum.MusicClassificationUtility.Core.Runtime;

/// <summary>
/// Transports versioned JSON requests to a persistent local Python worker.
/// </summary>
public sealed class BackendClient : IAnalysisBackend
{
	/// <summary>
	/// Current validated runtime configuration and preferences.
	/// </summary>
	private readonly AppSettings _settings;

	/// <summary>
	/// Application-owned Python adapter directory.
	/// </summary>
	private readonly string _pythonDirectory;

	/// <summary>
	/// Whether the backend has been disposed.
	/// </summary>
	private bool _disposed;

	/// <summary>
	/// Transports versioned JSON requests to a persistent local Python worker.
	/// </summary>
	/// <param name="settings"> Validated runtime configuration. </param>
	/// <param name="pythonDirectory"> Application-owned Python adapter folder. </param>
	public BackendClient(AppSettings settings, string pythonDirectory)
	{
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentException.ThrowIfNullOrWhiteSpace(pythonDirectory);
		_settings = settings.Copy();
		_pythonDirectory = Path.GetFullPath(pythonDirectory);
	}

	/// <summary>
	/// Current Python worker process.
	/// </summary>
	private Process _process;

	/// <summary>
	/// Asynchronous worker diagnostic reader.
	/// </summary>
	private Task _stderrTask;

	/// <summary>
	/// Bounded recent worker diagnostics.
	/// </summary>
	private readonly Queue<string> _diagnostics = new();

	/// <summary>
	/// Serializes worker lifecycle transitions.
	/// </summary>
	private readonly SemaphoreSlim _gate = new(1);

	/// <summary>
	/// Analyzes a local input while reporting completed audio progress.
	/// </summary>
	/// <param name="path"> Local input path. </param>
	/// <param name="progress"> Progress receiver. </param>
	/// <param name="cancellation"> Operation cancellation token. </param>
	/// <returns> Complete classification result. </returns>
	public async Task<AnalysisResult> AnalyzeAsync(string path,
		IProgress<BackendProgress> progress, CancellationToken cancellation)
	{
		const string RequestIdFormat = "N";
		const string RequestIdProperty = "id";
		const string MessageTypeProperty = "type";
		const string MetadataMessageType = "metadata";
		const string DecodingAudioText = "Decoding audio";
		const string ProgressMessageType = "progress";
		const string PercentProperty = "percent";
		const string MessageProperty = "message";
		const string AnalyzingText = "Analyzing";
		const string WindowCountProperty = "windows_completed";
		const string ResultMessageType = "result";
		const string ErrorMessageType = "error";

		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(progress);
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellation.ThrowIfCancellationRequested();
		await _gate.WaitAsync(cancellation);

		try
		{
			await using var registration = cancellation.Register(KillProcess);
			await EnsureReadyAsync(progress, cancellation);
			var active = _process!;
			var id = Guid.NewGuid().ToString(RequestIdFormat);
			var request = JsonSerializer.Serialize(new AnalysisRequest(id, path), JsonDefaults.ProtocolOptions);
			await active.StandardInput.WriteLineAsync(request.AsMemory(), cancellation);
			await active.StandardInput.FlushAsync(cancellation);

			while (true)
			{
				using var message = await ReadMessageAsync(active, cancellation);
				var root = message.RootElement;

				if (!root.TryGetProperty(RequestIdProperty, out var responseId) || responseId.GetString() != id)
				{
					throw new InvalidDataException("The classifier returned a response for an unexpected request.");
				}

				switch (root.GetProperty(MessageTypeProperty).GetString())
				{
					case MetadataMessageType:
						progress.Report(new BackendProgress(null, DecodingAudioText));
						break;

					case ProgressMessageType:
						var percent = root.TryGetProperty(PercentProperty, out var p) && p.ValueKind == JsonValueKind.Number
							? p.GetDouble()
							: (double?)null;

						var stage = root.TryGetProperty(MessageProperty, out var stageMessage)
							? stageMessage.GetString() ?? AnalyzingText
							: $"Analyzing · window {root.GetProperty(WindowCountProperty).GetInt32()}";

						progress.Report(new BackendProgress(percent, stage));
						break;

					case ResultMessageType:
						cancellation.ThrowIfCancellationRequested();
						return root.GetProperty(ResultMessageType).Deserialize<AnalysisResult>(JsonDefaults.Options)
							?? throw new InvalidDataException("The classifier returned an empty result.");

					case ErrorMessageType:
						throw new InvalidOperationException(root.GetProperty(MessageProperty).GetString());

					default:
						throw new InvalidDataException("Unknown classifier protocol message.");
				}
			}
		}
		catch (Exception) when (cancellation.IsCancellationRequested)
		{
			await ResetAsync();
			throw new OperationCanceledException(cancellation);
		}
		catch
		{
			await ResetAsync();
			throw;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Starts the Python worker and validates its protocol handshake.
	/// </summary>
	/// <param name="progress"> Progress receiver. </param>
	/// <param name="cancellation"> Operation cancellation token. </param>
	/// <returns> Operation completion. </returns>
	private async Task EnsureReadyAsync(IProgress<BackendProgress> progress, CancellationToken cancellation)
	{
		const int StartupTimeoutMinutes = 3;
		const int SupportedProtocolVersion = 1;
		const string UnbufferedOption = "-u";
		const string WorkerCommand = "worker";
		const string ThreadsOption = "--threads";
		const string FfmpegOption = "--ffmpeg";
		const string DecoderToolName = "ffmpeg";
		const string FfprobeOption = "--ffprobe";
		const string ProbeToolName = "ffprobe";
		const string RuntimeEnvironmentVariable = "MCU_RUNTIME_DIR";
		const string ModelsEnvironmentVariable = "MCU_MODEL_DIR";
		const string PythonEncodingVariable = "PYTHONIOENCODING";
		const string Utf8EncodingName = "utf-8";
		const string OfflineEnvironmentVariable = "HF_HUB_OFFLINE";
		const string EnabledEnvironmentValue = "1";
		const string DisableBytecodeVariable = "PYTHONDONTWRITEBYTECODE";
		const string MessageTypeProperty = "type";
		const string StageMessageType = "stage";
		const string MessageProperty = "message";
		const string LoadingModelText = "Loading model";
		const string ReadyMessageType = "ready";
		const string ProtocolVersionProperty = "protocol_version";

		if (_process is { HasExited: false })
		{
			return;
		}

		await ResetAsync();

		if (!File.Exists(_settings.PythonExecutable))
		{
			throw new FileNotFoundException("Python runtime is missing. Initialize it or choose an existing runtime in Settings.",
				_settings.PythonExecutable);
		}

		var runner = Path.Combine(_pythonDirectory, "run_analysis.py");

		if (!File.Exists(runner))
		{
			throw new FileNotFoundException("The application's python folder is missing.", runner);
		}

		var start = new ProcessStartInfo(_settings.PythonExecutable)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = new UTF8Encoding(false),
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		var startupArgs = new[]
		{
			UnbufferedOption,
			runner,
			WorkerCommand,
			ThreadsOption,
			_settings.Threads.ToString(),
			FfmpegOption,
			_settings.FindTool(DecoderToolName),
			FfprobeOption,
			_settings.FindTool(ProbeToolName)
		};

		foreach (var arg in startupArgs)
		{
			start.ArgumentList.Add(arg);
		}

		start.Environment[RuntimeEnvironmentVariable] = _settings.RuntimeDirectory;
		start.Environment[ModelsEnvironmentVariable] = _settings.Models;
		start.Environment[PythonEncodingVariable] = Utf8EncodingName;
		start.Environment[OfflineEnvironmentVariable] = EnabledEnvironmentValue;
		start.Environment[DisableBytecodeVariable] = EnabledEnvironmentValue;
		lock (_diagnostics)
		{
			_diagnostics.Clear();
		}

		_process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python.");
		var active = _process;

		_stderrTask = ReadDiagnosticsAsync(active);

		using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		startup.CancelAfter(TimeSpan.FromMinutes(StartupTimeoutMinutes));

		try
		{
			while (true)
			{
				using var message = await ReadMessageAsync(active, startup.Token);
				var root = message.RootElement;

				if (root.GetProperty(MessageTypeProperty).GetString() == StageMessageType)
				{
					progress.Report(new BackendProgress(null, root.GetProperty(MessageProperty).GetString() ?? LoadingModelText));
				}
				else if (root.GetProperty(MessageTypeProperty).GetString() == ReadyMessageType)
				{
					if (root.GetProperty(ProtocolVersionProperty).GetInt32() != SupportedProtocolVersion)
					{
						throw new InvalidDataException("Unsupported classifier protocol version.");
					}

					return;
				}
				else
				{
					throw new InvalidDataException("The classifier did not send its ready message.");
				}
			}
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
		{
			throw new TimeoutException("Python did not finish loading within timeout window.");
		}
	}

	/// <summary>
	/// Reads worker diagnostics until process exit, independently of individual request cancellation.
	/// </summary>
	/// <param name="active"> Worker whose output remains valid until cleanup awaits this task. </param>
	/// <returns> Completion after the worker closes its diagnostic stream. </returns>
	private async Task ReadDiagnosticsAsync(Process active)
	{
		const int MaximumDiagnosticLines = 40;
		while (await active.StandardError.ReadLineAsync() is { } line)
		{
			lock (_diagnostics)
			{
				_diagnostics.Enqueue(line);
				while (_diagnostics.Count > MaximumDiagnosticLines)
				{
					_diagnostics.Dequeue();
				}
			}
		}
	}

	/// <summary>
	/// Reads one protocol message or reports worker termination diagnostics.
	/// </summary>
	/// <param name="active"> Active worker process. </param>
	/// <param name="cancellation"> Operation cancellation token. </param>
	/// <returns> Parsed protocol message. </returns>
	private async Task<JsonDocument> ReadMessageAsync(Process active, CancellationToken cancellation)
	{
		var line = await active.StandardOutput.ReadLineAsync(cancellation);

		if (line != null)
		{
			return JsonDocument.Parse(line);
		}

		await active.WaitForExitAsync(cancellation);

		if (_stderrTask != null)
		{
			await _stderrTask.WaitAsync(cancellation);
		}

		string details;
		lock (_diagnostics)
		{
			details = string.Join(Environment.NewLine, _diagnostics);
		}

		throw new InvalidOperationException($"Python stopped (exit code {active.ExitCode}).\n{details}");
	}

	/// <summary>
	/// Terminates the worker process tree during cancellation or shutdown.
	/// </summary>
	private void KillProcess()
	{
		try
		{
			if (_process is { HasExited: false } active)
			{
				active.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
		}
	}

	/// <summary>
	/// Terminates the current worker and releases redirected streams.
	/// </summary>
	/// <returns> Operation completion. </returns>
	private async Task ResetAsync()
	{
		const int ShutdownTimeoutSeconds = 10;
		KillProcess();

		if (_process != null)
		{
			try
			{
				await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
			}
			catch (InvalidOperationException)
			{
			}

			if (_stderrTask != null)
			{
				try
				{
					await _stderrTask;
				}
				catch (IOException)
				{
				}
			}

			_process.Dispose();
			_process = null;
			_stderrTask = null;
		}
	}

	/// <summary>
	/// Stops the worker and releases its model resources.
	/// </summary>
	/// <returns> Operation completion. </returns>
	public async Task StopAsync()
	{
		KillProcess();
		await _gate.WaitAsync();

		try
		{
			await ResetAsync();
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// Releases the worker and pending lifecycle operations.
	/// </summary>
	/// <returns> Operation completion. </returns>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		await StopAsync();
		_disposed = true;
	}
}
