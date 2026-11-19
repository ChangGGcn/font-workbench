using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FontBook.Workbench;

public sealed class WorkbenchFamilyPreview : Control
{
    public static readonly DependencyProperty IsCardProperty = DependencyProperty.Register(
        nameof(IsCard), typeof(bool), typeof(WorkbenchFamilyPreview), new PropertyMetadata(false));
    private static readonly DependencyPropertyKey PreviewImageKey = DependencyProperty.RegisterReadOnly(
        nameof(PreviewImage), typeof(BitmapSource), typeof(WorkbenchFamilyPreview), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey PreviewStatusKey = DependencyProperty.RegisterReadOnly(
        nameof(PreviewStatus), typeof(string), typeof(WorkbenchFamilyPreview), new PropertyMetadata(null));
    public static readonly DependencyProperty PreviewImageProperty = PreviewImageKey.DependencyProperty;
    public static readonly DependencyProperty PreviewStatusProperty = PreviewStatusKey.DependencyProperty;
    internal static readonly DependencyProperty QueueProperty = DependencyProperty.RegisterAttached("LibraryPreviewQueue",
        typeof(LibraryPreviewQueue), typeof(WorkbenchFamilyPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    private LibraryPreviewQueue? _queue;
    private ScrollContentPresenter? _viewport;
    internal LibraryPreviewKey? Key
    {
        get; set;
    }
    internal CancellationTokenSource? Request
    {
        get; set;
    }

    public bool IsCard
    {
        get => (bool)GetValue(IsCardProperty); set => SetValue(IsCardProperty, value);
    }
    public BitmapSource? PreviewImage => (BitmapSource?)GetValue(PreviewImageProperty);
    public string? PreviewStatus => (string?)GetValue(PreviewStatusProperty);

    public WorkbenchFamilyPreview()
    {
        Loaded += (_, _) =>
        {
            _queue = (LibraryPreviewQueue?)GetValue(QueueProperty);
            _queue?.Attach(this);
        };
        Unloaded += (_, _) =>
        {
            _queue?.Detach(this);
            _queue = null;
            _viewport = null;
        };
        IsVisibleChanged += (_, _) => _queue?.Schedule();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DataContextProperty || e.Property == ForegroundProperty || e.Property == IsCardProperty)
        {
            ClearPreview();
            _queue?.Schedule();
        }
    }

    internal bool TryGetKey(object catalog, Color ink, out LibraryPreviewKey key)
    {
        key = default;
        if (!IsLoaded || !IsVisible || DataContext is not FamilyRow row || ActualWidth < 1 || ActualHeight < 1)
            return false;
        if (_viewport is null || !_viewport.IsAncestorOf(this))
        {
            DependencyObject? parent = VisualTreeHelper.GetParent(this);
            while (parent is not (null or ScrollContentPresenter))
                parent = VisualTreeHelper.GetParent(parent);
            _viewport = (ScrollContentPresenter?)parent;
        }
        if (_viewport is not { } viewport)
            return false;

        var area = TransformToAncestor(viewport).TransformBounds(new Rect(RenderSize));
        area.Intersect(new Rect(viewport.RenderSize));
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0)
            return false;

        float scale = (float)Math.Clamp(VisualTreeHelper.GetDpi(this).DpiScaleX, .5, 8);
        // Snap widths to small buckets so dragging the window does not queue a new raster per DIP.
        double width = ActualWidth < 16 ? ActualWidth : Math.Floor(ActualWidth / 16) * 16;
        key = new(catalog, row.Id, IsCard, Math.Clamp((int)Math.Floor(width * scale), 1, 1536),
            Math.Clamp((int)Math.Floor(ActualHeight * scale), 1, 384), scale, ink);
        return true;
    }

    internal void ClearPreview()
    {
        if (Key is null)
            return;
        Key = null;
        var request = Request;
        Request = null;
        request?.Cancel();
        Show(null, null);
    }

    internal void Show(BitmapSource? image, string? status, string? error = null)
    {
        SetValue(PreviewImageKey, image);
        SetValue(PreviewStatusKey, status);
        ToolTip = error;
    }
}

internal readonly record struct LibraryPreviewKey(object CatalogIdentity, uint FamilyId, bool IsCard,
    int Width, int Height, float Scale, Color Ink);

internal sealed class LibraryPreviewQueue : IDisposable
{
    private readonly Window _owner;
    private readonly ListBox _list;
    private readonly Func<FontCatalog?> _catalog;
    private readonly HashSet<WorkbenchFamilyPreview> _controls = [];
    private DispatcherOperation? _update;
    private bool _disposed;

    // Cache identity is per native snapshot, without keeping the managed catalog or its handle alive.
    private static readonly ConditionalWeakTable<FontCatalog, object> CatalogIdentities = new();
    private static readonly SemaphoreSlim RenderSlots = new(2);
    private const int MaxCacheEntries = 256, MaxCacheBytes = 24 * 1024 * 1024;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<LibraryPreviewKey, LinkedListNode<CacheEntry>> Cache = [];
    private static readonly LinkedList<CacheEntry> Recent = new();
    private static int _cacheBytes;
    private sealed record CacheEntry(LibraryPreviewKey Key, Result Preview, int Bytes);
    private sealed record Result(BitmapSource? Image, string? Status, string? Error = null);
    private static readonly Result Missing = new(null, "无可用预览");

    private static readonly Samples CardSamples = new(["永字", "Agj", "Ag", "Aa", "あア", "한글", "Аа", "Αα", "أب", "אב", "अक", "กข", "§★"]);
    private static readonly Samples LineSamples = new(["字体样本", "Hamburgefonts 0123", "Aa Bb Cc", "かなカナ", "한글 견본", "Шрифт", "Δείγμα", "أبجد", "אבגד", "अक्षर", "ตัวอย่าง", "§★"]);
    private sealed record Probe(string Text, uint[] Scalars);
    private sealed class Samples(string[] texts)
    {
        public string CoverageText { get; } = string.Join(" ", texts);
        public Probe[] Probes
        {
            get;
        } = texts.Select(text =>
            new Probe(text, text.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).Select(rune => (uint)rune.Value).ToArray())).ToArray();
    }

    public LibraryPreviewQueue(Window owner, ListBox list, Func<FontCatalog?> catalog)
    {
        (_owner, _list, _catalog) = (owner, list, catalog);
        _list.SetValue(WorkbenchFamilyPreview.QueueProperty, this);
        _list.LayoutUpdated += OnLayoutUpdated;
    }

    internal void Attach(WorkbenchFamilyPreview control)
    {
        if (!_disposed)
        {
            _controls.Add(control);
            Schedule();
        }
    }

    internal void Detach(WorkbenchFamilyPreview control)
    {
        _controls.Remove(control);
        control.ClearPreview();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Schedule();

    internal void Schedule()
    {
        if (!_disposed && _update is null && _controls.Count > 0)
            _update = _list.Dispatcher.InvokeAsync(UpdateVisible, DispatcherPriority.Background);
    }

    internal void Invalidate()
    {
        foreach (var control in _controls)
            control.ClearPreview();
        Schedule();
    }

    private void UpdateVisible()
    {
        _update = null;
        var catalog = _catalog();
        var identity = catalog is null ? null : CatalogIdentities.GetValue(catalog, static _ => new object());
        var ink = WindowsAppearance.Ink(_owner);
        foreach (var control in _controls)
        {
            if (catalog is null || !control.TryGetKey(identity!, ink, out var key))
            {
                control.ClearPreview();
                continue;
            }
            if (control.Key == key)
                continue;

            control.ClearPreview();
            control.Key = key;
            if (ReadCache(key) is { } cached)
            {
                control.Show(cached.Image, cached.Status, cached.Error);
                continue;
            }
            control.Show(null, "预览中…");
            var request = control.Request = new CancellationTokenSource();
            _ = PublishAsync(catalog, control, key, request);
        }
    }

    private async Task PublishAsync(FontCatalog catalog, WorkbenchFamilyPreview control, LibraryPreviewKey key, CancellationTokenSource request)
    {
        using var activity = FontReadActivity.Begin();
        try
        {
            Result result;
            try
            {
                result = await FetchAsync(catalog, key, request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { return; }
            catch (Exception ex) { result = new(null, "无法绘制预览", ex.Message); }

            if (request.IsCancellationRequested || !ReferenceEquals(control.Request, request))
                return;
            if (!ReferenceEquals(catalog, _catalog()) || !control.TryGetKey(key.CatalogIdentity, WindowsAppearance.Ink(_owner), out var current) || current != key)
            {
                control.ClearPreview();
                Schedule();
                return;
            }
            control.Show(result.Image, result.Status, result.Error);
        }
        finally
        {
            if (ReferenceEquals(control.Request, request))
                control.Request = null;
            request.Dispose();
        }
    }

    private static async Task<Result> FetchAsync(FontCatalog catalog, LibraryPreviewKey key, CancellationToken ct)
    {
        await RenderSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ReadCache(key) is { } hit)
                return hit;
            var result = await Task.Run(() => Render(catalog, key, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            WriteCache(key, result);
            return result;
        }
        finally { RenderSlots.Release(); }
    }

    private static Result Render(FontCatalog catalog, LibraryPreviewKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var styles = catalog.GetStyles(key.FamilyId);
        var style = styles.FirstOrDefault(s => s.Weight == 400 && s.Name is "Regular" or "常规" or "Normal") ??
            styles.MinBy(s => Math.Abs((long)s.Weight - 400));
        if (style is null)
            return Missing;
        ct.ThrowIfCancellationRequested();
        using var face = catalog.Select(key.FamilyId, style.Index);
        var samples = key.IsCard ? CardSamples : LineSamples;
        ct.ThrowIfCancellationRequested();
        var missing = face.AnalyzeSample(samples.CoverageText).Missing.Select(s => s.Value).ToHashSet();
        double widthDip = key.Width / key.Scale;
        int maxCardScalars = widthDip >= 104 ? 3 : 2;
        var probe = samples.Probes.FirstOrDefault(p => (!key.IsCard || p.Scalars.Length <= maxCardScalars) &&
            !p.Scalars.Any(missing.Contains));
        return probe is null ? Missing : RenderFitted(face, probe, key, ct);
    }

    private static Result RenderFitted(FaceSelection face, Probe probe, LibraryPreviewKey key, CancellationToken ct)
    {
        const int maxAttempts = 3;
        int padding = (int)MathF.Ceiling(2 * key.Scale);
        int fitWidth = key.Width - 2 * padding, fitHeight = key.Height - 2 * padding;
        if (fitWidth <= 0 || fitHeight <= 0)
            return Missing;

        float advances = key.IsCard ? probe.Scalars.Length : MathF.Max(1, probe.Text.Length * .62f);
        float points = MathF.Min(key.IsCard ? 23 : 18,
            .75f * MathF.Min(fitWidth / (key.Scale * advances), .88f * fitHeight / key.Scale));
        // A blank first line and mapped spaces move the ink away from the native origin. Leave
        // extra room for unusually high baselines and overhangs before fitting the actual ink.
        string text = (face.MapOne(' ') != 0 ? "\n  " : "\n") + probe.Text;
        for (int attempt = 0; attempt < maxAttempts && points >= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            float em = points * (96f / 72f) * key.Scale;
            int canvasWidth = Math.Min(2048, Math.Max(key.Width + (int)MathF.Ceiling(5 * em),
                (int)MathF.Ceiling((advances + 4) * 1.5f * em)));
            int canvasHeight = Math.Min(1280, Math.Max(key.Height + (int)MathF.Ceiling(3 * em), (int)MathF.Ceiling(6 * em)));
            // Empty axis overrides retain the selected face's own default instance.
            var rendered = face.Render(text, points, key.Scale, canvasWidth, canvasHeight, [], ct, key.Ink);
            if (rendered.Error is not null)
                return new(null, "无法绘制预览", rendered.Error);
            if (rendered.Image is not { } image || rendered.Coverage.Missing.Length > 0)
                return Missing;

            if (CropInk(image, fitWidth, fitHeight, padding, ct, out float retryScale) is { } fitted)
                return new(fitted, null);
            points *= retryScale;
        }
        return Missing;
    }

    // Inspect at native resolution. A touched canvas edge is not a safe bounding box, so retry
    // smaller instead of showing cut ink. Cache only a fresh bitmap of the enclosed ink and margin.
    private static BitmapSource? CropInk(BitmapSource image, int fitWidth, int fitHeight, int padding,
        CancellationToken ct, out float retryScale)
    {
        int width = image.PixelWidth, height = image.PixelHeight, stride = width * 4;
        var pixels = ArrayPool<byte>.Shared.Rent(height * stride);
        try
        {
            image.CopyPixels(pixels, stride, 0);
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                if ((y & 31) == 0)
                    ct.ThrowIfCancellationRequested();
                int alpha = y * stride + 3;
                for (int x = 0; x < width; x++, alpha += 4)
                {
                    if (pixels[alpha] == 0)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = y;
                }
            }

            retryScale = .72f;
            if (right < left || left == 0 || top == 0 || right == width - 1 || bottom == height - 1)
                return null;
            int inkWidth = right - left + 1, inkHeight = bottom - top + 1;
            if (inkWidth > fitWidth || inkHeight > fitHeight)
            {
                retryScale = MathF.Min(.95f * MathF.Min((float)fitWidth / inkWidth, (float)fitHeight / inkHeight), .9f);
                return null;
            }

            int croppedWidth = inkWidth + 2 * padding, croppedHeight = inkHeight + 2 * padding;
            int croppedStride = croppedWidth * 4;
            var bytes = new byte[croppedHeight * croppedStride];
            for (int y = 0; y < inkHeight; y++)
                Buffer.BlockCopy(pixels, (top + y) * stride + left * 4,
                    bytes, (y + padding) * croppedStride + padding * 4, inkWidth * 4);
            var cropped = BitmapSource.Create(croppedWidth, croppedHeight, image.DpiX, image.DpiY,
                image.Format, null, bytes, croppedStride);
            cropped.Freeze();
            return cropped;
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
    }

    private static Result? ReadCache(LibraryPreviewKey key)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var node))
                return null;
            Recent.Remove(node);
            Recent.AddFirst(node);
            return node.Value.Preview;
        }
    }

    private static void WriteCache(LibraryPreviewKey key, Result preview)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(key))
                return;
            int bytes = preview.Image is { } image ? image.PixelWidth * image.PixelHeight * 4 : 0;
            Cache.Add(key, Recent.AddFirst(new CacheEntry(key, preview, bytes)));
            _cacheBytes += bytes;
            while (Cache.Count > MaxCacheEntries || _cacheBytes > MaxCacheBytes)
            {
                var tail = Recent.Last!;
                _cacheBytes -= tail.Value.Bytes;
                Cache.Remove(tail.Value.Key);
                Recent.RemoveLast();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _list.LayoutUpdated -= OnLayoutUpdated;
        _list.ClearValue(WorkbenchFamilyPreview.QueueProperty);
        _update?.Abort();
        _update = null;
        Invalidate();
        _controls.Clear();
    }
}
