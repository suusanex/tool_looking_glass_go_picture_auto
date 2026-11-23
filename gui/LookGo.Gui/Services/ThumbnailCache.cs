using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LookGo.Gui.Services;

public enum ThumbnailStatus
{
    Loading,
    Loaded,
    Failed,
    Empty,
}

public sealed record ThumbnailLoadResult(ThumbnailStatus Status, BitmapImage? Image = null, string? Error = null)
{
    public static ThumbnailLoadResult Loading { get; } = new(ThumbnailStatus.Loading);
    public static ThumbnailLoadResult Empty { get; } = new(ThumbnailStatus.Empty);
    public static ThumbnailLoadResult Failed(string error) => new(ThumbnailStatus.Failed, null, error);
    public static ThumbnailLoadResult Loaded(BitmapImage image) => new(ThumbnailStatus.Loaded, image, null);
}

public sealed class ThumbnailCache
{
    private const int MaxEntries = 200;
    private const long MaxFileBytes = 64L * 1024L * 1024L;

    private readonly record struct CacheKey(string Path, long LastWriteTicks, long Length, int DecodeWidth);

    private sealed class CacheEntry
    {
        public required CacheKey Key { get; init; }
        public required BitmapImage Image { get; init; }
        public LinkedListNode<CacheKey>? Node { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries = new();
    private readonly LinkedList<CacheKey> _lru = new();
    private readonly Dictionary<CacheKey, Task<ThumbnailLoadResult>> _inflight = new();
    private readonly SemaphoreSlim _ioThrottle = new(4, 4);

    public async Task<ThumbnailLoadResult> LoadAsync(
        string? resolvedPath,
        int decodeWidth,
        DispatcherQueue dispatcher,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return ThumbnailLoadResult.Empty;

        FileInfo info;
        try
        {
            info = new FileInfo(resolvedPath);
            if (!info.Exists)
                return ThumbnailLoadResult.Failed("ファイルが見つかりません");
            if (info.Length <= 0)
                return ThumbnailLoadResult.Failed("空のファイルです");
            if (info.Length > MaxFileBytes)
                return ThumbnailLoadResult.Failed("ファイルサイズが大きすぎます");
        }
        catch (Exception ex)
        {
            return ThumbnailLoadResult.Failed(ex.Message);
        }

        var key = new CacheKey(info.FullName, info.LastWriteTimeUtc.Ticks, info.Length, decodeWidth);
        Task<ThumbnailLoadResult> task;

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                if (cached.Node is not null)
                {
                    _lru.Remove(cached.Node);
                    _lru.AddFirst(cached.Node);
                }
                return ThumbnailLoadResult.Loaded(cached.Image);
            }

            if (!_inflight.TryGetValue(key, out task!))
            {
                task = LoadInternalAsync(key, dispatcher);
                _inflight[key] = task;
            }
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ThumbnailLoadResult> LoadInternalAsync(CacheKey key, DispatcherQueue dispatcher)
    {
        try
        {
            byte[] bytes;
            await _ioThrottle.WaitAsync().ConfigureAwait(false);
            try
            {
                bytes = await Task.Run(() =>
                {
                    using var stream = new FileStream(key.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream((int)Math.Min(stream.Length, MaxFileBytes));
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }).ConfigureAwait(false);
            }
            finally
            {
                _ioThrottle.Release();
            }

            var tcs = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueued = dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    var bitmap = new BitmapImage
                    {
                        DecodePixelWidth = key.DecodeWidth,
                        DecodePixelType = DecodePixelType.Logical,
                    };
                    using var ras = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
                    await bitmap.SetSourceAsync(ras);
                    tcs.TrySetResult(bitmap);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            if (!enqueued)
                return ThumbnailLoadResult.Failed("UIスレッドへディスパッチできません");

            var image = await tcs.Task.ConfigureAwait(false);
            StoreInCache(key, image);
            return ThumbnailLoadResult.Loaded(image);
        }
        catch (Exception ex)
        {
            return ThumbnailLoadResult.Failed(ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _inflight.Remove(key);
            }
        }
    }

    private void StoreInCache(CacheKey key, BitmapImage image)
    {
        lock (_lock)
        {
            if (_entries.ContainsKey(key))
                return;

            while (_entries.Count >= MaxEntries && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _entries.Remove(oldest.Value);
            }

            var node = _lru.AddFirst(key);
            _entries[key] = new CacheEntry { Key = key, Image = image, Node = node };
        }
    }
}
