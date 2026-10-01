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
    public double ScrollY { get; set; }
    public double MediaPosition { get; set; }
    public bool WasPlaying { get; set; }
    public string DisplayTitle => $"{(IsPlayingAudio ? "♫ " : IsAudioOnly ? "◌ " : "")}{Title}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
