using System.Text.Json;

namespace StrongholdObserver;

// Widths are stored in 96-DPI units and only written after a manual resize.
sealed class ColumnWidths
{
    readonly string _path;
    readonly Dictionary<string, double> _widths = new(StringComparer.Ordinal);
    bool _dirty;

    public ColumnWidths(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            using var stream = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(stream);
            if (saved == null) return;
            foreach (var (key, value) in saved)
                if (double.IsFinite(value) && value is > 0 and <= 65536) _widths[key] = value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public bool TryGet(string key, int dpi, out int pixels)
    {
        if (_widths.TryGetValue(key, out var width))
        {
            pixels = (int)Math.Round(width * dpi / 96);
            return true;
        }
        pixels = 0;
        return false;
    }

    public void Set(string key, int pixels, int dpi)
    {
        _widths[key] = pixels * 96d / dpi;
        _dirty = true;
    }

    public bool Save()
    {
        if (!_dirty) return true;
        var temporary = _path + $".{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, _widths, JsonOptions.Options);
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
