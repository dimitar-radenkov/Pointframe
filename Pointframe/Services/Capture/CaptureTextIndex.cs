using System.Collections.Concurrent;

namespace Pointframe.Services;

internal sealed class CaptureTextIndex : ICaptureTextIndex
{
    private const int DefaultMaxCacheEntries = 2000;

    private readonly IImageFileService _imageFiles;
    private readonly IOcrService _ocr;
    private readonly int _maxCacheEntries;
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly LinkedList<string> _lru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _nodes =
        new(StringComparer.OrdinalIgnoreCase);

    public CaptureTextIndex(IImageFileService imageFiles, IOcrService ocr, int maxCacheEntries = DefaultMaxCacheEntries)
    {
        _imageFiles = imageFiles;
        _ocr = ocr;
        _maxCacheEntries = Math.Max(1, maxCacheEntries);
    }

    public async Task<string?> GetText(CaptureItem item, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(item.FilePath, out var entry)
            && entry.CapturedAtUtc == item.CapturedAtUtc)
        {
            Touch(item.FilePath);
            return entry.Text;
        }

        var bitmap = _imageFiles.LoadForAnnotation(item.FilePath);
        var text = await _ocr.Recognize(bitmap, cancellationToken);

        lock (_cacheGate)
        {
            _cache[item.FilePath] = new CacheEntry(item.CapturedAtUtc, text);
            Touch(item.FilePath);
            TrimToLimit();
        }

        return text;
    }

    private void Touch(string key)
    {
        lock (_cacheGate)
        {
            if (_nodes.TryGetValue(key, out var existingNode))
            {
                _lru.Remove(existingNode);
            }
            else
            {
                existingNode = new LinkedListNode<string>(key);
                _nodes[key] = existingNode;
            }

            _lru.AddFirst(existingNode);
        }
    }

    private void TrimToLimit()
    {
        while (_cache.Count > _maxCacheEntries)
        {
            var leastRecentlyUsed = _lru.Last;
            if (leastRecentlyUsed is null)
            {
                break;
            }

            _lru.RemoveLast();
            _nodes.Remove(leastRecentlyUsed.Value);
            _cache.TryRemove(leastRecentlyUsed.Value, out _);
        }
    }

    private readonly record struct CacheEntry(DateTime CapturedAtUtc, string? Text);
}
