using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace FoxBrowser;

public sealed class TabShrinkPanel : System.Windows.Controls.Panel
{
    private const double MinTabWidth = 72;
    private const double MaxTabWidth = 240;
    private const double TabGap = 6;
    private double _horizontalOffset;
    private double _totalWidth;

    public TabShrinkPanel()
    {
        ClipToBounds = true;
        PreviewMouseWheel += OnMouseWheel;
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : Math.Max(0, availableSize.Width);
        var count = InternalChildren.Count;
        var tabWidth = count == 0 ? MaxTabWidth : Math.Clamp((width - Math.Max(0, count - 1) * TabGap) / count, MinTabWidth, MaxTabWidth);
        _totalWidth = count * tabWidth + Math.Max(0, count - 1) * TabGap;
        foreach (UIElement child in InternalChildren)
            child.Measure(new WpfSize(tabWidth, Math.Max(0, availableSize.Height)));
        ClampOffset(width);
        return new WpfSize(width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        var count = InternalChildren.Count;
        var tabWidth = count == 0 ? MaxTabWidth : Math.Clamp((finalSize.Width - Math.Max(0, count - 1) * TabGap) / count, MinTabWidth, MaxTabWidth);
        _totalWidth = count * tabWidth + Math.Max(0, count - 1) * TabGap;
        ClampOffset(finalSize.Width);
        EnsureSelectedVisible(finalSize.Width, tabWidth);
        var x = -_horizontalOffset;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new WpfRect(x, 0, tabWidth, finalSize.Height));
            x += tabWidth + TabGap;
        }
        return finalSize;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_totalWidth <= ActualWidth) return;
        _horizontalOffset = Math.Clamp(_horizontalOffset - e.Delta / 2.0, 0, _totalWidth - ActualWidth);
        InvalidateArrange();
        e.Handled = true;
    }

    private void EnsureSelectedVisible(double availableWidth, double tabWidth)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] is not TabItem { IsSelected: true }) continue;
            var left = i * (tabWidth + TabGap);
            var right = left + tabWidth;
            if (left < _horizontalOffset) _horizontalOffset = left;
            else if (right > _horizontalOffset + availableWidth) _horizontalOffset = right - availableWidth;
            ClampOffset(availableWidth);
            break;
        }
    }

    private void ClampOffset(double availableWidth) => _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, _totalWidth - availableWidth));
}
