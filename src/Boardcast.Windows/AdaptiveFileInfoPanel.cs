using System.Windows;
using System.Windows.Controls;

namespace Boardcast.Windows;

/// <summary>Uses one line only when the complete file name and metadata fit.</summary>
public sealed class AdaptiveFileInfoPanel : Panel
{
    private const double InlineGap = 12;
    private const double LineGap = 5;
    private bool _inline;
    private double _metadataWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2)
            throw new InvalidOperationException("File information requires a name and a metadata element.");

        var name = InternalChildren[0];
        var metadata = InternalChildren[1];
        var naturalSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
        name.Measure(naturalSize);
        metadata.Measure(naturalSize);
        _metadataWidth = metadata.DesiredSize.Width;
        var inlineWidth = name.DesiredSize.Width + InlineGap + _metadataWidth;
        _inline = inlineWidth <= availableSize.Width;

        if (_inline)
            return new Size(inlineWidth, Math.Max(name.DesiredSize.Height, metadata.DesiredSize.Height));

        var lineSize = new Size(availableSize.Width, double.PositiveInfinity);
        name.Measure(lineSize);
        metadata.Measure(lineSize);
        return new Size(Math.Max(name.DesiredSize.Width, metadata.DesiredSize.Width),
            name.DesiredSize.Height + LineGap + metadata.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var name = InternalChildren[0];
        var metadata = InternalChildren[1];
        if (_inline)
        {
            var metadataWidth = Math.Min(_metadataWidth, finalSize.Width);
            name.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - metadataWidth - InlineGap), finalSize.Height));
            metadata.Arrange(new Rect(finalSize.Width - metadataWidth, 0, metadataWidth, finalSize.Height));
        }
        else
        {
            name.Arrange(new Rect(0, 0, finalSize.Width, name.DesiredSize.Height));
            metadata.Arrange(new Rect(0, name.DesiredSize.Height + LineGap,
                finalSize.Width, metadata.DesiredSize.Height));
        }
        return finalSize;
    }
}