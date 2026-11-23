using System.Collections.ObjectModel;
using LookGo.Core.Models;
using LookGo.Gui.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Controls;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Data;
using Microsoft.UI.Reactor.Data.Providers;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;
using static Microsoft.UI.Reactor.Advanced.Factories;

namespace LookGo.Gui.Components;

public sealed record EditorSectionProps(
    PlaylistEditorState Editor,
    PlaybackState Playback,
    ThumbnailCache Cache,
    bool FileOpBusy,
    Action OnOpenFile,
    Action OnNew,
    Action OnSave,
    Action OnSaveAs,
    Action OnLoadFromPlayback,
    Action OnAddImages,
    Action OnAddEmptyRow,
    Action OnMoveUp,
    Action OnMoveDown,
    Action<string> OnDeleteRow,
    Action<string> OnSelectRow,
    Action<string, string> OnUpdateRowPath,
    Action<double> OnChangeInterval,
    Action OnApply,
    Action OnCancelApply);

public sealed class EditorSection : Component<EditorSectionProps>
{
    public override Element Render()
    {
        var props = Props;
        var editor = props.Editor;
        var playback = props.Playback;
        var dispatcher = UseMemo(() => DispatcherQueue.GetForCurrentThread());

        var collection = UseMemo(() => new ObservableCollection<EditorRow>());
        var dataSource = UseMemo(
            () => new ObservableListDataSource<EditorRow>(collection, r => (RowKey)r.Id),
            collection);
        UseEffect(() => () => dataSource.Dispose(), dataSource);

        UseEffect(() =>
        {
            SyncCollection(collection, editor.Rows);
            return () => { };
        }, editor.Rows);

        var columns = UseMemo<FieldDescriptor[]>(() =>
        [
            Column<EditorRow>(nameof(EditorRow.OrderNumber), r => r.OrderNumber, displayName: "#", width: 48).NotSortable(),
            Column<EditorRow>(nameof(EditorRow.RawPath), r => r.RawPath, editable: true, displayName: "画像パス（クリックで編集）", width: 350).NotSortable(),
            Column<EditorRow>(nameof(EditorRow.ValidationSummary), r => r.ValidationSummary, displayName: "状態", width: 140).NotSortable(),
        ]);

        var selectedId = editor.SelectedRowId;
        var selectedIndex = -1;
        for (var i = 0; i < editor.Rows.Count; i++)
        {
            if (string.Equals(editor.Rows[i].Id, selectedId, StringComparison.Ordinal))
            {
                selectedIndex = i;
                break;
            }
        }

        var canMoveUp = selectedIndex > 0;
        var canMoveDown = selectedIndex >= 0 && selectedIndex < editor.Rows.Count - 1;
        var canDelete = selectedIndex >= 0;
        var canSaveCurrent = editor.CanOverwriteFile(editor.FilePath);
        var statusText = editor.GetStatusLabel(playback);
        var isMatched = editor.IsDisplayedMatch(playback);
        var statusBg = !string.IsNullOrWhiteSpace(editor.ApplyError)
            ? Theme.SystemCriticalBackground
            : editor.IsApplying
                ? Theme.SystemAttentionBackground
                : isMatched
                    ? Theme.SystemSuccessBackground
                    : editor.HasUnsavedChanges
                        ? Theme.SystemCautionBackground
                        : Theme.SubtleFill;

        var dataGrid = Component<DataGridComponent<EditorRow>, DataGridElement<EditorRow>>(
            new DataGridElement<EditorRow>
            {
                Source = dataSource,
                Columns = columns,
                SelectionMode = Microsoft.UI.Reactor.Controls.SelectionMode.None,
                Editable = true,
                EditMode = EditMode.Cell,
                RowHeight = 38,
                AllowColumnReorder = false,
                CellTemplate = ctx => RenderCell(ctx, selectedId, props.OnSelectRow),
                OnRowChanged = (key, updatedRow) =>
                {
                    dispatcher?.TryEnqueue(() => props.OnUpdateRowPath(key.ToString(), updatedRow.RawPath));
                    return Task.CompletedTask;
                },
                EmptyTemplate = Border(
                    TextBlock("プレイリストが空です。「画像を追加...」または「空行を追加」で画像を指定してください。")
                        .Opacity(0.65)
                        .HAlign(HorizontalAlignment.Center)
                        .VAlign(VerticalAlignment.Center)
                ).Padding(16),
            }
        ).WithKey($"dg-EditorRow-{dataSource.GetHashCode()}");

        var thumbnailGrid = editor.Rows.Count > 0
            ? (Element)(GridView<EditorRow>(
                editor.Rows,
                r => r.Id,
                (row, _) => Component<ThumbnailTile, ThumbnailTileProps>(new ThumbnailTileProps(
                    TileKey: $"edit-{row.Id}",
                    ResolvedPath: row.ResolvedPath,
                    OrderNumber: row.OrderNumber,
                    IsSelectedOrActive: string.Equals(row.Id, selectedId, StringComparison.Ordinal),
                    ShowFileName: true,
                    IsInvalid: !row.IsValid,
                    InvalidMessage: row.ValidationMessage,
                    ShowDeleteButton: true,
                    DecodeWidth: 180,
                    OnSelect: () => props.OnSelectRow(row.Id),
                    OnDelete: () => props.OnDeleteRow(row.Id),
                    Cache: props.Cache))
            ) with { SelectionMode = ListViewSelectionMode.None })
            : Border(
                TextBlock("編集中のサムネイルがここに表示されます")
                    .Opacity(0.6)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            ).Background(Theme.LayerFill).CornerRadius(6);

        return Card(
            Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Auto, GridSize.Star()],
                // Row 0: File & Apply Toolbar
                FlexRow(
                    TextBlock("プレイリスト編集（下書き）").Bold().FontSize(16).VAlign(VerticalAlignment.Center),
                    Button("開く...", props.OnOpenFile).IsEnabled(!props.FileOpBusy),
                    Button("新規", props.OnNew),
                    Button("保存", props.OnSave).IsEnabled(!props.FileOpBusy && canSaveCurrent),
                    Button("名前を付けて保存...", props.OnSaveAs).IsEnabled(!props.FileOpBusy),
                    Button("再生中を読込", props.OnLoadFromPlayback).IsEnabled(playback.Images.Count > 0),
                    Border(Empty()).Flex(grow: 1),
                    TextBlock("間隔(秒):").FontSize(13).VAlign(VerticalAlignment.Center),
                    NumberBox(
                        editor.IntervalSeconds,
                        v =>
                        {
                            if (double.IsFinite(v) && v > 0)
                                props.OnChangeInterval(v);
                        })
                        .Range(0.5, 86400)
                        .SpinButtons(NumberBoxSpinButtonPlacementMode.Inline)
                        .AutomationName("切替間隔（秒）")
                        .Width(132),
                    Button("表示へ反映", props.OnApply)
                        .AccentButton()
                        .IsEnabled(!editor.IsApplying && !props.FileOpBusy),
                    editor.IsApplying
                        ? Button("反映をキャンセル", props.OnCancelApply)
                        : null
                ).VAlign(VerticalAlignment.Center).Margin(0, 0, 0, 8).Grid(row: 0) with { ColumnGap = 6, RowGap = 6, Wrap = FlexWrap.Wrap },

                // Row 1: File path & status badge
                FlexRow(
                    Border(TextBlock(statusText).Bold().FontSize(12))
                        .Background(statusBg)
                        .CornerRadius(6)
                        .Padding(10, 4, 10, 4)
                        .VAlign(VerticalAlignment.Center),
                    TextBlock(editor.FilePath is null ? "ファイル: (未保存)" : $"ファイル: {editor.FilePath}")
                        .FontSize(12)
                        .Opacity(0.8)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .VAlign(VerticalAlignment.Center)
                        .Flex(grow: 1),
                    TextBlock($"{editor.Rows.Count} 行")
                        .FontSize(12)
                        .Opacity(0.75)
                        .VAlign(VerticalAlignment.Center)
                ).Margin(0, 0, 0, 8).Grid(row: 1) with { ColumnGap = 10, RowGap = 6 },

                // Row 2: Validation / Apply failure InfoBar
                (!string.IsNullOrWhiteSpace(editor.ApplyError)
                    ? InfoBar("表示反映エラー（現在の Go 表示には反映されていません）", editor.ApplyError)
                        .Severity(InfoBarSeverity.Error)
                        .IsClosable(false)
                        .Margin(0, 0, 0, 8)
                    : Empty()).Grid(row: 2),

                // Row 3: Row Edit Toolbar
                FlexRow(
                    Button("画像を追加...", props.OnAddImages).IsEnabled(!props.FileOpBusy),
                    Button("空行を追加", props.OnAddEmptyRow),
                    Button("上へ", props.OnMoveUp).IsEnabled(canMoveUp),
                    Button("下へ", props.OnMoveDown).IsEnabled(canMoveDown),
                    Button("削除", () =>
                    {
                        if (selectedId is not null)
                            props.OnDeleteRow(selectedId);
                    }).IsEnabled(canDelete),
                    TextBlock("パス列をクリックして直接編集できます。編集・保存しても「表示へ反映」を押すまで再生中の表示は変わりません。")
                        .FontSize(12)
                        .Opacity(0.7)
                        .VAlign(VerticalAlignment.Center)
                        .Margin(8, 0, 0, 0)
                ).Margin(0, 0, 0, 8).Grid(row: 3) with { ColumnGap = 6, RowGap = 6, Wrap = FlexWrap.Wrap },

                // Row 4: Split View (DataGrid + Synchronized Thumbnail Grid)
                Grid(
                    columns: [GridSize.Star(1.15), GridSize.Star(1.0)],
                    rows: [GridSize.Star()],
                    Border(dataGrid)
                        .BorderThickness(1)
                        .BorderBrush(Theme.CardStroke)
                        .CornerRadius(6)
                        .Margin(0, 0, 8, 0)
                        .Grid(row: 0, column: 0),
                    Border(thumbnailGrid)
                        .BorderThickness(1)
                        .BorderBrush(Theme.CardStroke)
                        .CornerRadius(6)
                        .Padding(6)
                        .Grid(row: 0, column: 1)
                ).Grid(row: 4)
            )
        ).Padding(12);
    }

    private static Element RenderCell(CellContext<EditorRow> ctx, string? selectedId, Action<string> onSelectRow)
    {
        var row = ctx.Row;
        var isSelected = string.Equals(row.Id, selectedId, StringComparison.Ordinal);

        Element content;
        if (ctx.Column.Name == nameof(EditorRow.OrderNumber))
        {
            var tb = TextBlock(row.OrderNumber.ToString())
                .Bold()
                .FontSize(12)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
            content = isSelected
                ? Border(tb).BorderThickness(3, 0, 0, 0).BorderBrush(Theme.Accent).Padding(4, 2, 4, 2)
                : tb.Padding(4, 2, 4, 2);
        }
        else if (ctx.Column.Name == nameof(EditorRow.ValidationSummary))
        {
            content = TextBlock(row.ValidationSummary)
                .FontSize(12)
                .Foreground(row.IsValid ? Theme.PrimaryText : Theme.SystemCritical)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(1)
                .VAlign(VerticalAlignment.Center)
                .Padding(6, 2, 6, 2);
        }
        else
        {
            var isEmpty = string.IsNullOrWhiteSpace(row.RawPath);
            content = TextBlock(isEmpty ? "(パスを入力してください)" : row.RawPath)
                .FontSize(12)
                .Opacity(isEmpty ? 0.55 : 1.0)
                .TextTrimming(TextTrimming.CharacterEllipsis)
                .MaxLines(1)
                .VAlign(VerticalAlignment.Center)
                .Padding(6, 2, 6, 2);
        }

        var cell = Border(content).ToolTip(row.IsValid ? row.ResolvedPath : row.ValidationMessage);
        if (isSelected)
            cell = cell.Background(Theme.SubtleFill);
        return cell.OnPointerPressed((_, _) => onSelectRow(row.Id));
    }

    private static void SyncCollection(ObservableCollection<EditorRow> target, IReadOnlyList<EditorRow> source)
    {
        if (target.Count == source.Count)
        {
            var identical = true;
            var sameIds = true;
            for (var i = 0; i < source.Count; i++)
            {
                if (!Equals(target[i], source[i]))
                    identical = false;
                if (!string.Equals(target[i].Id, source[i].Id, StringComparison.Ordinal))
                    sameIds = false;
            }
            if (identical)
                return;
            if (sameIds)
            {
                for (var i = 0; i < source.Count; i++)
                {
                    if (!Equals(target[i], source[i]))
                        target[i] = source[i];
                }
                return;
            }
        }

        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
