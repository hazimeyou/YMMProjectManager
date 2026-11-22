using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace YMMProjectManager.Domain;

//CSJ,enum=YmmpRole,YMMPファイルの役割
public enum YmmpRole
{
    //CSJ,field=Main,メイン
    Main,
    //CSJ,field=Backup,バックアップ
    Backup,
    //CSJ,field=Draft,下書き
    Draft,
    //CSJ,field=Branch,ブランチ
    Branch,
    Short,
    //CSJ,field=Archive,アーカイブ
    Archive,
    //CSJ,field=Rejected,拒否
    Rejected,
    //CSJ,field=Unknown,不明
    Unknown,
}

public sealed class LinkedYmmpFile : INotifyPropertyChanged
{
    //CSJ,field=filePath,ファイルパス
    private string filePath = string.Empty;
    //CSJ,field=displayName,表示名
    private string? displayName;
    private YmmpRole role = YmmpRole.Unknown;
    //CSJ,field=memo,メモ
    private string? memo;
    private DateTimeOffset updatedAt = DateTimeOffset.Now;
    private DateTimeOffset? lastCheckedAt;
    private bool exists;

    //CSJ,property=Id,識別ID
    public Guid Id { get; set; } = Guid.NewGuid();

    //CSJ,property=FilePath,ファイルパス
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

    //CSJ,property=DisplayName,表示名
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

    //CSJ,property=Role,役割
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

    //CSJ,property=Memo,メモ
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

    //CSJ,property=RegisteredAt,登録日時
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.Now;

    //CSJ,property=UpdatedAt,更新日時
    public DateTimeOffset UpdatedAt
    {
        get => updatedAt;
        set => SetField(ref updatedAt, value);
    }

    //CSJ,property=LastCheckedAt,最終確認日時
    public DateTimeOffset? LastCheckedAt
    {
        get => lastCheckedAt;
        set => SetField(ref lastCheckedAt, value);
    }

    //CSJ,property=Exists,存在するか判定する
    public bool Exists
    {
        get => exists;
        set => SetField(ref exists, value);
    }

    //CSJ,property=EffectiveDisplayName,実際に使う表示名
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? Path.GetFileNameWithoutExtension(FilePath)
            : DisplayName!;

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    //CSJ,method=SetField,フィールドを設定する
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

    //CSJ,method=OnPropertyChanged,プロパティ変更済み時の処理
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProjectFolder : INotifyPropertyChanged
{
    //CSJ,field=name,名前
    private string name = string.Empty;
    //CSJ,field=displayOrder,表示順序
    private int displayOrder;

    //CSJ,property=Id,識別ID
    public Guid Id { get; set; } = Guid.NewGuid();

    //CSJ,property=Name,名前
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

    //CSJ,property=DisplayOrder,表示順序
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

    //CSJ,property=CreatedAt,作成日時
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    //CSJ,property=UpdatedAt,更新日時
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    //CSJ,method=SetField,フィールドを設定する
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
    //CSJ,field=thumbnailSource,サムネイル元
    private ImageSource? thumbnailSource;
    //CSJ,field=thumbnailCacheDirectory,サムネイルキャッシュディレクトリ
    private string? thumbnailCacheDirectory;
    //CSJ,field=projectId,プロジェクトID
    private Guid? projectId;
    //CSJ,field=fullPath,完全パス
    private string fullPath = string.Empty;

    //CSJ,property=FullPath,完全パス
    public string FullPath
    {
        get => fullPath;
        set
        {
            if (fullPath == value) return;
            fullPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveDisplayName));
        }
    }
    //CSJ,property=MetadataAssociation,メタデータ関連付け
    public ProjectMetadataAssociation? MetadataAssociation { get; set; }
    //CSJ,property=FileIdentity,ファイル識別情報
    public ProjectFileIdentity? FileIdentity { get; set; }
    //CSJ,property=ProjectId,プロジェクトID
    public Guid? ProjectId
    {
        get => projectId;
        set { if (projectId != value) { projectId = value; OnPropertyChanged(); } }
    }
    //CSJ,property=DisplayName,表示名
    public string? DisplayName { get; set; }
    //CSJ,property=Pinned,保持済み
    public bool Pinned { get; set; }
    //CSJ,property=LastAccess,最後アクセス
    public DateTimeOffset? LastAccess { get; set; }
    //CSJ,property=ThumbnailCacheDirectory,サムネイルキャッシュディレクトリ
    public string? ThumbnailCacheDirectory
    {
        get => thumbnailCacheDirectory;
        set
        {
            if (!string.Equals(thumbnailCacheDirectory, value, StringComparison.Ordinal))
            {
                thumbnailCacheDirectory = value;
                OnPropertyChanged();
            }
        }
    }
    //CSJ,property=FolderId,フォルダーID
    public Guid? FolderId { get; set; }
    public ObservableCollection<LinkedYmmpFile> LinkedYmmpFiles { get; } = [];

    //CSJ,property=ThumbnailSource,サムネイル元
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

    //CSJ,property=EffectiveDisplayName,実際に使う表示名
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? Path.GetFileNameWithoutExtension(FullPath)
            : DisplayName!;

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    //CSJ,method=OnPropertyChanged,プロパティ変更済み時の処理
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
