using System.IO;
using System.Windows.Media.Imaging;

namespace EplanEdzManager.Desktop.Services;

public sealed class ThumbnailCache
{
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, BitmapImage Image)> _lru = new();
    private int _capacity;

    public ThumbnailCache(int capacity)
    {
        _capacity = Math.Clamp(capacity, 25, 500);
    }

    public void Resize(int capacity)
    {
        _capacity = Math.Clamp(capacity, 25, 500);
        Trim();
    }

    public bool TryGet(string key, out BitmapImage? image)
    {
        if (!_lookup.TryGetValue(key, out var node))
        {
            image = null;
            return false;
        }
        _lru.Remove(node);
        _lru.AddFirst(node);
        image = node.Value.Image;
        return true;
    }

    public void Add(string key, BitmapImage image)
    {
        if (_lookup.Remove(key, out var existing)) _lru.Remove(existing);
        var node = _lru.AddFirst((key, image));
        _lookup[key] = node;
        Trim();
    }

    public void Clear()
    {
        _lookup.Clear();
        _lru.Clear();
    }

    private void Trim()
    {
        while (_lookup.Count > _capacity && _lru.Last is not null)
        {
            _lookup.Remove(_lru.Last.Value.Key);
            _lru.RemoveLast();
        }
    }
}

public static class ThumbnailDecoder
{
    public static BitmapImage Decode(byte[] bytes, int decodePixelWidth = 720)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
