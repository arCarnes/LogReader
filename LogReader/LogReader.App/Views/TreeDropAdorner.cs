namespace LogReader.App.Views;

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using LogReader.App.Models;

public class TreeDropAdorner : Adorner
{
    private static readonly SolidColorBrush LineBrush = new(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush FillBrush;
    private static readonly Pen LinePen = new(LineBrush, 2);

    static TreeDropAdorner()
    {
        LineBrush.Freeze();
        LinePen.Freeze();
        FillBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x3B, 0x82, 0xF6));
        FillBrush.Freeze();
    }

    private Rect _targetBounds;
    private DropPlacement _placement;
    private bool _visible;

    public TreeDropAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    public void Update(Rect targetBounds, DropPlacement placement)
    {
        _targetBounds = targetBounds;
        _placement = placement;
        _visible = true;
        InvalidateVisual();
    }

    public void Hide()
    {
        _visible = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_visible) return;

        if (_placement == DropPlacement.Inside)
        {
            dc.DrawRectangle(FillBrush, LinePen, _targetBounds);
        }
        else
        {
            var y = _placement == DropPlacement.Before
                ? _targetBounds.Top
                : _targetBounds.Bottom;

            var left = _targetBounds.Left;
            var right = _targetBounds.Right;

            dc.DrawEllipse(LineBrush, null, new Point(left + 3, y), 3, 3);
            dc.DrawLine(LinePen, new Point(left + 6, y), new Point(right, y));
        }
    }
}
