using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Se7enSource;

/// <summary>« Identifier » : un grand nom au centre de chaque écran pendant 3 secondes (comme dans Windows).</summary>
public sealed class IdentifyWindow : Window
{
    private readonly (int Left, int Top, int Right, int Bottom) _bounds;

    public IdentifyWindow((int Left, int Top, int Right, int Bottom) bounds, string title, string detail)
    {
        _bounds = bounds;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontFamily = (FontFamily)Application.Current.FindResource("FontDisplay"), FontSize = 54,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail, FontFamily = (FontFamily)Application.Current.FindResource("FontSans"), FontSize = 24,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xAB, 0xB4)), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        });
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0F, 0x0F, 0x12)), CornerRadius = new CornerRadius(28),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Padding = new Thickness(56, 40, 56, 40), Child = stack,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        SourceInitialized += (_, _) =>
        {
            // en pixels, sur l'écran visé (WPF ajuste ensuite à la mise à l'échelle de cet écran)
            var h = new WindowInteropHelper(this).Handle;
            SetWindowPos(h, new IntPtr(-1), _bounds.Left, _bounds.Top, _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top, 0x0010 | 0x0040);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        Loaded += (_, _) => timer.Start();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
