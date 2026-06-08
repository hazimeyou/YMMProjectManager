using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace YMMProjectManager.Domain;

public enum YmmpRole
{
    Main,
    Backup,
    Draft,
    Branch,
    Short,
    Archive,
    Rejected,
    Unknown,
}

public sealed class LinkedYmmpFile : INotifyPropertyChanged
{
    private string filePath = string.Empty;
    private string? displayName;
    private YmmpRole role = YmmpRole.Unknown;
    private string? memo;
    private DateTimeOffset updatedAt = DateTimeOffset.Now;
    private DateTimeOffset? lastCheckedAt;
    private bool exists;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string FilePath
    {
        get => filePath;
        set
        {
            if (SetField(ref filePath, value))
            {
                UpdatedAt = DateTimeOffset.Now;
                OnPropertyChanged(nameof(EffectiveDisplayName));
            }
        }
    }

    public string? DisplayName
    {
        get => displayName;
        set
        {
            if (SetField(ref displayName, value))
            {
                UpdatedAt = DateTimeOffset.Now;
                OnPropertyChanged(nameof(EffectiveDisplayName));
            }
        }
    }

    public YmmpRole Role
    {
        get => role;
        set
        {
            if (SetField(ref role, value))
            {
                UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public string? Memo
    {
        get => memo;
        set
        {
            if (SetField(ref memo, value))
            {
                UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt
    {
        get => updatedAt;
        set => SetField(ref updatedAt, value);
    }

    public DateTimeOffset? LastCheckedAt
    {
        get => lastCheckedAt;
        set => SetField(ref lastCheckedAt, value);
    }

    public bool Exists
    {
        get => exists;
        set => SetField(ref exists, value);
    }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? Path.GetFileNameWithoutExtension(FilePath)
            : DisplayName!;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProjectFolder : INotifyPropertyChanged
{
    private string name = string.Empty;
    private int displayOrder;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => name;
        set
        {
            if (SetField(ref name, value))
            {
                UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public int DisplayOrder
    {
        get => displayOrder;
        set
        {
            if (SetField(ref displayOrder, value))
            {
                UpdatedAt = DateTimeOffset.Now;
            }
        }
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class ProjectEntry : INotifyPropertyChanged
{
    private ImageSource? thumbnailSource;

    public string FullPath { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset? LastAccess { get; set; }
    public string? ThumbnailCacheDirectory { get; set; }
    public Guid? FolderId { get; set; }
    public ObservableCollection<LinkedYmmpFile> LinkedYmmpFiles { get; } = [];

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
