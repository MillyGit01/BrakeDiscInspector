using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public readonly record struct ImgPoint(double X, double Y)
    {
        public static ImgPoint operator +(ImgPoint a, ImgPoint b) => new(a.X + b.X, a.Y + b.Y);
        public static ImgPoint operator -(ImgPoint a, ImgPoint b) => new(a.X - b.X, a.Y - b.Y);
        public static ImgPoint operator *(ImgPoint a, double s) => new(a.X * s, a.Y * s);
    }

    public enum ScaleMode
    {
        None,
        OffsetOnly
    }

    public readonly record struct RoiPlacementInput(
        ImgPoint BaseM1,
        ImgPoint BaseM2,
        ImgPoint DetM1,
        ImgPoint DetM2,
        bool DisableRot,
        bool ScaleLock,
        ScaleMode ScaleMode,
        bool UseMidAnchorFallback,
        IReadOnlyDictionary<string, MasterAnchorChoice>? AnchorByRoiId)
    {
        public string? ImageKey { get; init; }
    }

    public sealed record PlacementRoiDebug(
        string RoiId,
        MasterAnchorChoice Anchor,
        ImgPoint BaselineCenter,
        ImgPoint NewCenter,
        ImgPoint Delta,
        double BaseWidth,
        double BaseHeight,
        double BaseR,
        double BaseRInner,
        double NewWidth,
        double NewHeight,
        double NewR,
        double NewRInner,
        double AngleBase,
        double AngleNew);

    public sealed record PlacementDebug(
        double DistBase,
        double DistDet,
        double Scale,
        double AngleDeltaDeg,
        ImgPoint DeltaM1,
        ImgPoint DeltaM2,
        ImgPoint DeltaMid,
        IReadOnlyList<PlacementRoiDebug> RoiDetails);

    public sealed record RoiPlacementOutput(
        List<RoiModel> MastersPlaced,
        List<RoiModel> InspectionsPlaced,
        PlacementDebug Debug);

    public static class RoiPlacementEngine
    {
        public static RoiPlacementOutput Place(
            RoiPlacementInput input,
            IReadOnlyList<RoiModel> baselineMasters,
            IReadOnlyList<RoiModel> baselineInspections)
        {
            var baseMid = new ImgPoint(
                (input.BaseM1.X + input.BaseM2.X) * 0.5,
                (input.BaseM1.Y + input.BaseM2.Y) * 0.5);
            var detMid = new ImgPoint(
                (input.DetM1.X + input.DetM2.X) * 0.5,
                (input.DetM1.Y + input.DetM2.Y) * 0.5);

            var deltaM1 = input.DetM1 - input.BaseM1;
            var deltaM2 = input.DetM2 - input.BaseM2;
            var deltaMid = detMid - baseMid;

            var baseVec = input.BaseM2 - input.BaseM1;
            var detVec = input.DetM2 - input.DetM1;
            var angBase = Math.Atan2(baseVec.Y, baseVec.X);
            var angDet = Math.Atan2(detVec.Y, detVec.X);
            var angDelta = NormalizeAngleRad(angDet - angBase);
            var angDeltaDeg = angDelta * 180.0 / Math.PI;

            var distBase = Hypot(baseVec);
            var distDet = Hypot(detVec);
            var scale = distBase > 1e-9 ? distDet / distBase : 1.0;
            var translateOnly = input.DisableRot && input.ScaleLock;

            var mastersPlaced = new List<RoiModel>();
            if (baselineMasters != null)
            {
                foreach (var baseline in baselineMasters.Where(r => r != null))
                {
                    var placed = baseline.Clone();
                    var target = ResolveMasterCenter(baseline.Role, input);
                    if (target.HasValue)
                    {
                        ApplyCenter(placed, target.Value);
                    }

                    mastersPlaced.Add(placed);
                }
            }

            var inspectionPlaced = new List<RoiModel>();
            var debugEntries = new List<PlacementRoiDebug>();
            if (baselineInspections != null)
            {
                foreach (var baseline in baselineInspections.Where(r => r != null))
                {
                    var placed = baseline.Clone();
                    var anchor = ResolveAnchorChoice(input, baseline);
                    var pivotBase = ResolvePivot(anchor, input.BaseM1, input.BaseM2, baseMid);
                    var pivotDet = ResolvePivot(anchor, input.DetM1, input.DetM2, detMid);

                    var baselineCenter = GetCenter(baseline);
                    ImgPoint newCenter;
                    double newAngle = baseline.AngleDeg;

                    if (translateOnly || input.DisableRot)
                    {
                        var delta = anchor switch
                        {
                            MasterAnchorChoice.Master1 => deltaM1,
                            MasterAnchorChoice.Master2 => deltaM2,
                            MasterAnchorChoice.Mid => deltaMid,
                            _ => deltaMid
                        };
                        newCenter = baselineCenter + delta;
                    }
                    else
                    {
                        var vBase = baselineCenter - pivotBase;
                        var vRot = Rotate(vBase, angDelta);
                        newCenter = pivotDet + vRot;
                        newAngle = baseline.AngleDeg + angDeltaDeg;
                    }

                    ApplyCenter(placed, newCenter);
                    placed.AngleDeg = newAngle;

                    var deltaCenter = new ImgPoint(newCenter.X - baselineCenter.X, newCenter.Y - baselineCenter.Y);

                    inspectionPlaced.Add(placed);
                    debugEntries.Add(new PlacementRoiDebug(
                        baseline.Id ?? string.Empty,
                        anchor,
                        baselineCenter,
                        newCenter,
                        deltaCenter,
                        baseline.Width,
                        baseline.Height,
                        baseline.R,
                        baseline.RInner,
                        placed.Width,
                        placed.Height,
                        placed.R,
                        placed.RInner,
                        baseline.AngleDeg,
                        newAngle));
                }
            }

            var debug = new PlacementDebug(
                distBase,
                distDet,
                scale,
                angDeltaDeg,
                deltaM1,
                deltaM2,
                deltaMid,
                debugEntries);

            LogPlacement(input, translateOnly, debug);

            return new RoiPlacementOutput(mastersPlaced, inspectionPlaced, debug);
        }

        private static MasterAnchorChoice ResolveAnchorChoice(RoiPlacementInput input, RoiModel roi)
        {
            var roiId = roi.Id ?? string.Empty;
            if (input.AnchorByRoiId != null && !string.IsNullOrWhiteSpace(roiId)
                && input.AnchorByRoiId.TryGetValue(roiId, out var anchor))
            {
                return anchor;
            }

            return input.UseMidAnchorFallback ? MasterAnchorChoice.Mid : MasterAnchorChoice.Master1;
        }

        private static ImgPoint ResolvePivot(MasterAnchorChoice anchor, ImgPoint m1, ImgPoint m2, ImgPoint mid)
        {
            return anchor switch
            {
                MasterAnchorChoice.Master1 => m1,
                MasterAnchorChoice.Master2 => m2,
                MasterAnchorChoice.Mid => mid,
                _ => mid
            };
        }

        private static ImgPoint? ResolveMasterCenter(RoiRole role, RoiPlacementInput input)
        {
            return role switch
            {
                RoiRole.Master1Pattern => input.DetM1,
                RoiRole.Master2Pattern => input.DetM2,
                _ => null
            };
        }

        private static ImgPoint GetCenter(RoiModel roi)
        {
            var (cx, cy) = roi.GetCenter();
            return new ImgPoint(cx, cy);
        }

        private static void ApplyCenter(RoiModel roi, ImgPoint center)
        {
            roi.X = center.X;
            roi.Y = center.Y;
            roi.CX = center.X;
            roi.CY = center.Y;
            roi.Left = center.X - roi.Width * 0.5;
            roi.Top = center.Y - roi.Height * 0.5;
        }

        private static ImgPoint Rotate(ImgPoint v, double angleRad)
        {
            var cos = Math.Cos(angleRad);
            var sin = Math.Sin(angleRad);
            return new ImgPoint(
                v.X * cos - v.Y * sin,
                v.X * sin + v.Y * cos);
        }

        private static double Hypot(ImgPoint v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

        private static double NormalizeAngleRad(double rad)
        {
            var normalized = (rad + Math.PI) % (2.0 * Math.PI);
            if (normalized < 0)
            {
                normalized += 2.0 * Math.PI;
            }

            return normalized - Math.PI;
        }

        private static void LogPlacement(RoiPlacementInput input, bool translateOnly, PlacementDebug debug)
        {
            var summary =
                $"[PLACE][SUMMARY] imageKey='{input.ImageKey ?? "<none>"}' disableRot={input.DisableRot} scaleLock={input.ScaleLock} " +
                $"translateOnly={translateOnly} baseM1=({input.BaseM1.X:0.###},{input.BaseM1.Y:0.###}) " +
                $"baseM2=({input.BaseM2.X:0.###},{input.BaseM2.Y:0.###}) " +
                $"detM1=({input.DetM1.X:0.###},{input.DetM1.Y:0.###}) " +
                $"detM2=({input.DetM2.X:0.###},{input.DetM2.Y:0.###})";
            Util.GuiLog.Info(summary);

            var deltas =
                $"[PLACE][DELTAS] dM1=({debug.DeltaM1.X:0.###},{debug.DeltaM1.Y:0.###}) " +
                $"dM2=({debug.DeltaM2.X:0.###},{debug.DeltaM2.Y:0.###}) " +
                $"dMid=({debug.DeltaMid.X:0.###},{debug.DeltaMid.Y:0.###}) " +
                $"angDeltaDeg={debug.AngleDeltaDeg:0.###} scale={debug.Scale:0.####}";
            Util.GuiLog.Info(deltas);

            foreach (var detail in debug.RoiDetails)
            {
                var roiDetail =
                    $"[PLACE][ROI] id={detail.RoiId} anchor={detail.Anchor} baseC=({detail.BaselineCenter.X:0.###},{detail.BaselineCenter.Y:0.###}) " +
                    $"newC=({detail.NewCenter.X:0.###},{detail.NewCenter.Y:0.###}) dxdy=({detail.Delta.X:0.###},{detail.Delta.Y:0.###}) " +
                    $"baseSize=({detail.BaseWidth:0.###},{detail.BaseHeight:0.###},{detail.BaseR:0.###},{detail.BaseRInner:0.###}) " +
                    $"newSize=({detail.NewWidth:0.###},{detail.NewHeight:0.###},{detail.NewR:0.###},{detail.NewRInner:0.###}) " +
                    $"baseAng={detail.AngleBase:0.###} newAng={detail.AngleNew:0.###}";
                Util.GuiLog.Info(roiDetail);
            }
        }
    }
}
