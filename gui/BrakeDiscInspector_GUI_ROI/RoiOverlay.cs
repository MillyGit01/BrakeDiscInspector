using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrakeDiscInspector_GUI_ROI
{
    public class RoiOverlay : FrameworkElement
    {
        // === Transformación imagen <-> pantalla ===
        private Image _boundImage;
        private double _scale = 1.0;
        private double _offX = 0.0, _offY = 0.0;
        private int _imgW = 0, _imgH = 0;

        // Vincula este overlay con la Image real (ImgMain)
        public void BindToImage(Image image)
        {
            _boundImage = image;
            InvalidateOverlay();
        }

        // Fuerza recálculo y repintado
        public void InvalidateOverlay()
        {
            RecomputeImageTransform();
            InvalidateVisual();
        }

        // Recalcular scale/offset asumiendo Stretch=Uniform y letterbox centrado
        private void RecomputeImageTransform()
        {
            if (_boundImage?.Source is System.Windows.Media.Imaging.BitmapSource bmp)
            {
                _imgW = bmp.PixelWidth;
                _imgH = bmp.PixelHeight;
            }
            else
            {
                _imgW = _imgH = 0;
            }

            if (_imgW <= 0 || _imgH <= 0)
            {
                _scale = 1.0;
                _offX = _offY = 0.0;
                return;
            }

            double sw = ActualWidth;
            double sh = ActualHeight;

            if ((sw <= 0 || sh <= 0) && _boundImage != null)
            {
                sw = _boundImage.ActualWidth;
                sh = _boundImage.ActualHeight;
            }

            if (sw <= 0 || sh <= 0)
            {
                _scale = 1.0;
                _offX = _offY = 0.0;
                return;
            }

            _scale = Math.Min(sw / _imgW, sh / _imgH);
            _offX = (sw - _imgW * _scale) * 0.5;
            _offY = (sh - _imgH * _scale) * 0.5;
        }

        // Conversión coordenadas
        public System.Windows.Point ToScreen(double ix, double iy)
        {
            return new System.Windows.Point(_offX + ix * _scale, _offY + iy * _scale);
        }

        public double ToScreenLen(double ilen) => ilen * _scale;

        public System.Windows.Point ToImage(double sx, double sy)
        {
            if (_scale <= 0.0)
                return new System.Windows.Point(0.0, 0.0);

            return new System.Windows.Point((sx - _offX) / _scale, (sy - _offY) / _scale);
        }

        public double ToImageLen(double slen)
        {
            if (_scale <= 0.0)
                return 0.0;
            return slen / _scale;
        }
        public ROI Roi { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            RecomputeImageTransform();

            var roi = Roi;
            if (roi == null)
                return;

            roi.EnforceMinSize(10, 10);

            var dpi = VisualTreeHelper.GetDpi(this);

            System.Windows.Point centerImage = roi.Shape == RoiShape.Rectangle
                ? new System.Windows.Point(roi.X, roi.Y)
                : new System.Windows.Point(roi.CX, roi.CY);

            var centerScreen = ToScreen(centerImage.X, centerImage.Y);

            switch (roi.Shape)
            {
                case RoiShape.Rectangle:
                    DrawRectangle(dc, roi, centerScreen, dpi.PixelsPerDip);
                    break;
                case RoiShape.Circle:
                    DrawCircle(dc, roi, centerScreen, dpi.PixelsPerDip);
                    break;
                case RoiShape.Annulus:
                    DrawAnnulus(dc, roi, centerScreen, dpi.PixelsPerDip);
                    break;
            }
        }

        private static Brush ResolveStrokeFromLegend(ROI roi, Brush fallback)
        {
            var legend = roi?.Legend ?? string.Empty;

            // Use the role embedded in the UI legend to choose consistent colors.
            // Masters are typically "Master 1 Pattern/Search" or "Master 2 Pattern/Search".
            // Inspections often contain "Inspection".
            if (legend.IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0)
                return Brushes.DeepSkyBlue;

            if (legend.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0)
                return Brushes.LimeGreen;

            if (legend.IndexOf("inspection", StringComparison.OrdinalIgnoreCase) >= 0)
                return Brushes.OrangeRed;

            return fallback;
        }

        private void DrawRectangle(DrawingContext dc, ROI roi, System.Windows.Point centerScreen, double pixelsPerDip)
        {
            double widthScreen = ToScreenLen(roi.Width);
            double heightScreen = ToScreenLen(roi.Height);

            var rect = new Rect(centerScreen.X - widthScreen / 2.0, centerScreen.Y - heightScreen / 2.0, widthScreen, heightScreen);
            var rotate = new RotateTransform(roi.AngleDeg, centerScreen.X, centerScreen.Y);

            // IMPORTANT: stroke color and label color must match.
            var stroke = ResolveStrokeFromLegend(roi, Brushes.Lime);
            var pen = new Pen(stroke, 2.0);

            dc.PushTransform(rotate);
            dc.DrawRectangle(null, pen, rect);
            dc.Pop();

            DrawLabel(dc, roi.Legend, rect.TopLeft, pixelsPerDip, stroke);
        }

        private void DrawCircle(DrawingContext dc, ROI roi, System.Windows.Point centerScreen, double pixelsPerDip)
        {
            double radius = roi.R > 0 ? roi.R : Math.Max(roi.Width, roi.Height) / 2.0;
            double radiusScreen = ToScreenLen(radius);

            // IMPORTANT: stroke color and label color must match.
            var stroke = ResolveStrokeFromLegend(roi, Brushes.DeepSkyBlue);
            var pen = new Pen(stroke, 2.0);

            dc.DrawEllipse(null, pen, centerScreen, radiusScreen, radiusScreen);

            var labelAnchor = new System.Windows.Point(centerScreen.X - radiusScreen, centerScreen.Y - radiusScreen);
            DrawLabel(dc, roi.Legend, labelAnchor, pixelsPerDip, stroke);
        }

        private void DrawAnnulus(DrawingContext dc, ROI roi, System.Windows.Point centerScreen, double pixelsPerDip)
        {
            double outerRadius = roi.R > 0 ? roi.R : Math.Max(roi.Width, roi.Height) / 2.0;
            if (outerRadius <= 0)
                outerRadius = Math.Max(roi.Width, roi.Height) / 2.0;

            double ro = ToScreenLen(outerRadius);
            double innerCandidate = roi.RInner;
            double riImage = innerCandidate > 0
                ? AnnulusDefaults.ClampInnerRadius(innerCandidate, outerRadius)
                : AnnulusDefaults.ResolveInnerRadius(innerCandidate, outerRadius);
            double ri = ToScreenLen(riImage);

            // IMPORTANT: stroke color and label color must match.
            var stroke = ResolveStrokeFromLegend(roi, Brushes.DeepSkyBlue);
            var circlePen = new Pen(stroke, 2.0);

            dc.DrawEllipse(null, circlePen, centerScreen, ro, ro);
            dc.DrawEllipse(null, circlePen, centerScreen, ri, ri);

            // Keep the dashed pen as-is (visual cue).
            var dashedPen = new Pen(Brushes.OrangeRed, 1.5)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
            };
            dc.DrawEllipse(null, dashedPen, centerScreen, ro, ro);

            var label = string.IsNullOrWhiteSpace(roi.Legend) ? "Annulus" : roi.Legend;
            var ft = new FormattedText(
                label,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                stroke,
                pixelsPerDip);

            var labelPos = new System.Windows.Point(centerScreen.X - ft.Width / 2.0, centerScreen.Y - ro - ft.Height - 6);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null,
                new Rect(labelPos.X - 4, labelPos.Y - 2, ft.Width + 8, ft.Height + 4));
            dc.DrawText(ft, labelPos);
        }

        private void DrawLabel(DrawingContext dc, string legend, System.Windows.Point anchor, double pixelsPerDip, Brush brush)
        {
            if (string.IsNullOrWhiteSpace(legend))
                return;

            var ft = new FormattedText(
                legend,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                brush,
                pixelsPerDip);

            var labelPos = new System.Windows.Point(anchor.X, anchor.Y + 2);
            dc.DrawText(ft, labelPos);
        }


    }
}
