using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Xaml.Media;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace LookGo.Gui.Components;

public sealed record BitmapImageElement : Element
{
    public ImageSource? Source { get; init; }
    public Stretch Stretch { get; init; } = Stretch.Uniform;

    internal BitmapImageElement(ImageSource? source, Stretch stretch = Stretch.Uniform)
    {
        Source = source;
        Stretch = stretch;
    }
}

internal static class BitmapImageDescriptor
{
    public static readonly ControlDescriptor<BitmapImageElement, WinUI.Image> Descriptor =
        new ControlDescriptor<BitmapImageElement, WinUI.Image>
        {
            Children = new None<BitmapImageElement, WinUI.Image>(),
        }
        .OneWay(
            get: static e => e.Source,
            set: static (c, v) => c.Source = v)
        .OneWay(
            get: static e => e.Stretch,
            set: static (c, v) => c.Stretch = v);

    internal sealed class Handler : DescriptorHandler<BitmapImageElement, WinUI.Image>
    {
        public Handler() : base(BitmapImageDescriptor.Descriptor) { }
    }
}

public static class BitmapImageView
{
    static BitmapImageView() =>
        ControlRegistry.Register<BitmapImageElement, WinUI.Image>(
            static () => new BitmapImageDescriptor.Handler());

    public static BitmapImageElement Of(ImageSource? source, Stretch stretch = Stretch.Uniform) =>
        new(source, stretch);
}
