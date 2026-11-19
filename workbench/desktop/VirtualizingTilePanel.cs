using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FontBook.Workbench;

// Uniform cells make both scrolling and item-to-row mapping independent of how many containers exist.
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    private const double MinimumTileWidth = 112;
    private const double TileHeight = 108;
    private const double LineStep = 24;
    private Size _viewport;
    private double _extentHeight, _offset, _arrangedOffset;
    private int _columns = 1, _bringIndex = -1;

    public ScrollViewer? ScrollOwner
    {
        get; set;
    }
    public bool CanHorizontallyScroll
    {
        get; set;
    }
    public bool CanVerticallyScroll
    {
        get; set;
    }
    public double ExtentWidth => _viewport.Width;
    public double ExtentHeight => _extentHeight;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null)
            return default;

        var viewport = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : MinimumTileWidth,
            double.IsFinite(availableSize.Height) ? availableSize.Height : TileHeight);
        int count = owner.Items.Count;
        int columns = Math.Max(1, (int)(viewport.Width / MinimumTileWidth));
        int rows = count == 0 ? 0 : (count - 1) / columns + 1;
        double extent = rows * TileHeight;
        double offset = _offset;
        if (columns != _columns && count > 0)
        {
            // Keep the previous top item on resize, prioritizing the primary selection if it was visible.
            int first = Math.Min((int)(_offset / TileHeight) * _columns, count - 1);
            offset = first / columns * TileHeight + _offset % TileHeight;
            if (owner is Selector { SelectedIndex: >= 0 } selector)
            {
                double oldTop = selector.SelectedIndex / _columns * TileHeight;
                if (oldTop < _offset + _viewport.Height && oldTop + TileHeight > _offset)
                    offset = Reveal(offset, viewport.Height, selector.SelectedIndex / columns * TileHeight, TileHeight);
            }
        }
        if ((uint)_bringIndex < count)
            offset = Reveal(offset, viewport.Height, _bringIndex / columns * TileHeight, TileHeight);
        _bringIndex = -1;
        offset = Math.Clamp(offset, 0, Math.Max(0, extent - viewport.Height));
        bool scrollChanged = _viewport != viewport || _extentHeight != extent || _offset != offset;
        (_viewport, _extentHeight, _offset, _columns) = (viewport, extent, offset, columns);
        if (scrollChanged)
            ScrollOwner?.InvalidateScrollInfo();

        // One row of slack on each side; previews independently check the actual viewport.
        int firstRow = Math.Max(0, (int)(offset / TileHeight) - 1);
        int lastRowExclusive = Math.Min(rows, (int)Math.Ceiling((offset + viewport.Height) / TileHeight) + 1);
        int firstIndex = firstRow * columns;
        int endIndex = Math.Min(count, lastRowExclusive * columns);
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var recycler = GetVirtualizationMode(owner) == VirtualizationMode.Recycling
            ? generator as IRecyclingItemContainerGenerator : null;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if ((index >= firstIndex && index < endIndex) || children[i].IsKeyboardFocusWithin)
                continue;
            if (recycler is null)
                generator.Remove(position, 1);
            else
                recycler.Recycle(position, 1);
            RemoveInternalChildRange(i, 1);
        }
        if (firstIndex < endIndex)
        {
            var size = new Size(viewport.Width / columns, TileHeight);
            var start = generator.GeneratorPositionFromIndex(firstIndex);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int index = firstIndex; index < endIndex; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out _);
                    // A recycled container can be returned as already realized, but is no longer a visual child.
                    if (childIndex >= children.Count || !ReferenceEquals(children[childIndex], child))
                    {
                        InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(size);
                }
            }
        }
        return new Size(viewport.Width, Math.Min(viewport.Height, extent));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = finalSize.Width / _columns;
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % _columns * width, index / _columns * TileHeight - _offset, width, TileHeight));
        }
        _arrangedOffset = _offset;
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        _bringIndex = -1;
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                SetVerticalOffset(0);
                break;
            case NotifyCollectionChangedAction.Remove when args.ItemUICount > 0:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move when args.ItemUICount > 0:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Replace when args.ItemUICount > 0:
                // The generator may already have linked the replacement container at this position.
                var generator = ItemContainerGenerator;
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                using (generator.StartAt(args.Position, GeneratorDirection.Forward, true))
                {
                    for (int i = 0; i < args.ItemUICount; i++)
                    {
                        var child = (UIElement)generator.GenerateNext(out _);
                        InsertInternalChild(args.Position.Index + i, child);
                        generator.PrepareItemContainer(child);
                    }
                }
                break;
        }
    }

    protected override void BringIndexIntoView(int index)
    {
        if (ItemsControl.GetItemsOwner(this) is { } owner && (uint)index < owner.Items.Count)
        {
            _bringIndex = index;
            InvalidateMeasure();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual == this || !IsAncestorOf(visual))
            return Rect.Empty;

        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        double top = bounds.Top + _arrangedOffset;
        SetVerticalOffset(Reveal(_offset, ViewportHeight, top, bounds.Height));
        bounds.Y = top - _offset;
        return Rect.Intersect(bounds, new Rect(_viewport));
    }

    private static double Reveal(double offset, double viewportHeight, double top, double height)
    {
        if (height >= viewportHeight || top < offset)
            return top;
        return Math.Max(offset, top + height - viewportHeight);
    }

    internal int NavigationIndex(int current, int count, Key key)
    {
        if (key == Key.End)
            return count - 1;
        if (key == Key.Home || current < 0)
            return 0;

        int column = current % _columns;
        int page = Math.Max(1, (int)(ViewportHeight / TileHeight)) * _columns;
        return key switch
        {
            Key.Left when column > 0 => current - 1,
            Key.Right when column < _columns - 1 => Math.Min(count - 1, current + 1),
            Key.Up when current >= _columns => current - _columns,
            Key.Down when current / _columns < (count - 1) / _columns => Math.Min(count - 1, current + _columns),
            Key.PageUp => Math.Max(0, current - page),
            Key.PageDown => Math.Min(count - 1, current + page),
            _ => current,
        };
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        _bringIndex = -1;
        offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (_offset == offset)
            return;
        _offset = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineUp() => SetVerticalOffset(_offset - LineStep);
    public void LineDown() => SetVerticalOffset(_offset + LineStep);
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    public void MouseWheelUp() => Wheel(-1);
    public void MouseWheelDown() => Wheel(1);
    private void Wheel(int direction) => SetVerticalOffset(_offset + direction *
        (SystemParameters.WheelScrollLines < 0 ? ViewportHeight : SystemParameters.WheelScrollLines * LineStep));

    // The grid always fits its viewport horizontally.
    public void SetHorizontalOffset(double offset)
    {
    }
    public void LineLeft()
    {
    }
    public void LineRight()
    {
    }
    public void PageLeft()
    {
    }
    public void PageRight()
    {
    }
    public void MouseWheelLeft()
    {
    }
    public void MouseWheelRight()
    {
    }
}
