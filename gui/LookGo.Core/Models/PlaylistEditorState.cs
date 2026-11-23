using LookGo.Core.Playlist;

namespace LookGo.Core.Models;

public sealed record ApplySnapshot(
    string OperationId,
    int TargetEditRevision,
    string TargetFilePath,
    string FileSha256,
    double IntervalSeconds);

public sealed record AppliedRecord(
    string OperationId,
    int AppliedEditRevision,
    string FilePath,
    string FileSha256,
    double IntervalSeconds);

public sealed record PlaylistEditorState
{
    public string? FilePath { get; init; }
    public IReadOnlyList<EditorRow> Rows { get; init; } = Array.Empty<EditorRow>();
    public string? SelectedRowId { get; init; }
    public double IntervalSeconds { get; init; } = 10.0;
    public int EditRevision { get; init; }
    public int? SavedRevision { get; init; }
    public string? SavedFilePath { get; init; }
    public string? SavedFileSha256 { get; init; }
    public ApplySnapshot? PendingApply { get; init; }
    public AppliedRecord? LastApplied { get; init; }
    public string? ApplyError { get; init; }

    public bool IsApplying => PendingApply is not null;

    public bool HasUnsavedChanges =>
        FilePath is null ||
        SavedRevision != EditRevision ||
        !PlaylistDocument.PathsEqual(FilePath, SavedFilePath);

    public bool CanOverwriteFile(string? targetPath)
    {
        if (PendingApply is null || string.IsNullOrWhiteSpace(targetPath))
            return true;
        return !PlaylistDocument.PathsEqual(targetPath, PendingApply.TargetFilePath);
    }

    public bool IsDisplayedMatch(PlaybackState playback)
    {
        if (!playback.WorkerRunning || playback.Images.Count == 0)
            return false;
        if (LastApplied is null)
            return false;
        if (!string.Equals(playback.ActiveOperationId, LastApplied.OperationId, StringComparison.Ordinal))
            return false;
        if (EditRevision != LastApplied.AppliedEditRevision)
            return false;
        if (!PlaylistDocument.PathsEqual(FilePath, LastApplied.FilePath))
            return false;
        return Math.Abs(IntervalSeconds - LastApplied.IntervalSeconds) < 1e-9;
    }

    public string GetStatusLabel(PlaybackState playback)
    {
        if (PendingApply is { } pending)
        {
            var editedSinceApply = EditRevision != pending.TargetEditRevision ||
                                   !PlaylistDocument.PathsEqual(FilePath, pending.TargetFilePath);
            return editedSinceApply
                ? "表示へ反映中（さらに未反映の編集あり）"
                : "表示へ反映中...";
        }

        if (!string.IsNullOrWhiteSpace(ApplyError))
        {
            return HasUnsavedChanges
                ? "反映失敗（編集未保存・表示未反映）"
                : "反映失敗（ファイル保存済み・表示未反映）";
        }

        if (IsDisplayedMatch(playback))
            return "表示へ反映済み";

        if (HasUnsavedChanges)
        {
            return FilePath is null
                ? "新規リスト（未保存・表示未反映）"
                : "編集済み（未保存・表示未反映）";
        }

        return "ファイル保存済み（表示未反映）";
    }
}
