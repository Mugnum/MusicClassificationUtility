using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Mugnum.MusicClassificationUtility.Core.Infrastructure;

/// <summary>
/// Runs local tools while observing their output and cancellation lifetimes.
/// </summary>
internal static class ProcessRunner
{
	/// <summary>
	/// Runs a redirected child process and releases cancellation before process disposal.
	/// </summary>
	/// <param name="start"> Validated executable and argument configuration. </param>
	/// <param name="progress"> Receives standard output and error lines. </param>
	/// <param name="cancellation"> Cancels the process tree. </param>
	/// <returns> Completion after process exit and redirected output have been observed. </returns>
	internal static async Task RunAsync(ProcessStartInfo start, IProgress<string> progress, CancellationToken cancellation)
	{
		ArgumentNullException.ThrowIfNull(start);
		ArgumentNullException.ThrowIfNull(progress);
		cancellation.ThrowIfCancellationRequested();
		using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
		await using var registration = cancellation.Register(static state => Stop((Process)state!), process);
		var output = ReadOutputAsync(process.StandardOutput, progress);
		var errors = ReadOutputAsync(process.StandardError, progress);
		try
		{
			await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
			await Task.WhenAll(output, errors).ConfigureAwait(false);
			cancellation.ThrowIfCancellationRequested();
			if (process.ExitCode != 0)
			{
				var errorText = await errors.ConfigureAwait(false);
				var details = string.IsNullOrWhiteSpace(errorText)
					? await output.ConfigureAwait(false)
					: errorText;
				throw new InvalidOperationException($"{Path.GetFileName(start.FileName)} failed (exit code {process.ExitCode}).\n{details.Trim()}");
			}
		}
		finally
		{
			Stop(process);
			await process.WaitForExitAsync().ConfigureAwait(false);
			await Task.WhenAll(output, errors).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Forwards one output stream while retaining a bounded diagnostic tail.
	/// </summary>
	/// <param name="reader"> Redirected output stream. </param>
	/// <param name="progress"> Receives each output line. </param>
	/// <returns> Recent output for an error message. </returns>
	private static async Task<string> ReadOutputAsync(StreamReader reader, IProgress<string> progress)
	{
		const int MaximumDiagnosticCharacters = 16000;
		var tail = new StringBuilder();
		while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
		{
			progress.Report(line);
			tail.AppendLine(line);
			if (tail.Length > MaximumDiagnosticCharacters)
			{
				tail.Remove(0, tail.Length - MaximumDiagnosticCharacters);
			}
		}

		return tail.ToString();
	}

	/// <summary>
	/// Stops a child process and descendants during cancellation or cleanup.
	/// </summary>
	/// <param name="process"> Process whose lifetime is owned by the runner. </param>
	private static void Stop(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
		}
		catch (Win32Exception) when (process.HasExited)
		{
		}
	}
}

