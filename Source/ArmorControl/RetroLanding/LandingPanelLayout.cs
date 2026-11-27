using System;

namespace KspRetroLanding
{
    internal readonly struct LandingPanelLayout
    {
        internal const float PreferredWidth = 560, PreferredHeight = 640;
        private const float Margin = 16, MinimumPlot = 168, MaximumPlot = 276, Readouts = 156, ColumnGap = 12;

        internal readonly float Width, Height, ContentWidth, PlotSize, ReadoutWidth;
        internal readonly bool StackEngineBay, StackSettings;

        internal LandingPanelLayout(float screenWidth, float screenHeight, float horizontalPadding, float scrollbarWidth)
        {
            Width = Math.Min(PreferredWidth, Math.Max(1, screenWidth - Margin));
            Height = Math.Min(PreferredHeight, Math.Max(1, screenHeight - Margin));
            // Leave space for the vertical scrollbar even when the content
            // does not yet overflow. Text must fit either way.
            ContentWidth = Math.Max(1, Width - horizontalPadding - Math.Max(24, scrollbarWidth) - 8);
            StackEngineBay = ContentWidth < MinimumPlot + Readouts + ColumnGap;
            StackSettings = ContentWidth < 350;
            float availablePlot = StackEngineBay ? ContentWidth : ContentWidth - Readouts - ColumnGap;
            float preferredPlot = Math.Max(MinimumPlot, Math.Min(MaximumPlot, Height - 180));
            PlotSize = Math.Min(availablePlot, preferredPlot);
            ReadoutWidth = StackEngineBay ? ContentWidth : Readouts;
        }
    }
}
