// Application-styled About dialog: dark rounded chrome, author, copyright and a
// clickable project link - replaces the stock MessageBox.

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GameManager;

public partial class AboutWindow : Window
{
    public AboutWindow(string body, string diagnostics)
    {
        InitializeComponent();
        BodyText.Text = body;
        DiagText.Text = diagnostics;

        // built in code: an x:Static extension cannot feed the Uri-typed
        // NavigateUri property directly (string -> Uri throws at BAML load)
        var link = new Hyperlink(new Run(Strings.AboutProjectUrl))
        {
            NavigateUri = new Uri(Strings.AboutProjectUrl),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8D, 0xA4, 0xFF)),
            TextDecorations = null
        };
        link.RequestNavigate += OnLinkClick;
        LinkHost.Cursor = Cursors.Hand;
        LinkHost.Inlines.Clear();
        LinkHost.Inlines.Add(link);

        // gamepad right-stick analog scrolling (driven from MainWindow's poll
        // loop via DriveScroll, so the About window needs no poller of its own)
        Loaded += (_, _) => { CompositionTarget.Rendering += OnRenderTick; };
        Closed += (_, _) => { CompositionTarget.Rendering -= OnRenderTick; };
    }

    /// <summary>Latest right-stick Y from the pad poll (-1..+1). Set from
    /// MainWindow's polling callback.</summary>
    internal double PadScrollY;

    // Analog scroll: velocity-proportional (full deflection ~ 540 px/s), sampled
    // per render tick. Runs only while the dialog is open.
    private DateTime _lastTick = DateTime.UtcNow;

    private void OnRenderTick(object? sender, EventArgs e)
    {
        double ry = PadScrollY;
        if (ry == 0)
        {
            _lastTick = DateTime.UtcNow;
            return;
        }
        var now = DateTime.UtcNow;
        double seconds = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (seconds <= 0 || seconds > 0.25) return; // stale tick (dialog hidden)

        Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset - ry * 540 * seconds);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Up) { Scroll.LineUp(); e.Handled = true; }
        else if (e.Key == Key.Down) { Scroll.LineDown(); e.Handled = true; }
        else if (e.Key == Key.PageUp) { Scroll.PageUp(); e.Handled = true; }
        else if (e.Key == Key.PageDown) { Scroll.PageDown(); e.Handled = true; }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        // clicking the project link must not start a window drag
        if (e.OriginalSource is DependencyObject src && LinkHost.IsAncestorOf(src)) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnLinkClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
        }
        catch
        {
            // browser association missing - nothing useful to do here
        }
        e.Handled = true;
    }
}
