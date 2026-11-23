using LookGo.Core.Backend;
using LookGo.Core.Models;
using LookGo.Core.Playlist;
using LookGo.Gui.Components;
using LookGo.Gui.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run<MainApp>("Looking Glass Go — 再生操作・プレイリスト編集", width: 1280, height: 860);

sealed class MainApp : Component
{
    public override Element Render()
    {
        var repoRoot = UseMemo(() => RepoLocator.FindRepositoryRoot(Environment.GetCommandLineArgs()));

        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return Border(
                VStack(12,
                    Heading("lookgo.py が見つかりません"),
                    TextBlock("リポジトリ ルートを特定できませんでした。--repo <path> 引数または LOOKGO_REPO 環境変数を指定して再起動してください。")
                        .TextWrapping()
                )
            ).Padding(32);
        }

        return Component<WorkspaceRoot, WorkspaceProps>(new WorkspaceProps(repoRoot));
    }
}

sealed record WorkspaceProps(string RepoRoot);

sealed class WorkspaceRoot : Component<WorkspaceProps>
{
    public override Element Render()
    {
        var (playback, updatePlayback) = UseReducer(PlaybackState.Empty);
        var (editor, updateEditor) = UseReducer(PlaylistEditorReducer.CreateNew());
        var (actionBusy, setActionBusy) = UseState(false);
        var (uiError, setUiError) = UseState<string?>(null);
        var (fileOpBusy, setFileOpBusy) = UseState(false);

        var dispatcher = UseMemo(() => DispatcherQueue.GetForCurrentThread());
        var cache = UseMemo(() => new ThumbnailCache());
        var backend = UseMemo<IBackendClient>(() => new PythonBridgeClient(Props.RepoRoot), Props.RepoRoot);

        var pollInFlightRef = UseRef(false);
        var disposedRef = UseRef(false);
        var latestEditorRef = UseRef(editor);
        latestEditorRef.Current = editor;

        UseEffect(() =>
        {
            return () =>
            {
                disposedRef.Current = true;
                _ = backend.DisposeAsync();
            };
        }, backend);

        async Task RefreshStatusAsync()
        {
            if (pollInFlightRef.Current || disposedRef.Current)
                return;
            pollInFlightRef.Current = true;
            try
            {
                var result = await backend.GetStatusAsync();
                if (disposedRef.Current)
                    return;
                if (result.Ok && result.State is not null)
                {
                    var liveState = result.State;
                    dispatcher?.TryEnqueue(() =>
                    {
                        if (disposedRef.Current)
                            return;
                        updatePlayback(_ => liveState);
                        updateEditor(cur => PlaylistEditorReducer.ReconcilePlayback(cur, liveState));
                    });
                }
            }
            finally
            {
                pollInFlightRef.Current = false;
            }
        }

        UseEffect(() =>
        {
            _ = RefreshStatusAsync();
            if (dispatcher is null)
                return () => { };

            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += (_, _) => _ = RefreshStatusAsync();
            timer.Start();
            return () => timer.Stop();
        }, backend);

        async Task RunPlaybackActionAsync(Func<Task<BackendResult>> action)
        {
            if (actionBusy || disposedRef.Current)
                return;
            setActionBusy(true);
            setUiError(null);
            try
            {
                var result = await action();
                if (disposedRef.Current)
                    return;
                if (result.Ok && result.State is not null)
                {
                    var nextState = result.State;
                    updatePlayback(_ => nextState);
                    updateEditor(cur => PlaylistEditorReducer.ReconcilePlayback(cur, nextState));
                }
                else
                {
                    setUiError(result.Error ?? "操作に失敗しました。");
                    await RefreshStatusAsync();
                }
            }
            catch (Exception ex)
            {
                if (!disposedRef.Current)
                    setUiError(ex.Message);
            }
            finally
            {
                if (!disposedRef.Current)
                    setActionBusy(false);
            }
        }

        async Task OpenPlaylistAsync()
        {
            if (fileOpBusy)
                return;
            setFileOpBusy(true);
            try
            {
                var picked = await PickFileAsync(new FilePickerOptions(
                    FileTypeFilter: new[] { ".txt" },
                    CommitButtonText: "開く")
                {
                    Title = "プレイリストを開く",
                });
                if (picked is null)
                    return;

                var (fullPath, rows, sha256) = PlaylistDocument.ReadFile(picked.Path);
                updateEditor(cur => PlaylistEditorReducer.LoadFromFile(cur, fullPath, rows, sha256));
            }
            catch (Exception ex)
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, $"ファイルを開けませんでした: {ex.Message}"));
            }
            finally
            {
                setFileOpBusy(false);
            }
        }

        async Task SaveAsPlaylistAsync()
        {
            if (fileOpBusy)
                return;
            setFileOpBusy(true);
            try
            {
                var snapshot = latestEditorRef.Current;
                var suggested = string.IsNullOrWhiteSpace(snapshot.FilePath)
                    ? "playlist"
                    : Path.GetFileNameWithoutExtension(snapshot.FilePath);
                var target = await PickSaveFileAsync(new SaveFilePickerOptions(
                    new[] { new FileTypeChoice("プレイリスト (*.txt)", new[] { ".txt" }) },
                    SuggestedFileName: suggested,
                    CommitButtonText: "保存")
                {
                    Title = "名前を付けて保存",
                });
                if (target is null)
                    return;

                var current = latestEditorRef.Current;
                if (!current.CanOverwriteFile(target.Path))
                {
                    updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(
                        cur, null, "現在「表示へ反映」中のファイルには上書き保存できません。別のファイル名を指定してください。"));
                    return;
                }

                var rebased = PlaylistDocument.RebaseRowsForSaveAs(current.Rows, current.FilePath, target.Path);
                var (fullPath, sha256) = PlaylistDocument.WriteFileAtomic(target.Path, rebased);
                updateEditor(cur => PlaylistEditorReducer.MarkSaved(cur, fullPath, sha256, rebased));
            }
            catch (Exception ex)
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, $"保存に失敗しました: {ex.Message}"));
            }
            finally
            {
                setFileOpBusy(false);
            }
        }

        async Task SavePlaylistAsync()
        {
            var current = latestEditorRef.Current;
            if (string.IsNullOrWhiteSpace(current.FilePath))
            {
                await SaveAsPlaylistAsync();
                return;
            }
            if (!current.CanOverwriteFile(current.FilePath))
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(
                    cur, null, "現在「表示へ反映」中のため上書き保存できません。完了またはキャンセル後に保存してください。"));
                return;
            }
            try
            {
                var (fullPath, sha256) = PlaylistDocument.WriteFileAtomic(current.FilePath, current.Rows);
                updateEditor(cur => PlaylistEditorReducer.MarkSaved(cur, fullPath, sha256, cur.Rows));
            }
            catch (Exception ex)
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, $"保存に失敗しました: {ex.Message}"));
            }
        }

        async Task AddImagesAsync()
        {
            if (fileOpBusy)
                return;
            setFileOpBusy(true);
            try
            {
                var picked = await PickMultipleFilesAsync(new FilePickerOptions(
                    CommitButtonText: "追加")
                {
                    Title = "プレイリストに追加する画像を選択",
                    FileTypeChoices = new[]
                    {
                        new FileTypeChoice("画像ファイル", new[] { ".png", ".jpg", ".jpeg", ".webp" }),
                    },
                });
                if (picked.Count == 0)
                    return;
                updateEditor(cur => PlaylistEditorReducer.AddPaths(cur, picked.Select(f => f.Path)));
            }
            catch (Exception ex)
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, $"画像選択に失敗しました: {ex.Message}"));
            }
            finally
            {
                setFileOpBusy(false);
            }
        }

        async Task ApplyToDisplayAsync()
        {
            if (latestEditorRef.Current.IsApplying || fileOpBusy)
                return;

            string? targetPath = latestEditorRef.Current.FilePath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                setFileOpBusy(true);
                try
                {
                    var picked = await PickSaveFileAsync(new SaveFilePickerOptions(
                        new[] { new FileTypeChoice("プレイリスト (*.txt)", new[] { ".txt" }) },
                        SuggestedFileName: "playlist",
                        CommitButtonText: "保存して反映")
                    {
                        Title = "反映用のプレイリストファイルを保存",
                    });
                    if (picked is null)
                        return;
                    targetPath = picked.Path;
                }
                catch (Exception ex)
                {
                    updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, $"保存先の選択に失敗しました: {ex.Message}"));
                    return;
                }
                finally
                {
                    setFileOpBusy(false);
                }
            }

            var current = latestEditorRef.Current;
            if (current.IsApplying)
                return;

            var rowsToWrite = PlaylistDocument.PathsEqual(current.FilePath, targetPath)
                ? current.Rows
                : PlaylistDocument.RebaseRowsForSaveAs(current.Rows, current.FilePath, targetPath);

            var validationError = PlaylistDocument.ValidateForApply(rowsToWrite, current.IntervalSeconds, targetPath);
            if (validationError is not null)
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, validationError));
                return;
            }

            string fullPath;
            string sha256;
            try
            {
                (fullPath, sha256) = PlaylistDocument.WriteFileAtomic(targetPath, rowsToWrite);
            }
            catch (Exception ex)
            {
                updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(cur, null, $"ファイル保存に失敗しました: {ex.Message}"));
                return;
            }

            var operationId = Guid.NewGuid().ToString("N");
            var interval = current.IntervalSeconds;
            var applySnapshot = new ApplySnapshot(operationId, current.EditRevision, fullPath, sha256, interval);

            updateEditor(cur =>
            {
                var saved = PlaylistEditorReducer.MarkSaved(cur, fullPath, sha256, rowsToWrite);
                return PlaylistEditorReducer.BeginApply(saved, applySnapshot);
            });

            var showResult = await backend.ShowAsync(fullPath, interval, operationId, sha256);
            if (disposedRef.Current)
                return;

            if (showResult.Ok && showResult.State is not null)
            {
                var committed = showResult.State;
                updatePlayback(_ => committed);
                updateEditor(cur => PlaylistEditorReducer.CompleteApplySuccess(cur, operationId, committed));
                return;
            }

            var live = await backend.GetStatusAsync();
            if (disposedRef.Current)
                return;
            if (live.Ok && live.State is not null)
            {
                var liveState = live.State;
                updatePlayback(_ => liveState);
                if (liveState.WorkerRunning &&
                    string.Equals(liveState.ActiveOperationId, operationId, StringComparison.Ordinal))
                {
                    updateEditor(cur => PlaylistEditorReducer.ReconcilePlayback(cur, liveState));
                    return;
                }
            }

            updateEditor(cur => PlaylistEditorReducer.CompleteApplyFailure(
                cur,
                operationId,
                showResult.Error ?? "表示への反映に失敗しました。"));
        }

        async Task CancelApplyAsync()
        {
            var opId = latestEditorRef.Current.PendingApply?.OperationId;
            if (string.IsNullOrWhiteSpace(opId))
                return;
            await backend.CancelShowAsync(opId);
        }

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star()],
            Component<PlaybackSection, PlaybackSectionProps>(new PlaybackSectionProps(
                Playback: playback,
                ActionBusy: actionBusy,
                UiError: uiError,
                Cache: cache,
                OnPrev: () => _ = RunPlaybackActionAsync(() => backend.PrevAsync()),
                OnTogglePause: () => _ = RunPlaybackActionAsync(() => playback.Paused ? backend.ResumeAsync() : backend.PauseAsync()),
                OnNext: () => _ = RunPlaybackActionAsync(() => backend.NextAsync()),
                OnStop: () => _ = RunPlaybackActionAsync(() => backend.StopAsync()),
                OnRefresh: () => _ = RefreshStatusAsync(),
                OnJump: idx => _ = RunPlaybackActionAsync(() => backend.JumpAsync(idx))
            )).Margin(12, 12, 12, 6).Grid(row: 0),

            Component<EditorSection, EditorSectionProps>(new EditorSectionProps(
                Editor: editor,
                Playback: playback,
                Cache: cache,
                FileOpBusy: fileOpBusy,
                OnOpenFile: () => _ = OpenPlaylistAsync(),
                OnNew: () => updateEditor(PlaylistEditorReducer.ResetToNew),
                OnSave: () => _ = SavePlaylistAsync(),
                OnSaveAs: () => _ = SaveAsPlaylistAsync(),
                OnLoadFromPlayback: () => updateEditor(cur =>
                    PlaylistEditorReducer.LoadFromPlaybackPaths(cur, playback.Images, playback.Interval, playback.ListPath)),
                OnAddImages: () => _ = AddImagesAsync(),
                OnAddEmptyRow: () => updateEditor(PlaylistEditorReducer.AddEmptyRow),
                OnMoveUp: () =>
                {
                    if (editor.SelectedRowId is { } id)
                        updateEditor(cur => PlaylistEditorReducer.MoveRow(cur, id, -1));
                },
                OnMoveDown: () =>
                {
                    if (editor.SelectedRowId is { } id)
                        updateEditor(cur => PlaylistEditorReducer.MoveRow(cur, id, +1));
                },
                OnDeleteRow: id => updateEditor(cur => PlaylistEditorReducer.DeleteRow(cur, id)),
                OnSelectRow: id => updateEditor(cur => PlaylistEditorReducer.SelectRow(cur, id)),
                OnUpdateRowPath: (id, rawPath) => updateEditor(cur => PlaylistEditorReducer.UpdateRowPath(cur, id, rawPath)),
                OnChangeInterval: interval => updateEditor(cur => PlaylistEditorReducer.SetInterval(cur, interval)),
                OnApply: () => _ = ApplyToDisplayAsync(),
                OnCancelApply: () => _ = CancelApplyAsync()
            )).Margin(12, 6, 12, 12).Grid(row: 1)
        );
    }
}
