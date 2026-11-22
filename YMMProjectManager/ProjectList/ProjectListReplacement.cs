using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YMMProjectManager.Infrastructure;

/// <summary>Records and resolves the gap between parking the current list and publishing the new one.</summary>
//CSJ,class=ProjectListReplacement,プロジェクト一覧置換
internal sealed class ProjectListReplacement(string listPath)
{
    //CSJ,enum=RecoveryState,復旧状態
    internal enum RecoveryState { ActivePreserved, Published, CanRestore, CanRestoreExternal, Conflict, Unverifiable }
    //CSJ,record=Recovery,復旧
    internal sealed record Recovery(RecoveryState State, string ActivePath, string ParkedPath, string CandidatePath, string JournalPath,
        Journal Proof, FileVersion? ActiveVersion, FileVersion? ParkedVersion);
    //CSJ,record=FileVersion,ファイルバージョン
    internal sealed record FileVersion(ulong Volume, Guid FileId, string Sha256);
    //CSJ,record=Journal,作業記録
    internal sealed record Journal(int Version, Guid Operation, FileVersion Original, FileVersion Result);
    //CSJ,record=Envelope,付帯情報付きデータ
    private sealed record Envelope(byte[] Payload, string Sha256);
    //CSJ,field=JsonOptions,JSONオプション
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    //CSJ,property=JournalPath,作業記録パス
    internal string JournalPath => listPath + ".publication-pending";
    internal string ParkedPath(Guid operation) => $"{listPath}.parked-{operation:N}";
    //CSJ,method=CandidatePath,候補パス
    internal string CandidatePath(Guid operation) => $"{listPath}.tmp-{operation:N}";
    private string ClosedPath(Guid operation, bool external) =>
        $"{listPath}.publication-{operation:N}." + (external ? "closed-external" : "closed");

    internal bool HasExternalResolution(Guid operation) => Exists(ClosedPath(operation, external: true));

    internal void RequireLocalNtfs()
    {
        //CSJ,variable=root,ルート
        var root = Path.GetPathRoot(listPath);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':')
            throw new IOException("Recoverable project list publication requires a fixed local NTFS drive.");
        //CSJ,variable=drive,ドライブ
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recoverable project list publication requires a fixed local NTFS drive.");
    }

    //CSJ,method=IsPending,保留かを判定する
    internal bool IsPending() => Exists(JournalPath);

    //CSJ,method=Exists,存在するか判定する
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal void RequireNoPending()
    {
        if (IsPending())
            throw new InvalidDataException($"An interrupted project list save needs explicit recovery. Evidence: {JournalPath}");
    }

    //CSJ,method=WriteAsync,書き込む（非同期）
    internal async Task<FileStream> WriteAsync(Journal journal, CancellationToken token)
    {
        //CSJ,variable=temporary,一時
        var temporary = $"{JournalPath}-{journal.Operation:N}.tmp";
        //CSJ,variable=stream,ストリーム
        var stream = OwnedFilePublication.CreateOwnedOutputFile(temporary);
        try
        {
            //CSJ,variable=payload,本体データ
            var payload = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
            //CSJ,variable=evidence,証跡
            var evidence = new Envelope(payload, Convert.ToHexString(SHA256.HashData(payload)));
            await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(stream.SafeFileHandle, JournalPath);
            return stream;
        }
        catch
        {
            OwnedFilePublication.TryDeleteOpenedPath(stream.SafeFileHandle);
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    //CSJ,method=InspectAsync,点検する（非同期）
    internal async Task<Recovery?> InspectAsync(CancellationToken token)
    {
        if (!IsPending()) return null;
        RequireLocalNtfs();
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(listPath)!, enforceShare: true);
        //CSJ,variable=marker,マーカー
        using var marker = Open(JournalPath);
        //CSJ,variable=journal,作業記録
        var journal = await ReadAsync(marker, token).ConfigureAwait(false);
        //CSJ,variable=active,有効
        using var active = TryOpen(listPath);
        return await ObserveAsync(journal, active, token).ConfigureAwait(false);
    }

    //CSJ,method=ResolveAsync,解決する（非同期）
    internal async Task<Recovery> ResolveAsync(Recovery confirmed, CancellationToken token)
    {
        RequireLocalNtfs();
        //CSJ,variable=fence,整合性境界
        using var fence = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(listPath)!, enforceShare: true);
        //CSJ,variable=marker,マーカー
        using var marker = Open(JournalPath, canRename: true);
        //CSJ,variable=journal,作業記録
        var journal = await ReadAsync(marker, token).ConfigureAwait(false);
        // An active file that the user chose to keep cannot be renamed or written until the journal is archived.
        //CSJ,variable=active,有効
        using var active = TryOpen(listPath);
        var observed = await ObserveAsync(journal, active, token).ConfigureAwait(false);
        if (observed != confirmed || observed.State is RecoveryState.Unverifiable)
            throw new InvalidDataException("The interrupted publication no longer has the confirmed evidence. All versions were retained.");
        //CSJ,variable=external,外部
        var external = observed.State is RecoveryState.CanRestoreExternal or RecoveryState.Conflict;
        var closedPath = ClosedPath(journal.Operation, external);
        if (observed.State is RecoveryState.CanRestore or RecoveryState.CanRestoreExternal)
        {
            using var parked = Open(observed.ParkedPath, canRename: true);
            if (observed.ParkedVersion is null || !await MatchesAsync(parked, observed.ParkedVersion, token).ConfigureAwait(false))
                throw new InvalidDataException("The parked project list changed. Evidence was retained.");
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(parked.SafeFileHandle, listPath);
            // The original is already restored; persist the outcome even when cancellation arrives here.
            OwnedFilePublication.RenameOpenedPath(marker.SafeFileHandle, closedPath);
        }
        else
        {
            token.ThrowIfCancellationRequested();
            OwnedFilePublication.RenameOpenedPath(marker.SafeFileHandle, closedPath);
        }
        // Keep all evidence when a user resolves an interrupted save.
        return observed;
    }

    //CSJ,method=ObserveAsync,観測する（非同期）
    private async Task<Recovery> ObserveAsync(Journal journal, FileStream? active, CancellationToken token)
    {
        var parkedPath = ParkedPath(journal.Operation);
        using var parked = TryOpen(parkedPath);
        var parkedVersion = parked is null ? null : await ReadFileVersionAsync(parked, token).ConfigureAwait(false);
        //CSJ,variable=activeVersion,有効バージョン
        var activeVersion = active is null ? null : await ReadFileVersionAsync(active, token).ConfigureAwait(false);
        //CSJ,variable=state,状態
        var state = (activeVersion, parkedVersion) switch
        {
            (null, null) => RecoveryState.Unverifiable,
            (null, _) => parkedVersion == journal.Original ? RecoveryState.CanRestore : RecoveryState.CanRestoreExternal,
            _ when activeVersion == journal.Result => RecoveryState.Published,
            _ when activeVersion == journal.Original => RecoveryState.ActivePreserved,
            _ => RecoveryState.Conflict,
        };
        return new Recovery(state, listPath, parkedPath, CandidatePath(journal.Operation), JournalPath, journal, activeVersion, parkedVersion);
    }

    //CSJ,method=Open,開く
    internal static FileStream Open(string path, bool canRename = false, FileShare share = FileShare.Read)
    {
        var handle = OwnedFilePublication.OpenOrdinaryPath(path, expectDirectory: false, allowRename: canRename, share);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    //CSJ,method=TryOpen,表示を試みる
    internal static FileStream? TryOpen(string path, FileShare share = FileShare.Read)
    {
        try { return Open(path, share: share); }
        catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
        { return null; }
    }

    internal static void VerifyCompatibleFileSecurity(FileStream original, FileStream candidate)
    {
        // The new file inherits from its directory. Refuse to write list bytes until its security is known to match.
        const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        if (original.GetAccessControl().GetSecurityDescriptorSddlForm(sections) !=
            candidate.GetAccessControl().GetSecurityDescriptorSddlForm(sections))
            throw new InvalidDataException("The staged list does not have the original file permissions.");
        if (HasSpecialAccessPolicy(original.SafeFileHandle) || HasSpecialAccessPolicy(candidate.SafeFileHandle))
            throw new InvalidDataException("Project list replacement cannot preserve a custom access policy.");
    }

    internal static void VerifyCompatibleFileMetadata(FileStream original, FileStream candidate, string parkedPath)
    {
        // A rename does not inherit the displaced file's metadata as File.Replace did.
        VerifyCompatibleFileSecurity(original, candidate);
        //CSJ,variable=ordinary,通常
        const FileAttributes ordinary = FileAttributes.Archive | FileAttributes.Normal;
        if ((File.GetAttributes(original.SafeFileHandle) & ~ordinary) != 0 ||
            (File.GetAttributes(candidate.SafeFileHandle) & ~ordinary) != 0)
            throw new InvalidDataException("Project list replacement requires ordinary files without custom file attributes.");

        //CSJ,variable=find,検索
        using var find = FindFirstStream(OwnedFilePublication.ExtendedPath(parkedPath), 0, out var stream, 0);
        if (find.IsInvalid)
        {
            //CSJ,variable=error,エラー
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return; // ERROR_HANDLE_EOF: no streams.
            throw new IOException("Could not verify the project's file streams.", new Win32Exception(error));
        }
        if (stream.Name != "::$DATA" || FindNextStream(find, out _))
            throw new InvalidDataException("The project list has additional file streams that a rename cannot preserve.");
        //CSJ,variable=streamError,ストリームエラー
        var streamError = Marshal.GetLastWin32Error();
        if (streamError != 38)
            throw new IOException("Could not verify the project's file streams.", new Win32Exception(streamError));
    }

    private static bool HasSpecialAccessPolicy(SafeFileHandle file)
    {
        //CSJ,variable=fileObject,ファイルオブジェクト
        const int fileObject = 1;
        const int labelAttributesAndScope = 0x10 | 0x20 | 0x40;
        //CSJ,variable=error,エラー
        var error = GetSecurityInfo(file, fileObject, labelAttributesAndScope, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            out var acl, out var descriptor);
        try
        {
            if (error != 0) throw new IOException("Could not verify the project's extended access policy.", new Win32Exception((int)error));
            // AceCount is the third 16-bit slot in the ACL header.
            return acl != IntPtr.Zero && Marshal.ReadInt16(acl, 4) != 0;
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    //CSJ,method=GetSecurityInfo,セキュリティ情報を取得する
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle file, int objectType, int securityInformation,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    //CSJ,struct=StreamInfo,ストリーム情報
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamInfo
    {
        //CSJ,field=length,長さ
        private long length;
        //CSJ,field=Name,名前
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string Name;
    }

    //CSJ,class=StreamFindHandle,ストリーム検索ハンドル
    private sealed class StreamFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private StreamFindHandle() : base(ownsHandle: true) { }
        //CSJ,method=ReleaseHandle,ハンドルを解放する
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    //CSJ,method=FindFirstStream,最初ストリームを探す
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern StreamFindHandle FindFirstStream(string path, int level, out StreamInfo data, uint flags);

    //CSJ,method=FindNextStream,次ストリームを探す
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(StreamFindHandle find, out StreamInfo data);

    //CSJ,method=FindClose,閉じるを探す
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    //CSJ,method=ReadAsync,読み取る（非同期）
    private static async Task<Journal> ReadAsync(FileStream stream, CancellationToken token)
    {
        if (stream.Length is <= 0 or > 4096)
            throw new InvalidDataException("Invalid interrupted project list journal length.");
        //CSJ,variable=envelope,付帯情報付きデータ
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, JsonOptions, token).ConfigureAwait(false);
        if (envelope?.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
            throw new InvalidDataException("Invalid interrupted project list journal checksum.");
        //CSJ,variable=record,記録
        var record = JsonSerializer.Deserialize<Journal>(envelope.Payload, JsonOptions);
        if (record is not { Version: 1 } || record.Operation == Guid.Empty || !IsValid(record.Original) || !IsValid(record.Result) ||
            record.Original == record.Result)
            throw new InvalidDataException("Invalid interrupted project list journal.");
        return record;
    }

    //CSJ,method=IsValid,有効かを判定する
    private static bool IsValid(FileVersion? version) => version is { Sha256.Length: 64 } &&
        version.FileId != Guid.Empty && version.Sha256.All(Uri.IsHexDigit);

    //CSJ,method=ReadFileVersionAsync,ファイルバージョンを読み取る（非同期）
    internal static async Task<FileVersion> ReadFileVersionAsync(FileStream stream, CancellationToken token)
    {
        var (volume, id) = ProjectFileEvidence.ReadPhysicalIdentity(stream);
        stream.Position = 0;
        //CSJ,variable=hash,ハッシュ
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return new FileVersion(volume, id, hash);
    }

    //CSJ,method=MatchesAsync,一致するか確認する（非同期）
    internal static async Task<bool> MatchesAsync(FileStream stream, FileVersion expected, CancellationToken token)
    {
        var (volume, id) = ProjectFileEvidence.ReadPhysicalIdentity(stream);
        return volume == expected.Volume && id == expected.FileId &&
            await ReadFileVersionAsync(stream, token).ConfigureAwait(false) == expected;
    }
}
