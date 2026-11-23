using LookGo.Core.Models;
using LookGo.Gui.Services;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace LookGo.Gui.Components;

public sealed record PlaybackStripItem(int Index, string ResolvedPath);

public sealed record PlaybackSectionProps(
    PlaybackState Playback,
    bool ActionBusy,
    string? UiError,
    ThumbnailCache Cache,
    Action OnPrev,
    Action OnTogglePause,
    Action OnNext,
    Action OnStop,
    Action OnRefresh,
    Action<int> OnJump);

public sealed class PlaybackSection : Component<PlaybackSectionProps>
{
    public override Element Render()
    {
        var props = Props;
        var playback = props.Playback;
        var hasImages = playback.Images.Count > 0;
        var canControl = playback.WorkerRunning && hasImages && !props.ActionBusy;

        var statusText = GetPlaybackStatusLabel(playback);
        var positionText = hasImages && playback.Index is { } idx
            ? $"{idx + 1} / {playback.Images.Count} 枚 • {playback.Interval:0.#} 秒ごと"
            : playback.WorkerRunning ? "待機中（未表示）" : "停止中";

        var statusBrush = playback.Status switch
        {
            "error" => Theme.SystemCriticalBackground,
            "paused" => Theme.SystemCautionBackground,
            "updating" => Theme.SystemAttentionBackground,
            "playing" => Theme.SystemSuccessBackground,
            _ => Theme.SubtleFill,
        };

        var errorMessage = !string.IsNullOrWhiteSpace(props.UiError)
            ? props.UiError
            : playback.LastError;

        var stripItems = UseMemo(
            () => playback.Images.Select((path, index) => new PlaybackStripItem(index, path)).ToArray(),
            playback.Images);

        Element stripArea = stripItems.Length > 0
            ? LazyHStack(
                stripItems,
                item => $"{item.Index}:{item.ResolvedPath}",
                (item, _) => Component<ThumbnailTile, ThumbnailTileProps>(new ThumbnailTileProps(
                    TileKey: $"play-{item.Index}-{item.ResolvedPath}",
                    ResolvedPath: item.ResolvedPath,
                    OrderNumber: item.Index + 1,
                    IsSelectedOrActive: playback.Index == item.Index,
                    ShowFileName: false,
                    IsInvalid: false,
                    InvalidMessage: null,
                    ShowDeleteButton: false,
                    DecodeWidth: 160,
                    OnSelect: () => props.OnJump(item.Index),
                    OnDelete: null,
                    Cache: props.Cache))
                    .Margin(0, 0, 8, 0)
            ).Height(138)
            : Border(
                TextBlock("現在 Looking Glass Go に表示中の画像はありません。下のリスト編集領域から「表示へ反映」を実行してください。")
                    .Opacity(0.7)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            ).Height(138).Background(Theme.LayerFill).CornerRadius(6);

        return Card(
            VStack(10,
                FlexRow(
                    TextBlock("鑑賞コントロール・表示中リスト").Bold().FontSize(16).VAlign(VerticalAlignment.Center),
                    Border(
                        TextBlock(statusText).Bold().FontSize(12)
                    ).Background(statusBrush).CornerRadius(6).Padding(10, 4, 10, 4).VAlign(VerticalAlignment.Center),
                    TextBlock(positionText).FontSize(13).Opacity(0.85).VAlign(VerticalAlignment.Center).Flex(grow: 1),
                    Button("⏮ 前へ", props.OnPrev)
                        .IsEnabled(canControl)
                        .AutomationName("前の画像へ"),
                    Button(playback.Paused ? "▶ 再開" : "⏸ 一時停止", props.OnTogglePause)
                        .AccentButton()
                        .IsEnabled(canControl)
                        .AutomationName(playback.Paused ? "スライドショーを再開" : "スライドショーを一時停止"),
                    Button("⏭ 次へ", props.OnNext)
                        .IsEnabled(canControl)
                        .AutomationName("次の画像へ"),
                    Button("⏹ 表示停止", props.OnStop)
                        .IsEnabled(playback.WorkerRunning && !props.ActionBusy)
                        .AutomationName("表示を停止"),
                    Button("↻ 更新", props.OnRefresh)
                        .AutomationName("表示状態を最新化")
                ) with { ColumnGap = 8, RowGap = 8 },
                !string.IsNullOrWhiteSpace(errorMessage)
                    ? InfoBar("再生・バックエンドエラー", errorMessage)
                        .Severity(InfoBarSeverity.Error)
                        .IsClosable(false)
                    : null,
                stripArea
            )
        ).Padding(12);
    }

    private static string GetPlaybackStatusLabel(PlaybackState playback)
    {
        if (!playback.WorkerRunning)
            return playback.Status == "error" ? "エラー終了" : "停止中";
        if (playback.Busy)
            return playback.Paused ? "表示更新中（一時停止継続）" : "表示更新中（再生継続）";
        return playback.Status switch
        {
            "playing" => "再生中",
            "paused" => "一時停止中",
            "idle" => "待機中",
            "error" => "エラー",
            "stopped" => "停止中",
            _ => playback.Status,
        };
    }
}
