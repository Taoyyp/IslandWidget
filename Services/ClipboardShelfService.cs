using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;

namespace IslandWidget.Services;

public sealed class ClipboardEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public bool IsImage { get; init; }
    public string Text { get; init; } = "";
    public string Preview { get; init; } = "";
    public BitmapSource? Image { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public string TimeLabel => CapturedAt.ToString("HH:mm");
}

public sealed class ShelfFileEntry
{
    public required string FullPath { get; init; }
    public string FileName => Path.GetFileName(FullPath) is { Length: > 0 } n ? n : FullPath;
    public bool IsDirectory => Directory.Exists(FullPath);
    public string SizeLabel
    {
        get
        {
            if (IsDirectory) return "文件夹";
            try
            {
                var len = new FileInfo(FullPath).Length;
                if (len < 1024) return $"{len} B";
                if (len < 1024 * 1024) return $"{len / 1024.0:0.#} KB";
                return $"{len / (1024.0 * 1024.0):0.1} MB";
            }
            catch { return "文件"; }
        }
    }
}

/// <summary>
/// Event-driven WM_CLIPBOARDUPDATE listener (zero polling!) + Drag-and-Drop File Shelf (文件中转站).
/// </summary>
public sealed class ClipboardShelfService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxClipboardItems = 15;
    private const int MaxShelfFiles = 24;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _suppressSelfCopy;
    private string _lastTextHash = "";

    public ObservableCollection<ClipboardEntry> History { get; } = new();
    public ObservableCollection<ShelfFileEntry> ShelfFiles { get; } = new();

    public void AttachWindow(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_suppressSelfCopy)
        {
            CaptureClipboardSafe();
        }
        return IntPtr.Zero;
    }

    private void CaptureClipboardSafe()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text.Length > 20000) text = text[..20000];
                if (text == _lastTextHash) return;
                _lastTextHash = text;

                var existing = History.FirstOrDefault(x => !x.IsImage && x.Text == text);
                if (existing is not null)
                    History.Remove(existing);

                var preview = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (preview.Length > 72) preview = preview[..72] + "…";

                History.Insert(0, new ClipboardEntry
                {
                    IsImage = false,
                    Text = text,
                    Preview = preview,
                    CapturedAt = DateTime.Now
                });
                TrimHistory();
            }
            else if (Clipboard.ContainsImage())
            {
                var src = Clipboard.GetImage();
                if (src is null) return;

                // Downscale large clipboard bitmaps so we don't bloat memory!
                BitmapSource frozen = ShrinkBitmap(src, 360);
                _lastTextHash = "";
                History.Insert(0, new ClipboardEntry
                {
                    IsImage = true,
                    Preview = $"[图片 {src.PixelWidth}×{src.PixelHeight}]",
                    Image = frozen,
                    CapturedAt = DateTime.Now
                });
                TrimHistory();
            }
        }
        catch
        {
            // Clipboard may be locked transiently by another app
        }
    }

    private static BitmapSource ShrinkBitmap(BitmapSource source, int maxWidth)
    {
        if (source.PixelWidth <= maxWidth)
        {
            if (source.CanFreeze && !source.IsFrozen) source.Freeze();
            return source;
        }
        double scale = (double)maxWidth / source.PixelWidth;
        var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
        var cached = new WriteableBitmap(transformed);
        cached.Freeze();
        return cached;
    }

    private void TrimHistory()
    {
        while (History.Count > MaxClipboardItems)
            History.RemoveAt(History.Count - 1);
    }

    public void CopyEntryToClipboard(ClipboardEntry entry)
    {
        try
        {
            _suppressSelfCopy = true;
            if (entry.IsImage && entry.Image is not null)
                Clipboard.SetImage(entry.Image);
            else if (!string.IsNullOrEmpty(entry.Text))
                Clipboard.SetText(entry.Text);
        }
        catch { }
        finally
        {
            _suppressSelfCopy = false;
        }
    }

    public int AddDroppedFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!File.Exists(raw) && !Directory.Exists(raw)) continue;
            if (ShelfFiles.Any(f => string.Equals(f.FullPath, raw, StringComparison.OrdinalIgnoreCase)))
                continue;

            ShelfFiles.Insert(0, new ShelfFileEntry { FullPath = raw });
            added++;
            while (ShelfFiles.Count > MaxShelfFiles)
                ShelfFiles.RemoveAt(ShelfFiles.Count - 1);
        }
        return added;
    }

    public void CopyShelfFilesToClipboard()
    {
        if (ShelfFiles.Count == 0) return;
        try
        {
            _suppressSelfCopy = true;
            var list = new StringCollection();
            foreach (var f in ShelfFiles)
            {
                if (File.Exists(f.FullPath) || Directory.Exists(f.FullPath))
                    list.Add(f.FullPath);
            }
            if (list.Count > 0)
                Clipboard.SetFileDropList(list);
        }
        catch { }
        finally
        {
            _suppressSelfCopy = false;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try { RemoveClipboardFormatListener(_hwnd); } catch { }
            _hwndSource?.RemoveHook(WndProc);
            _hwnd = IntPtr.Zero;
        }
    }
}
