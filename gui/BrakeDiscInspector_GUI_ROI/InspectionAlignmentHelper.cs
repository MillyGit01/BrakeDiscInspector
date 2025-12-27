using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI;

internal static class InspectionAlignmentHelper
{
    private static void LogAlign(Action<string>? trace, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var payload = "[ALIGN]" + message;
        GuiLog.Info(payload);
        trace?.Invoke(payload);
    }

    private static void LogAlign(Action<string>? trace, FormattableString message)
    {
        if (message == null)
        {
            return;
        }

        LogAlign(trace, message.ToString(CultureInfo.InvariantCulture));
    }

    internal static (double Tx, double Ty) ComputeTranslation(
        Point2d pivotBaseline,
        Point2d pivotDetected,
        double angleRad,
        double scale)
    {
        var cosA = Math.Cos(angleRad);
        var sinA = Math.Sin(angleRad);
        var tx = pivotDetected.X - scale * (pivotBaseline.X * cosA - pivotBaseline.Y * sinA);
        var ty = pivotDetected.Y - scale * (pivotBaseline.X * sinA + pivotBaseline.Y * cosA);
        return (tx, ty);
    }

    internal static void MoveInspectionTo(
        RoiModel inspectionTarget,
        RoiModel baselineInspection,
        MasterAnchorChoice anchor,
        AnchorTransformContext anchors,
        Action<string>? trace = null)
    {
        if (inspectionTarget == null || baselineInspection == null)
        {
            return;
        }

        var pivotBaseline = anchor == MasterAnchorChoice.Master1
            ? anchors.M1BaselineCenter
            : anchors.M2BaselineCenter;
        var pivotCurrent = anchor == MasterAnchorChoice.Master1
            ? anchors.M1DetectedCenter
            : anchors.M2DetectedCenter;

        var (baseCx, baseCy) = baselineInspection.GetCenter();
        var vBase = new Point2d(baseCx - pivotBaseline.X, baseCy - pivotBaseline.Y);

        var rotForCenter = anchors.AngleDeltaGlobal;
        var rotForAngle = anchors.DisableRot ? 0.0 : anchors.AngleDeltaGlobal;

        // ROI scaling is disabled globally: ROIs must keep fixed size/offset across images.
        // Keep anchors.Scale only for logging/diagnostics.
        var scaleRequested = anchors.Scale;
        var scaleEffective = anchors.ScaleLock ? 1.0 : anchors.Scale;
        var angleDeltaDeg = anchors.AngleDeltaGlobal * 180.0 / Math.PI;

        var cosA = Math.Cos(rotForCenter);
        var sinA = Math.Sin(rotForCenter);
        var vx = (vBase.X * cosA - vBase.Y * sinA) * scaleEffective;
        var vy = (vBase.X * sinA + vBase.Y * cosA) * scaleEffective;

        var roiNewCenter = new Point2d(pivotCurrent.X + vx, pivotCurrent.Y + vy);
        var roiNewAngleDeg = baselineInspection.AngleDeg + rotForAngle * 180.0 / Math.PI;

        var (tx, ty) = ComputeTranslation(pivotBaseline, pivotCurrent, rotForCenter, scaleEffective);
        var widthScaled = Math.Max(1, baselineInspection.Width * scaleEffective);
        var heightScaled = Math.Max(1, baselineInspection.Height * scaleEffective);
        var radiusScaled = Math.Max(1, baselineInspection.R * scaleEffective);
        var radiusInnerScaled = Math.Max(0, baselineInspection.RInner * scaleEffective);
        if (radiusInnerScaled >= radiusScaled)
        {
            radiusInnerScaled = Math.Max(0, radiusScaled - 1);
        }

        var finalRect = new System.Windows.Rect(
            roiNewCenter.X - widthScaled * 0.5,
            roiNewCenter.Y - heightScaled * 0.5,
            widthScaled,
            heightScaled);

        LogAlign(trace,
            FormattableStringFactory.Create(
                "[BATCH][ROI] roi={0} anchor_master={1} pivot_base=({2:0.###},{3:0.###}) pivot_det=({4:0.###},{5:0.###})",
                inspectionTarget.Label ?? inspectionTarget.Id,
                (int)anchor,
                pivotBaseline.X,
                pivotBaseline.Y,
                pivotCurrent.X,
                pivotCurrent.Y));
        LogAlign(trace,
            FormattableStringFactory.Create(
                "[BATCH][ROI] roi={0} roi_base_center=({1:0.###},{2:0.###}) v_base=({3:0.###},{4:0.###})",
                inspectionTarget.Label ?? inspectionTarget.Id,
                baseCx,
                baseCy,
                vBase.X,
                vBase.Y));
        LogAlign(trace,
            FormattableStringFactory.Create(
                "[BATCH][ROI] roi={0} center_delta=({1:0.###},{2:0.###})",
                inspectionTarget.Label ?? inspectionTarget.Id,
                roiNewCenter.X - baseCx,
                roiNewCenter.Y - baseCy));
        LogAlign(trace,
            FormattableStringFactory.Create(
                "[BATCH][ROI] roi={0} applied rot_deg_center={1:0.###} rot_deg_angle={2:0.###} scale_req={3:0.####} scale_applied={4:0.####} tx={5:0.###} ty={6:0.###} scaleLock={7} disableRot={8}",
                inspectionTarget.Label ?? inspectionTarget.Id,
                rotForCenter * 180.0 / Math.PI,
                rotForAngle * 180.0 / Math.PI,
                scaleRequested,
                scaleEffective,
                tx,
                ty,
                anchors.ScaleLock,
                anchors.DisableRot));
        LogAlign(trace,
            FormattableStringFactory.Create(
                "[BATCH][ROI] roi={0} angle_delta_deg={1:0.###} scale_req={2:0.####} scale_eff={3:0.####} scaleLock={4} disableRot={5}",
                inspectionTarget.Label ?? inspectionTarget.Id,
                angleDeltaDeg,
                scaleRequested,
                scaleEffective,
                anchors.ScaleLock,
                anchors.DisableRot));

        switch (baselineInspection.Shape)
        {
            case RoiShape.Rectangle:
                inspectionTarget.Shape = RoiShape.Rectangle;
                inspectionTarget.Width = widthScaled;
                inspectionTarget.Height = heightScaled;
                inspectionTarget.X = roiNewCenter.X;
                inspectionTarget.Y = roiNewCenter.Y;
                inspectionTarget.Left = roiNewCenter.X - inspectionTarget.Width * 0.5;
                inspectionTarget.Top = roiNewCenter.Y - inspectionTarget.Height * 0.5;
                inspectionTarget.CX = roiNewCenter.X;
                inspectionTarget.CY = roiNewCenter.Y;
                inspectionTarget.AngleDeg = roiNewAngleDeg;
                break;
            case RoiShape.Circle:
                inspectionTarget.Shape = RoiShape.Circle;
                inspectionTarget.CX = roiNewCenter.X;
                inspectionTarget.CY = roiNewCenter.Y;
                inspectionTarget.Width = widthScaled;
                inspectionTarget.Height = heightScaled;
                inspectionTarget.Left = roiNewCenter.X - inspectionTarget.Width * 0.5;
                inspectionTarget.Top = roiNewCenter.Y - inspectionTarget.Height * 0.5;
                inspectionTarget.R = radiusScaled;
                inspectionTarget.AngleDeg = roiNewAngleDeg;
                break;
            case RoiShape.Annulus:
                inspectionTarget.Shape = RoiShape.Annulus;
                inspectionTarget.CX = roiNewCenter.X;
                inspectionTarget.CY = roiNewCenter.Y;
                inspectionTarget.Width = widthScaled;
                inspectionTarget.Height = heightScaled;
                inspectionTarget.Left = roiNewCenter.X - inspectionTarget.Width * 0.5;
                inspectionTarget.Top = roiNewCenter.Y - inspectionTarget.Height * 0.5;
                inspectionTarget.R = radiusScaled;
                inspectionTarget.RInner = radiusInnerScaled;
                inspectionTarget.AngleDeg = roiNewAngleDeg;
                break;
        }

        LogAlign(trace,
            FormattableStringFactory.Create(
                "[BATCH][ROI] roi={0} roi_new_center=({1:0.###},{2:0.###}) roi_new_angle={3:0.###} roi_new_rect=({4:0.###},{5:0.###},{6:0.###},{7:0.###})",
                inspectionTarget.Label ?? inspectionTarget.Id,
                roiNewCenter.X,
                roiNewCenter.Y,
                roiNewAngleDeg,
                finalRect.Left,
                finalRect.Top,
                finalRect.Right,
                finalRect.Bottom));
    }
}

internal readonly record struct AnchorTransformContext(
    Point2d M1BaselineCenter,
    Point2d M2BaselineCenter,
    Point2d M1DetectedCenter,
    Point2d M2DetectedCenter,
    double M1BaselineAngleDeg,
    double M2BaselineAngleDeg,
    double M1DetectedAngleDeg,
    double M2DetectedAngleDeg,
    double Scale,
    double AngleDeltaGlobal,
    bool ScaleLock,
    bool DisableRot);
