using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FoxBrowser;

public sealed class BrowserTab : INotifyPropertyChanged
{
    private string _title;
    private string _address;
    private bool _isAudioOnly;
    private bool _isDarkMode;
    private bool _isPlayingAudio;
    private bool _isLoading;
    private string? _customTitle;
    private string? _colorHex;
    private System.Windows.Media.SolidColorBrush? _colorBrush;

    public BrowserTab(string address)
    {
        Id = Guid.NewGuid();
        _address = address;
        _title = "Нова вкладка";
    }

    public Guid Id { get; }
    public string Address { get => _address; set { if (_address == value) return; _address = value; OnPropertyChanged(); } }
    public string Title { get => _title; set { if (_title == value) return; _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); } }
    public bool IsLoading { get => _isLoading; set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); } }
    public bool IsAudioOnly { get => _isAudioOnly; set { if (_isAudioOnly == value) return; _isAudioOnly = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); } }
    public bool IsDarkMode { get => _isDarkMode; set { if (_isDarkMode == value) return; _isDarkMode = value; OnPropertyChanged(); } }
    public double DarkBrightness { get; set; } = 0.72;
    public double DarkContrast { get; set; } = 1.08;
    public double DarkSaturation { get; set; } = 0.88;
    public bool IsPlayingAudio { get => _isPlayingAudio; set { if (_isPlayingAudio == value) return; _isPlayingAudio = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); } }
    public string? CustomTitle
    {
        get => _customTitle;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_customTitle == normalized) return;
            _customTitle = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }
    public string? ColorHex
    {
        get => _colorHex;
        set
        {
            if (_colorHex == value) return;
            _colorHex = value;
            _colorBrush = CreateBrush(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ColorBrush));
        }
    }
    public System.Windows.Media.SolidColorBrush? ColorBrush => _colorBrush;
    public double ScrollY { get; set; }
    public double MediaPosition { get; set; }
    public bool WasPlaying { get; set; }
    public string DisplayTitle => $"{(IsPlayingAudio ? "♫ " : IsAudioOnly ? "◌ " : "")}{CustomTitle ?? Title}";

    private static System.Windows.Media.SolidColorBrush? CreateBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch { return null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
