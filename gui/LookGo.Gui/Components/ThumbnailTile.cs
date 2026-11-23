using LookGo.Gui.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace LookGo.Gui.Components;

public sealed record ThumbnailTileProps(
    string TileKey,
    string? ResolvedPath,
    int OrderNumber,
    bool IsSelectedOrActive,
    bool ShowFileName,
    bool IsInvalid,
    string? InvalidMessage,
    bool ShowDeleteButton,
    int DecodeWidth,
    Action OnSelect,
    Action? OnDelete,
    ThumbnailCache Cache);

public sealed class ThumbnailTile : Component<ThumbnailTileProps>
{
    public override Element Render()
    {
        var props = Props;
        var (loadResult, setLoadResult) = UseState(ThumbnailLoadResult.Loading);
        var dispatcher = UseMemo(() => DispatcherQueue.GetForCurrentThread());

        UseEffect(() =>
        {
            if (props.IsInvalid || string.IsNullOrWhiteSpace(props.ResolvedPath))
            {
                setLoadResult(ThumbnailLoadResult.Failed(props.InvalidMessage ?? "無効な画像"));
                return () => { };
            }

            if (dispatcher is null)
            {
                setLoadResult(ThumbnailLoadResult.Failed("UIスレッド利用不可"));
                return () => { };
            }

            setLoadResult(ThumbnailLoadResult.Loading);
            var cts = new CancellationTokenSource();
            var cancelled = false;

            _ = LoadIntoStateAsync(props.Cache, props.ResolvedPath, props.DecodeWidth, dispatcher, cts, () => cancelled, setLoadResult);

            return () =>
            {
                cancelled = true;
                try { cts.Cancel(); } catch { }
            };
        }, props.ResolvedPath ?? string.Empty, props.IsInvalid, props.InvalidMessage ?? string.Empty, props.DecodeWidth);

        var borderBrush = props.IsSelectedOrActive
            ? Theme.Accent
            : props.IsInvalid
                ? Theme.SystemCritical
                : Theme.CardStroke;
        var background = props.IsSelectedOrActive
            ? Theme.SubtleFill
            : Theme.CardBackground;
        var borderThickness = props.IsSelectedOrActive ? 2.5 : 1.0;

        Element preview;
        if (loadResult.Status == ThumbnailStatus.Loaded && loadResult.Image is not null)
        {
            preview = BitmapImageView.Of(loadResult.Image, Stretch.Uniform)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
        }
        else if (loadResult.Status == ThumbnailStatus.Loading)
        {
            preview = TextBlock("読込中...")
                .FontSize(11)
                .Opacity(0.6)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
        }
        else
        {
            preview = TextBlock(props.IsInvalid ? "無効" : "読込不可")
                .FontSize(11)
                .Foreground(Theme.SystemCritical)
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center);
        }

        var previewHeight = props.ShowFileName ? 88.0 : 90.0;
        var previewBox = Border(preview)
            .Height(previewHeight)
            .Background(Theme.LayerFill)
            .CornerRadius(4)
            .Padding(2);

        var headerBadge = TextBlock($"#{props.OrderNumber}")
            .Bold()
            .FontSize(11)
            .Foreground(props.IsSelectedOrActive ? Theme.Accent : Theme.SecondaryText);

        Element headerRow;
        if (props.ShowDeleteButton && props.OnDelete is { } onDelete)
        {
            headerRow = FlexRow(
                headerBadge.VAlign(VerticalAlignment.Center).Flex(grow: 1),
                Button("×", onDelete)
                    .FontSize(11)
                    .Padding(6, 1, 6, 1)
                    .ToolTip("この行を削除")
                    .AutomationName($"行 {props.OrderNumber} を削除")
                    .OnTapped((_, e) => e.Handled = true)
            ).VAlign(VerticalAlignment.Center);
        }
        else
        {
            headerRow = FlexRow(
                headerBadge.Flex(grow: 1),
                props.IsSelectedOrActive
                    ? TextBlock("表示中").Bold().FontSize(10).Foreground(Theme.Accent)
                    : null
            );
        }

        var fileName = string.IsNullOrWhiteSpace(props.ResolvedPath)
            ? "(未指定)"
            : Path.GetFileName(props.ResolvedPath);

        var clickableContent = VStack(4,
            previewBox,
            props.ShowFileName
                ? TextBlock(fileName)
                    .FontSize(11)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1)
                : null
        )
        .IsTabStop(true)
        .AutomationName(props.ShowFileName ? $"画像 {props.OrderNumber}: {fileName}" : $"表示画像 {props.OrderNumber}")
        .OnKeyDown((_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                e.Handled = true;
                props.OnSelect();
            }
        })
        .OnTapped((_, e) =>
        {
            e.Handled = true;
            props.OnSelect();
        });

        var card = Border(
            VStack(4,
                headerRow,
                clickableContent
            )
        )
        .Width(props.ShowFileName ? 144 : 118)
        .Height(props.ShowFileName ? 156 : 128)
        .Padding(6)
        .CornerRadius(8)
        .BorderThickness(borderThickness)
        .BorderBrush(borderBrush)
        .Background(background);

        if (props.ShowFileName)
        {
            var tip = props.IsInvalid && !string.IsNullOrWhiteSpace(props.InvalidMessage)
                ? $"{props.ResolvedPath}\n({props.InvalidMessage})"
                : (props.ResolvedPath ?? "(未指定)");
            card = card.ToolTip(tip);
        }

        return card;
    }

    private static async Task LoadIntoStateAsync(
        ThumbnailCache cache,
        string resolvedPath,
        int decodeWidth,
        DispatcherQueue dispatcher,
        CancellationTokenSource cts,
        Func<bool> isCancelled,
        Action<ThumbnailLoadResult> setResult)
    {
        try
        {
            var loaded = await cache.LoadAsync(resolvedPath, decodeWidth, dispatcher, cts.Token).ConfigureAwait(false);
            if (!isCancelled())
                dispatcher.TryEnqueue(() =>
                {
                    if (!isCancelled())
                        setResult(loaded);
                });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!isCancelled())
                dispatcher.TryEnqueue(() =>
                {
                    if (!isCancelled())
                        setResult(ThumbnailLoadResult.Failed(ex.Message));
                });
        }
        finally
        {
            cts.Dispose();
        }
    }
}
