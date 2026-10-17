using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mugnum.MusicClassificationUtility.GUI.Diagnostics;

/// <summary>
/// Captures application-owned WPF content for offscreen visual validation.
/// </summary>
internal static class WindowArtifactRenderer
{
	/// <summary>
	/// Saves laid-out window content with its application background.
	/// </summary>
	/// <param name="window"> Application window to render. </param>
	/// <param name="path"> Destination PNG path. </param>
	public static void Save(Window window, string path)
	{
		const int Padding = 24;
		const int CombinedPadding = Padding * 2;
		const int Resolution = 96;

		ArgumentNullException.ThrowIfNull(window);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		window.UpdateLayout();
		var surface = window.Content as FrameworkElement
			?? throw new ArgumentException("The window has no renderable content.", nameof(window));

		if (surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
		{
			throw new InvalidOperationException("The window must be laid out before rendering.");
		}

		var drawing = new DrawingVisual();
		var bounds = new Rect(Padding, Padding, surface.ActualWidth, surface.ActualHeight);
		using (var context = drawing.RenderOpen())
		{
			context.DrawRectangle(window.Background, null, new Rect(0, 0, surface.ActualWidth + CombinedPadding, surface.ActualHeight + CombinedPadding));
			context.DrawRectangle(new VisualBrush(surface) { Stretch = Stretch.Fill }, null, bounds);
		}

		var bitmap = new RenderTargetBitmap((int)surface.ActualWidth + CombinedPadding,
			(int)surface.ActualHeight + CombinedPadding,
			Resolution, Resolution, PixelFormats.Pbgra32);

		bitmap.Render(drawing);
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using var file = File.Create(path);
		encoder.Save(file);
	}
}
