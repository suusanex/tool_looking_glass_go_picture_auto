using LookGo.Core.Models;
using LookGo.Core.Playlist;
using Xunit;

namespace LookGo.Core.Tests;

public sealed class PlaylistEditorReducerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _imgA;
    private readonly string _imgB;

    public PlaylistEditorReducerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lookgo-reducer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _imgA = Path.Combine(_tempDir, "a.png");
        _imgB = Path.Combine(_tempDir, "b.png");
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(_imgA, pngHeader);
        File.WriteAllBytes(_imgB, pngHeader);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void DuplicatePaths_HaveDistinctRowIds_AndDeleteTargetsOnlySelectedRow()
    {
        var state = PlaylistEditorReducer.CreateNew();
        state = PlaylistEditorReducer.AddPaths(state, [_imgA, _imgB, _imgA]);

        Assert.Equal(3, state.Rows.Count);
        var firstId = state.Rows[0].Id;
        var thirdId = state.Rows[2].Id;
        Assert.NotEqual(firstId, thirdId);

        state = PlaylistEditorReducer.SelectRow(state, thirdId);
        state = PlaylistEditorReducer.DeleteRow(state, thirdId);

        Assert.Equal(2, state.Rows.Count);
        Assert.Equal(firstId, state.Rows[0].Id);
        Assert.Equal([_imgA, _imgB], state.Rows.Select(r => r.RawPath));
        Assert.Equal([1, 2], state.Rows.Select(r => r.OrderNumber));
    }

    [Fact]
    public void MoveRow_PreservesSelectionAndUpdatesOrderNumbers()
    {
        var state = PlaylistEditorReducer.CreateNew();
        state = PlaylistEditorReducer.AddPaths(state, [_imgA, _imgB]);
        var secondId = state.Rows[1].Id;

        state = PlaylistEditorReducer.SelectRow(state, secondId);
        state = PlaylistEditorReducer.MoveRow(state, secondId, -1);

        Assert.Equal(secondId, state.SelectedRowId);
        Assert.Equal([_imgB, _imgA], state.Rows.Select(r => r.RawPath));
        Assert.Equal([1, 2], state.Rows.Select(r => r.OrderNumber));
    }

    [Fact]
    public void ApplyLifecycle_HandlesSaveApplyFailureEditDuringApplyAndExternalCliUpdate()
    {
        var listPath = Path.Combine(_tempDir, "playlist.txt");
        var state = PlaylistEditorReducer.CreateNew(10);
        state = PlaylistEditorReducer.AddPaths(state, [_imgA]);
        var (fullPath, sha) = PlaylistDocument.WriteFileAtomic(listPath, state.Rows);
        state = PlaylistEditorReducer.MarkSaved(state, fullPath, sha, state.Rows);

        var initialPlayback = new PlaybackState
        {
            Status = "playing",
            WorkerRunning = true,
            Images = [_imgB],
            Index = 0,
            ActiveOperationId = "initial-op",
            Interval = 10,
        };

        // Saved but not applied yet
        Assert.False(state.HasUnsavedChanges);
        Assert.False(state.IsDisplayedMatch(initialPlayback));
        Assert.Equal("ファイル保存済み（表示未反映）", state.GetStatusLabel(initialPlayback));

        // Begin Apply
        var snap1 = new ApplySnapshot("op-1", state.EditRevision, fullPath, sha, 10);
        state = PlaylistEditorReducer.BeginApply(state, snap1);
        Assert.True(state.IsApplying);
        Assert.False(state.CanOverwriteFile(fullPath));
        Assert.True(state.CanOverwriteFile(Path.Combine(_tempDir, "other.txt")));
        Assert.Equal("表示へ反映中...", state.GetStatusLabel(initialPlayback));

        // Failure leaves old playback and LastApplied untouched
        state = PlaylistEditorReducer.CompleteApplyFailure(state, "op-1", "conversion failed");
        Assert.False(state.IsApplying);
        Assert.Null(state.LastApplied);
        Assert.Equal("反映失敗（ファイル保存済み・表示未反映）", state.GetStatusLabel(initialPlayback));

        // Retry Apply and edit in memory WHILE applying
        var snap2 = new ApplySnapshot("op-2", state.EditRevision, fullPath, sha, 10);
        state = PlaylistEditorReducer.BeginApply(state, snap2);
        state = PlaylistEditorReducer.AddPaths(state, [_imgB]);
        Assert.Equal("表示へ反映中（さらに未反映の編集あり）", state.GetStatusLabel(initialPlayback));

        // Even after op-2 succeeds in worker, editor must NOT claim "表示へ反映済み" because EditRevision moved ahead
        var playbackOp2 = initialPlayback with { ActiveOperationId = "op-2", Images = [_imgA] };
        state = PlaylistEditorReducer.CompleteApplySuccess(state, "op-2", playbackOp2);
        Assert.False(state.IsApplying);
        Assert.False(state.IsDisplayedMatch(playbackOp2));
        Assert.Equal("編集済み（未保存・表示未反映）", state.GetStatusLabel(playbackOp2));

        // Now save and apply op-3 cleanly
        var (fullPath3, sha3) = PlaylistDocument.WriteFileAtomic(listPath, state.Rows);
        state = PlaylistEditorReducer.MarkSaved(state, fullPath3, sha3, state.Rows);
        var snap3 = new ApplySnapshot("op-3", state.EditRevision, fullPath3, sha3, 10);
        state = PlaylistEditorReducer.BeginApply(state, snap3);
        var playbackOp3 = playbackOp2 with { ActiveOperationId = "op-3", Images = [_imgA, _imgB], Paused = false };
        state = PlaylistEditorReducer.CompleteApplySuccess(state, "op-3", playbackOp3);

        Assert.True(state.IsDisplayedMatch(playbackOp3));
        Assert.Equal("表示へ反映済み", state.GetStatusLabel(playbackOp3));

        // Navigation / pause / auto-advance keep ActiveOperationId, so DisplayedMatch remains true!
        var navigatedPlayback = playbackOp3 with { Index = 1, Paused = true, Status = "paused" };
        Assert.True(state.IsDisplayedMatch(navigatedPlayback));

        // External CLI `show` changes ActiveOperationId -> immediately invalidates DisplayedMatch
        var externalPlayback = navigatedPlayback with { ActiveOperationId = "cli-external-op" };
        Assert.False(state.IsDisplayedMatch(externalPlayback));
        Assert.Equal("ファイル保存済み（表示未反映）", state.GetStatusLabel(externalPlayback));
    }

    [Fact]
    public void ReconcilePlayback_CompletesPendingApplyWhenStatusAlreadyObservedCommit()
    {
        var listPath = Path.Combine(_tempDir, "playlist.txt");
        var state = PlaylistEditorReducer.CreateNew(5);
        state = PlaylistEditorReducer.AddPaths(state, [_imgA]);
        var (fullPath, sha) = PlaylistDocument.WriteFileAtomic(listPath, state.Rows);
        state = PlaylistEditorReducer.MarkSaved(state, fullPath, sha, state.Rows);
        state = PlaylistEditorReducer.BeginApply(state, new ApplySnapshot("op-rec", state.EditRevision, fullPath, sha, 5));

        var live = new PlaybackState
        {
            WorkerRunning = true,
            Status = "playing",
            Images = [_imgA],
            Index = 0,
            ActiveOperationId = "op-rec",
            Interval = 5,
        };
        state = PlaylistEditorReducer.ReconcilePlayback(state, live);
        Assert.False(state.IsApplying);
        Assert.True(state.IsDisplayedMatch(live));
    }

    [Fact]
    public void ResetToNewAndSaveAsAnotherFileDuringApply_KeepPendingIsolatedAndNeverMisreportDisplayed()
    {
        var listA = Path.Combine(_tempDir, "listA.txt");
        var listB = Path.Combine(_tempDir, "listB.txt");

        var state = PlaylistEditorReducer.CreateNew(10);
        state = PlaylistEditorReducer.AddPaths(state, [_imgA]);
        var (fullA, shaA) = PlaylistDocument.WriteFileAtomic(listA, state.Rows);
        state = PlaylistEditorReducer.MarkSaved(state, fullA, shaA, state.Rows);
        state = PlaylistEditorReducer.BeginApply(state, new ApplySnapshot("op-a", state.EditRevision, fullA, shaA, 10));

        // Overwriting A while applying is blocked; Save As to B is permitted
        Assert.False(state.CanOverwriteFile(fullA));
        Assert.True(state.CanOverwriteFile(listB));

        state = PlaylistEditorReducer.AddPaths(state, [_imgB]);
        var (fullB, shaB) = PlaylistDocument.WriteFileAtomic(listB, state.Rows);
        state = PlaylistEditorReducer.MarkSaved(state, fullB, shaB, state.Rows);
        Assert.True(state.IsApplying);
        Assert.Equal("表示へ反映中（さらに未反映の編集あり）", state.GetStatusLabel(PlaybackState.Empty));

        // ResetToNew during apply also keeps PendingApply tracked
        state = PlaylistEditorReducer.ResetToNew(state);
        Assert.True(state.IsApplying);
        Assert.Null(state.FilePath);

        // When op-a completes, IsDisplayedMatch stays false because editor is now a new unsaved list
        var liveA = new PlaybackState
        {
            WorkerRunning = true,
            Status = "playing",
            Images = [_imgA],
            Index = 0,
            ActiveOperationId = "op-a",
            Interval = 10,
        };
        state = PlaylistEditorReducer.CompleteApplySuccess(state, "op-a", liveA);
        Assert.False(state.IsApplying);
        Assert.False(state.IsDisplayedMatch(liveA));
        Assert.Equal("新規リスト（未保存・表示未反映）", state.GetStatusLabel(liveA));
    }
}
