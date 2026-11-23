using LookGo.Core.Models;

namespace LookGo.Core.Playlist;

public static class PlaylistEditorReducer
{
    public static PlaylistEditorState CreateNew(double intervalSeconds = 10.0)
    {
        return new PlaylistEditorState
        {
            IntervalSeconds = double.IsFinite(intervalSeconds) && intervalSeconds > 0 ? intervalSeconds : 10.0,
        };
    }

    public static PlaylistEditorState ResetToNew(PlaylistEditorState current)
    {
        return current with
        {
            FilePath = null,
            Rows = Array.Empty<EditorRow>(),
            SelectedRowId = null,
            EditRevision = current.EditRevision + 1,
            SavedRevision = null,
            SavedFilePath = null,
            SavedFileSha256 = null,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState LoadFromFile(
        PlaylistEditorState current,
        string filePath,
        IReadOnlyList<EditorRow> rows,
        string fileSha256)
    {
        var nextRevision = current.EditRevision + 1;
        return current with
        {
            FilePath = filePath,
            Rows = rows,
            SelectedRowId = rows.Count > 0 ? rows[0].Id : null,
            EditRevision = nextRevision,
            SavedRevision = nextRevision,
            SavedFilePath = filePath,
            SavedFileSha256 = fileSha256,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState LoadFromPlaybackPaths(
        PlaylistEditorState current,
        IReadOnlyList<string> imagePaths,
        double intervalSeconds,
        string? listPath = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(listPath) ? null : Path.GetDirectoryName(listPath);
        var rows = new List<EditorRow>(imagePaths.Count);
        for (var i = 0; i < imagePaths.Count; i++)
            rows.Add(PlaylistDocument.CreateRow(i + 1, imagePaths[i], baseDir));

        return current with
        {
            FilePath = null,
            Rows = rows,
            SelectedRowId = rows.Count > 0 ? rows[0].Id : null,
            IntervalSeconds = double.IsFinite(intervalSeconds) && intervalSeconds > 0 ? intervalSeconds : current.IntervalSeconds,
            EditRevision = current.EditRevision + 1,
            SavedRevision = null,
            SavedFilePath = null,
            SavedFileSha256 = null,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState AddPaths(PlaylistEditorState current, IEnumerable<string> rawPaths)
    {
        var baseDir = BaseDirectory(current.FilePath);
        var list = current.Rows.ToList();
        string? lastAddedId = null;
        foreach (var path in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var full = Path.GetFullPath(PlaylistDocument.ExpandUserPath(path));
            var row = PlaylistDocument.CreateRow(list.Count + 1, full, baseDir);
            list.Add(row);
            lastAddedId = row.Id;
        }

        if (lastAddedId is null)
            return current;

        return current with
        {
            Rows = list,
            SelectedRowId = lastAddedId,
            EditRevision = current.EditRevision + 1,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState AddEmptyRow(PlaylistEditorState current)
    {
        var row = PlaylistDocument.CreateRow(current.Rows.Count + 1, "", BaseDirectory(current.FilePath));
        var list = new List<EditorRow>(current.Rows.Count + 1);
        list.AddRange(current.Rows);
        list.Add(row);
        return current with
        {
            Rows = list,
            SelectedRowId = row.Id,
            EditRevision = current.EditRevision + 1,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState UpdateRowPath(PlaylistEditorState current, string rowId, string newRawPath)
    {
        var index = FindIndex(current.Rows, rowId);
        if (index < 0)
            return current;

        var existing = current.Rows[index];
        var trimmed = newRawPath?.Trim() ?? "";
        if (string.Equals(existing.RawPath, trimmed, StringComparison.Ordinal))
            return current;

        var list = current.Rows.ToList();
        list[index] = PlaylistDocument.CreateRow(existing.OrderNumber, trimmed, BaseDirectory(current.FilePath), existing.Id);
        return current with
        {
            Rows = list,
            SelectedRowId = existing.Id,
            EditRevision = current.EditRevision + 1,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState DeleteRow(PlaylistEditorState current, string rowId)
    {
        var index = FindIndex(current.Rows, rowId);
        if (index < 0)
            return current;

        var remaining = current.Rows.Where((_, i) => i != index).ToList();
        var renumbered = PlaylistDocument.RenumberAndRevalidate(remaining, BaseDirectory(current.FilePath));
        string? nextSelected = current.SelectedRowId;
        if (current.SelectedRowId == rowId)
        {
            if (renumbered.Count == 0)
                nextSelected = null;
            else
                nextSelected = renumbered[Math.Min(index, renumbered.Count - 1)].Id;
        }

        return current with
        {
            Rows = renumbered,
            SelectedRowId = nextSelected,
            EditRevision = current.EditRevision + 1,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState MoveRow(PlaylistEditorState current, string rowId, int delta)
    {
        var index = FindIndex(current.Rows, rowId);
        if (index < 0)
            return current;

        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= current.Rows.Count || targetIndex == index)
            return current;

        var list = current.Rows.ToList();
        (list[index], list[targetIndex]) = (list[targetIndex], list[index]);
        var renumbered = PlaylistDocument.RenumberAndRevalidate(list, BaseDirectory(current.FilePath));
        return current with
        {
            Rows = renumbered,
            SelectedRowId = rowId,
            EditRevision = current.EditRevision + 1,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState SelectRow(PlaylistEditorState current, string? rowId)
    {
        if (string.Equals(current.SelectedRowId, rowId, StringComparison.Ordinal))
            return current;
        if (rowId is not null && FindIndex(current.Rows, rowId) < 0)
            return current;
        return current with { SelectedRowId = rowId };
    }

    public static PlaylistEditorState SetInterval(PlaylistEditorState current, double intervalSeconds)
    {
        if (!double.IsFinite(intervalSeconds) || intervalSeconds <= 0)
            return current;
        if (Math.Abs(current.IntervalSeconds - intervalSeconds) < 1e-9)
            return current;
        return current with
        {
            IntervalSeconds = intervalSeconds,
            EditRevision = current.EditRevision + 1,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState MarkSaved(
        PlaylistEditorState current,
        string savedFilePath,
        string savedFileSha256,
        IReadOnlyList<EditorRow> savedRows)
    {
        return current with
        {
            FilePath = savedFilePath,
            Rows = savedRows,
            SavedRevision = current.EditRevision,
            SavedFilePath = savedFilePath,
            SavedFileSha256 = savedFileSha256,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState BeginApply(PlaylistEditorState current, ApplySnapshot snapshot)
    {
        return current with
        {
            PendingApply = snapshot,
            ApplyError = null,
        };
    }

    public static PlaylistEditorState CompleteApplySuccess(
        PlaylistEditorState current,
        string operationId,
        PlaybackState committedPlayback)
    {
        if (current.PendingApply is not { } pending ||
            !string.Equals(pending.OperationId, operationId, StringComparison.Ordinal))
        {
            return current;
        }

        if (!string.IsNullOrEmpty(committedPlayback.ActiveOperationId) &&
            !string.Equals(committedPlayback.ActiveOperationId, operationId, StringComparison.Ordinal))
        {
            return current with
            {
                PendingApply = null,
                ApplyError = "ワーカーが別のプレイリスト操作を確定しました。",
            };
        }

        return current with
        {
            PendingApply = null,
            ApplyError = null,
            LastApplied = new AppliedRecord(
                pending.OperationId,
                pending.TargetEditRevision,
                pending.TargetFilePath,
                pending.FileSha256,
                pending.IntervalSeconds),
        };
    }

    public static PlaylistEditorState CompleteApplyFailure(
        PlaylistEditorState current,
        string? operationId,
        string errorMessage)
    {
        var clearPending = operationId is null ||
                           (current.PendingApply is { } pending && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal));
        return current with
        {
            PendingApply = clearPending ? null : current.PendingApply,
            ApplyError = errorMessage,
        };
    }

    public static PlaylistEditorState ReconcilePlayback(PlaylistEditorState current, PlaybackState playback)
    {
        if (current.PendingApply is { } pending &&
            playback.WorkerRunning &&
            string.Equals(playback.ActiveOperationId, pending.OperationId, StringComparison.Ordinal))
        {
            return CompleteApplySuccess(current, pending.OperationId, playback);
        }
        return current;
    }

    private static string? BaseDirectory(string? filePath)
    {
        return string.IsNullOrWhiteSpace(filePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(PlaylistDocument.ExpandUserPath(filePath)));
    }

    private static int FindIndex(IReadOnlyList<EditorRow> rows, string rowId)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Id, rowId, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
