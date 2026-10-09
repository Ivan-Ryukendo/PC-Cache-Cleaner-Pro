using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CleanPC.Pro.Ui
{
    /// <summary>Minimal line chart of free space (GB) over time, one line per drive. Pure rendering, theme-aware, scales with DPI.</summary>
    public class SpaceChart : FrameworkElement
    {
        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register("Series", typeof(IEnumerable<ChartSeries>), typeof(SpaceChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public IEnumerable<ChartSeries> Series { get { return (IEnumerable<ChartSeries>)GetValue(SeriesProperty); } set { SetValue(SeriesProperty, value); } }

        Brush Res(string key, Brush fallback) { var b = TryFindResource(key) as Brush; return b ?? fallback; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            var bg = Res("SurfaceBrush", Brushes.White); var text = Res("TextDimBrush", Brushes.Gray); var grid = Res("BorderBrushX", Brushes.LightGray);
            dc.DrawRectangle(bg, new Pen(grid, 1), new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));
            var series = (Series ?? Enumerable.Empty<ChartSeries>()).Where(s => s.Points != null && s.Points.Count > 0).ToList();
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Func<string, FormattedText> ft = s => new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, dpi);
            if (series.Count == 0 || w < 120 || h < 80)
            {
                var t = ft("No samples yet - free space is recorded each time the program starts or cleans.");
                t.MaxTextWidth = Math.Max(10, w - 20);
                dc.DrawText(t, new Point(10, 10)); return;
            }
            DateTime t0 = series.Min(s => s.Points.Min(p => p.Key)), t1 = series.Max(s => s.Points.Max(p => p.Key));
            double ymax = series.Max(s => s.Points.Max(p => p.Value)) * 1.08; if (ymax < 1) ymax = 1;
            double left = 52, right = 14, top = 12, bottom = 26;
            double pw = w - left - right, ph = h - top - bottom;
            double span = (t1 - t0).TotalSeconds; if (span < 1) span = 1;
            var gp = new Pen(grid, 1) { DashStyle = DashStyles.Dot };
            for (int i = 0; i <= 4; i++)
            {
                double y = top + ph - ph * i / 4.0;
                dc.DrawLine(gp, new Point(left, y), new Point(w - right, y));
                var lab = ft((ymax * i / 4.0).ToString("N0") + " GB");
                dc.DrawText(lab, new Point(left - lab.Width - 6, y - lab.Height / 2));
            }
            var l0 = ft(t0.ToString("yyyy-MM-dd")); dc.DrawText(l0, new Point(left, h - bottom + 6));
            var l1 = ft(t1.ToString("yyyy-MM-dd")); dc.DrawText(l1, new Point(w - right - l1.Width, h - bottom + 6));
            foreach (var s in series)
            {
                var pen = new Pen(s.Color, 2) { LineJoin = PenLineJoin.Round };
                Point? prev = null;
                foreach (var p in s.Points)
                {
                    double x = left + (span <= 1 ? pw / 2 : pw * (p.Key - t0).TotalSeconds / span);
                    double y = top + ph - ph * (p.Value / ymax);
                    var pt = new Point(x, y);
                    if (prev.HasValue) dc.DrawLine(pen, prev.Value, pt);
                    dc.DrawEllipse(s.Color, null, pt, 3, 3);
                    prev = pt;
                }
            }
        }
    }
}
