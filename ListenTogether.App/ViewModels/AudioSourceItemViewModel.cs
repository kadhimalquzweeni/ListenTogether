using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenTogether.Core.Audio;

namespace ListenTogether.App.ViewModels;

public sealed class AudioSourceItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isMuted;
    private double _volume = 100;

    public AudioSourceItemViewModel(AudioSource source, bool isSelected = false)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? GainChanged;

    public AudioSource Source { get; }
    public string DisplayName => Source.DisplayName;
    public int? ProcessId => Source.ProcessId;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveGain)));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
                RaiseGainChanged();
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _volume, clamped))
                RaiseGainChanged();
        }
    }

    public float EffectiveGain => IsMuted ? 0f : (float)(Volume / 100d);

    private void RaiseGainChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveGain)));
        GainChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
