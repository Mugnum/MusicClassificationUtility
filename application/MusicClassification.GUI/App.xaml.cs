using System.Windows;
using Mugnum.MusicClassificationUtility.GUI.Windows;

namespace Mugnum.MusicClassificationUtility.GUI;

/// <summary>
/// Starts the Windows desktop application.
/// </summary>
public partial class App : Application
{
	/// <summary>
	/// Creates and shows the main application window.
	/// </summary>
	/// <param name="e"> Framework event data. </param>
	protected override void OnStartup(StartupEventArgs e)
	{
		ArgumentNullException.ThrowIfNull(e);
		base.OnStartup(e);
		var window = new MainWindow(e.Args);
		MainWindow = window;
		window.Show();
	}
}
