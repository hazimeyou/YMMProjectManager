using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace YMMProjectManager.Domain;

public sealed class ProjectEntry : INotifyPropertyChanged
{
    private ImageSource? thumbnailSource;

    public string FullPath { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset? LastAccess { get; set; }
    public string? ThumbnailCacheDirectory { get; set; }
    public ImageSource? ThumbnailSource
    {
        get => thumbnailSource;
        set
        {
            if (!ReferenceEquals(thumbnailSource, value))
            {
                thumbnailSource = value;
                OnPropertyChanged();
            }
        }
    }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? Path.GetFileNameWithoutExtension(FullPath)
            : DisplayName!;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
