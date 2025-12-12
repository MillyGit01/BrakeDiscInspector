using System;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI;

internal static class InspectionAlignmentHelper
{
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

        var angleEffective = anchors.DisableRot ? 0.0 : anchors.AngleDeltaGlobal;
        var scaleEffective = anchors.ScaleLock ? 1.0 : anchors.Scale;

        var cosA = Math.Cos(angleEffective);
        var sinA = Math.Sin(angleEffective);
        var vx = (vBase.X * cosA - vBase.Y * sinA) * scaleEffective;
        var vy = (vBase.X * sinA + vBase.Y * cosA) * scaleEffective;

        var roiNewCenter = new Point2d(pivotCurrent.X + vx, pivotCurrent.Y + vy);
        var roiNewAngleDeg = baselineInspection.AngleDeg + angleEffective * 180.0 / Math.PI;

        trace?.Invoke(
            $"[ROI] name={inspectionTarget.Label} anchor={anchor} " +
            $"baseCenter=({baseCx:F1},{baseCy:F1}) baseAngle={baselineInspection.AngleDeg:F1} " +
            $"newCenter=({roiNewCenter.X:F1},{roiNewCenter.Y:F1}) newAngle={roiNewAngleDeg:F1}");

        switch (baselineInspection.Shape)
        {
            case RoiShape.Rectangle:
                inspectionTarget.Shape = RoiShape.Rectangle;
                inspectionTarget.Width = Math.Max(1, baselineInspection.Width * scaleEffective);
                inspectionTarget.Height = Math.Max(1, baselineInspection.Height * scaleEffective);
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
                inspectionTarget.Width = Math.Max(1, baselineInspection.Width * scaleEffective);
                inspectionTarget.Height = Math.Max(1, baselineInspection.Height * scaleEffective);
                inspectionTarget.Left = roiNewCenter.X - inspectionTarget.Width * 0.5;
                inspectionTarget.Top = roiNewCenter.Y - inspectionTarget.Height * 0.5;
                inspectionTarget.R = Math.Max(1, baselineInspection.R * scaleEffective);
                inspectionTarget.AngleDeg = roiNewAngleDeg;
                break;
            case RoiShape.Annulus:
                inspectionTarget.Shape = RoiShape.Annulus;
                inspectionTarget.CX = roiNewCenter.X;
                inspectionTarget.CY = roiNewCenter.Y;
                inspectionTarget.Width = Math.Max(1, baselineInspection.Width * scaleEffective);
                inspectionTarget.Height = Math.Max(1, baselineInspection.Height * scaleEffective);
                inspectionTarget.Left = roiNewCenter.X - inspectionTarget.Width * 0.5;
                inspectionTarget.Top = roiNewCenter.Y - inspectionTarget.Height * 0.5;
                inspectionTarget.R = Math.Max(1, baselineInspection.R * scaleEffective);
                inspectionTarget.RInner = Math.Max(0, baselineInspection.RInner * scaleEffective);
                if (inspectionTarget.RInner >= inspectionTarget.R)
                {
                    inspectionTarget.RInner = Math.Max(0, inspectionTarget.R - 1);
                }
                inspectionTarget.AngleDeg = roiNewAngleDeg;
                break;
        }
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
