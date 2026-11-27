// One game entry: name, .lnk path, icon and UI state for the tile.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace GameManager;

public sealed class GameInfo : INotifyPropertyChanged
{
    private int _iconSize = 256;
    private bool _isSelected;
    private DateTime _lastLaunchUtc = DateTime.MinValue;

    // The icon is carried as object and cast at the binding/call site (see XAML and UpdateAcrylic):
    // an odd compiler issue prevented this specific file from resolving BitmapSource.
    public GameInfo(string lnkPath, object? icon)
    {
        LnkPath = lnkPath;
        Icon = icon;
        Name = Path.GetFileNameWithoutExtension(lnkPath);
    }

    public string Name { get; }
    public string LnkPath { get; }
    public object? Icon { get; }

    public int IconSize
    {
        get => _iconSize;
        set
        {
            if (_iconSize == value) return;
            _iconSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSize)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>Launches the game. Debounced so click + double-click paths never
    /// launch twice. Returns false when debounced or on failure.</summary>
    public bool Launch()
    {
        if ((DateTime.UtcNow - _lastLaunchUtc).TotalMilliseconds < 500) return false;
        _lastLaunchUtc = DateTime.UtcNow;
        try
        {
            Process.Start(new ProcessStartInfo(LnkPath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Strings.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}