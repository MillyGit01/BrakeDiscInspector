using System;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI;

internal static class InspectionAlignmentHelper
{
    internal static void MoveInspectionTo(
        RoiModel inspectionTarget,
        RoiModel baselineInspection,
        RoiModel? baselineMaster1,
        RoiModel? baselineMaster2,
        Point master1,
        Point master2)
    {
        GuiLog.Info($"[analyze-master] reposition inspection m1=({master1.X:0.0},{master1.Y:0.0}) m2=({master2.X:0.0},{master2.Y:0.0})");
        if (inspectionTarget == null)
        {
            return;
        }

        if (baselineInspection == null)
        {
            baselineInspection = inspectionTarget.Clone();
        }

        bool transformed = false;

        if (baselineMaster1 != null && baselineMaster2 != null)
        {
            var savedM1Center = GetRoiCenter(baselineMaster1);
            var savedM2Center = GetRoiCenter(baselineMaster2);
            var originalCenter = GetRoiCenter(baselineInspection);

            double dxOld = savedM2Center.X - savedM1Center.X;
            double dyOld = savedM2Center.Y - savedM1Center.Y;
            double dxNew = master2.X - master1.X;
            double dyNew = master2.Y - master1.Y;

            double lenOld = Math.Sqrt(dxOld * dxOld + dyOld * dyOld);
            double lenNew = Math.Sqrt(dxNew * dxNew + dyNew * dyNew);

            if (lenOld > 1e-6 && lenNew > 0)
            {
                double scale = lenNew / lenOld;
                double angleOld = Math.Atan2(dyOld, dxOld);
                double angleNew = Math.Atan2(dyNew, dxNew);
                double angleDelta = angleNew - angleOld;
                double cos = Math.Cos(angleDelta);
                double sin = Math.Sin(angleDelta);

                double relX = originalCenter.X - savedM1Center.X;
                double relY = originalCenter.Y - savedM1Center.Y;

                double rotatedX = scale * (cos * relX - sin * relY);
                double rotatedY = scale * (sin * relX + cos * relY);

                double newCx = master1.X + rotatedX;
                double newCy = master1.Y + rotatedY;

                ApplyShapeTransform(inspectionTarget, baselineInspection, newCx, newCy, scale, angleDelta, false);
                transformed = true;
            }
        }

        if (!transformed)
        {
            var mid = new Point((master1.X + master2.X) / 2.0, (master1.Y + master2.Y) / 2.0);
            ApplyShapeTransform(inspectionTarget, baselineInspection, mid.X, mid.Y, 1.0, 0.0, true);
        }
    }

    private static void ApplyShapeTransform(
        RoiModel target,
        RoiModel baseline,
        double centerX,
        double centerY,
        double scale,
        double angleDelta,
        bool fallback)
    {
        switch (baseline.Shape)
        {
            case RoiShape.Rectangle:
                target.Shape = RoiShape.Rectangle;
                target.X = centerX;
                target.Y = centerY;
                target.Width = Math.Max(1, baseline.Width * scale);
                target.Height = Math.Max(1, baseline.Height * scale);
                double baseAngle = baseline.AngleDeg;
                target.AngleDeg = fallback ? baseAngle : baseAngle + angleDelta * 180.0 / Math.PI;
                break;
            case RoiShape.Circle:
                target.Shape = RoiShape.Circle;
                target.CX = centerX;
                target.CY = centerY;
                target.R = Math.Max(1, baseline.R * scale);
                target.AngleDeg = baseline.AngleDeg;
                break;
            case RoiShape.Annulus:
                target.Shape = RoiShape.Annulus;
                target.CX = centerX;
                target.CY = centerY;
                target.R = Math.Max(1, baseline.R * scale);
                target.RInner = Math.Max(0, baseline.RInner * scale);
                if (target.RInner >= target.R)
                {
                    target.RInner = Math.Max(0, target.R - 1);
                }
                target.AngleDeg = baseline.AngleDeg;
                break;
        }
    }

    private static Point GetRoiCenter(RoiModel roi)
    {
        var (cx, cy) = roi.GetCenter();
        return new Point(cx, cy);
    }
}
