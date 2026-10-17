using Mugnum.MusicClassificationUtility.Core.Runtime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mugnum.MusicClassificationUtility.GUI.Windows;

/// <summary>
/// Coordinates runtime readiness and first-launch setup guidance.
/// </summary>
public partial class MainWindow
{
	/// <summary>
	/// Dependency property used to disable analysis until initialization completes.
	/// </summary>
	public static readonly DependencyProperty IsRuntimeReadyProperty = DependencyProperty.Register(
		nameof(IsRuntimeReady), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

	/// <summary>
	/// Whether the selected runtime has a successful setup record.
	/// </summary>
	public bool IsRuntimeReady
	{
		get => (bool)GetValue(IsRuntimeReadyProperty);
		private set => SetValue(IsRuntimeReadyProperty, value);
	}

	/// <summary>
	/// Opens settings on launch when initialization has not completed.
	/// </summary>
	/// <param name="sender"> Framework event source. </param>
	/// <param name="eventArgs"> Framework event data. </param>
	private void OnApplicationLoaded(object sender, RoutedEventArgs eventArgs)
	{
		Loaded -= OnApplicationLoaded;
		if (!IsRuntimeReady)
		{
			_ = RunUiActionAsync(() => OnSettingsClickAsync(sender, eventArgs));
		}
	}

	/// <summary>
	/// Rechecks the completion file and updates the setup attention indicator.
	/// </summary>
	private void CheckReadiness()
	{
		IsRuntimeReady = _selfTest || RuntimeReadiness.IsReady(_settings);
		ReadinessPanel.Visibility = IsRuntimeReady
			? Visibility.Collapsed
			: Visibility.Visible;
		var brush = new SolidColorBrush(Colors.Transparent);
		SettingsAttentionBorder.BorderBrush = brush;
		if (!IsRuntimeReady)
		{
			var animation = new ColorAnimation(Colors.Transparent, Color.FromRgb(220, 38, 38), TimeSpan.FromSeconds(1))
			{
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever
			};
			brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
			StatusText.Text = "Runtime is not ready. Open Settings to initialize it.";
		}

		UpdateControls();
	}
}

