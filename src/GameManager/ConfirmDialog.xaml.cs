// Second-stage confirmation for the destructive power actions (forced shut
// down / restart): the power panel picks the action, this dialog makes the
// irreversible step explicit. Dark card matching the panel's chrome.
//
// Input surface (all four device classes):
//   - Mouse / touch: plain buttons.
//   - Keyboard: Enter presses the focused button, Left/Right or Tab moves the
//     focus, Esc cancels. Focus starts on "Cancel" so Enter alone cannot
//     power the machine off.
//   - Gamepad: MainWindow's pad poll routes dpad/stick left-right to
//     SwitchFocus, A to PressFocused, B to Cancel.
//
// The key/button hint badges follow the live input device (Enter+Esc for
// keyboard/mouse, A+B for gamepad) like the power panel does.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameManager;

public partial class ConfirmDialog : Window
{
    /// <summary>True when the destructive action was explicitly confirmed.</summary>
    internal bool Confirmed { get; private set; }

    // false = cancel focused (the safe default), true = confirm focused
    private bool _confirmFocused;
    private bool _padMode;

    // cancel/confirm button slots: 120 wide + 12 gap; the ring base offset
    // already sits at the cancel button (-3,-3 margin covers the 126x48 ring)
    private const double ConfirmOffset = 120 + 12;

    internal ConfirmDialog(string title, string body, string confirmText, bool padMode)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        ConfirmButtonText.Text = confirmText;
        _padMode = padMode;

        UpdateBadges();

        // mouse / touch flips the badges back to the keyboard set
        PreviewMouseDown += (_, _) => MarkPadInput(pad: false);
        PreviewStylusDown += (_, _) => MarkPadInput(pad: false);

        Loaded += (_, _) =>
        {
            CancelButton.Focus(); // safe default
            PositionRing(confirm: false, animate: false);
        };
    }

    private void UpdateBadges()
    {
        EscBadge.Visibility = _padMode ? Visibility.Collapsed : Visibility.Visible;
        PadBBadge.Visibility = _padMode ? Visibility.Visible : Visibility.Collapsed;
        EnterBadge.Visibility = _padMode ? Visibility.Collapsed : Visibility.Visible;
        PadABadge.Visibility = _padMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Live input-device detection, driven by MainWindow's pad poll.</summary>
    internal void MarkPadInput(bool pad)
    {
        if (_padMode == pad) return;
        _padMode = pad;
        UpdateBadges();
    }

    /// <summary>Gamepad dpad/stick and keyboard arrows: step the focus. The
    /// ring glides to the new button.</summary>
    internal void SwitchFocus(int dx)
    {
        _confirmFocused = dx > 0;
        (_confirmFocused ? (IInputElement)ConfirmButton : (IInputElement)CancelButton).Focus();
        PositionRing(_confirmFocused, animate: true);
    }

    /// <summary>Centers the FocusRing on the cancel/confirm button; slides
    /// there with a short eased animation (or jumps for the initial
    /// placement).</summary>
    private void PositionRing(bool confirm, bool animate)
    {
        double x = confirm ? ConfirmOffset : 0;

        if (!animate)
        {
            RingSlide.X = x;
            FocusRing.Opacity = 1;
            return;
        }

        if (Math.Abs(RingSlide.X - x) < 0.5) return;

        var anim = new DoubleAnimation(RingSlide.X, x, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RingSlide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>Gamepad A / keyboard Enter: activate the focused button.</summary>
    internal void PressFocused()
    {
        if (_confirmFocused) ConfirmButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        else OnCancel(this, new RoutedEventArgs());
    }

    /// <summary>Gamepad B / Esc / X: close without acting.</summary>
    internal void Cancel()
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }

    // keeps the pad-driven focus flag in sync when focus moves by other means
    private void OnButtonFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool confirm = e.NewFocus == ConfirmButton;
        if (e.NewFocus == ConfirmButton || e.NewFocus == CancelButton)
        {
            if (_confirmFocused != confirm)
            {
                _confirmFocused = confirm;
                PositionRing(confirm, animate: true);
            }
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        MarkPadInput(pad: false); // keyboard use flips the badges

        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;
            case Key.Enter:
                PressFocused();
                e.Handled = true;
                break;
            case Key.Left:
                SwitchFocus(-1);
                e.Handled = true;
                break;
            case Key.Right:
                SwitchFocus(1);
                e.Handled = true;
                break;
        }
    }
}
