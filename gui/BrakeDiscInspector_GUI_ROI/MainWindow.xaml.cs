// Dialogs
using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using BrakeDiscInspector_GUI_ROI.LayoutManagement;
using BrakeDiscInspector_GUI_ROI.Workflow;
using BrakeDiscInspector_GUI_ROI.Overlay;
using BrakeDiscInspector_GUI_ROI.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
// WPF media & shapes
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
// OpenCV alias
using Cv = OpenCvSharp;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WBrush = System.Windows.Media.Brush;
using WBrushes = System.Windows.Media.Brushes;
using WColor = System.Windows.Media.Color;
using WEllipse = System.Windows.Shapes.Ellipse;
using WLine = System.Windows.Shapes.Line;
// Aliases WPF
using WRectShape = System.Windows.Shapes.Rectangle;
using Path = System.IO.Path;
using WSize = System.Windows.Size;
using LegacyROI = BrakeDiscInspector_GUI_ROI.ROI;
using ROI = BrakeDiscInspector_GUI_ROI.RoiModel;
using RoiShapeType = BrakeDiscInspector_GUI_ROI.RoiShape;
using BrakeDiscInspector_GUI_ROI.Models;
// --- BEGIN: UI/OCV type aliases ---
using SW = System.Windows;
using SWM = System.Windows.Media;
using SWShapes = System.Windows.Shapes;
using SWPoint = System.Windows.Point;
using SWRect = System.Windows.Rect;
using SWVector = System.Windows.Vector;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect; // CODEX: alias for OpenCV rectangles.
using WRect = System.Windows.Rect; // CODEX: alias for WPF rectangles.
using WPoint = System.Windows.Point; // CODEX: alias for WPF points.
using WInt32Rect = System.Windows.Int32Rect; // CODEX: alias for WPF Int32Rect.
// --- END: UI/OCV type aliases ---

namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged, ILayoutHost
    {
        static MainWindow()
        {
            if (RoiHudAccentBrush.CanFreeze && !RoiHudAccentBrush.IsFrozen)
            {
                RoiHudAccentBrush.Freeze();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private int _freezeRoiRepositionCounter;
        private bool _pendingActiveInspectionSync;
        private bool _globalUnlocked = false;
        private bool _editingM1;
        private bool _editingM2;
        private RoiRole? _activeMaster1Role;
        private RoiRole? _activeMaster2Role;
        private bool _editingInspection1;
        private bool _editingInspection2;
        private bool _editingInspection3;
        private bool _editingInspection4;
        private int? _editingInspectionSlot;
        private bool IsEditingInspection => _editingInspectionSlot.HasValue;
        private bool _editModeActive = false;
        private string? _activeEditableRoiId = null;
        private bool _hasInspectionAnalysisTransform;
        private bool _isFirstImageForCurrentKey = true;
        private bool _inspectionBaselineRestorePending;
        private volatile bool _suspendManualOverlayInvalidations;
        private bool _layoutAutosaveEnabled;
        private bool IsRoiRepositionFrozen => _freezeRoiRepositionCounter > 0;

        private IDisposable FreezeRoiRepositionScope([CallerMemberName] string? scope = null)
            => new ActionOnDispose(
                () =>
                {
                    _freezeRoiRepositionCounter++;
                    if (!string.IsNullOrEmpty(scope))
                    {
                        AppendLog($"[freeze] enter {scope}");
                    }
                },
                () =>
                {
                    if (_freezeRoiRepositionCounter > 0)
                    {
                        _freezeRoiRepositionCounter--;
                    }

                    if (_freezeRoiRepositionCounter <= 0)
                    {
                        _freezeRoiRepositionCounter = 0;

                        if (!string.IsNullOrEmpty(scope))
                        {
                            AppendLog($"[freeze] exit {scope}");
                        }

                        if (_pendingActiveInspectionSync)
                        {
                            _pendingActiveInspectionSync = false;
                            try
                            {
                                SyncActiveInspectionToLayout();
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"[freeze] deferred sync failed: {ex.Message}");
                            }
                        }
                    }
                });

        private sealed class ActionOnDispose : IDisposable
        {
            private readonly Action? _onExit;
            private bool _disposed;

            public ActionOnDispose(Action? onEnter, Action? onExit)
            {
                _onExit = onExit;

                try
                {
                    onEnter?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                try
                {
                    _onExit?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
        }

        private void LogRoi(string prefix, int slotIndex, RoiModel? roi)
        {
            try
            {
                if (roi == null)
                {
                    AppendLog($"{prefix} slot={slotIndex} roi=NULL");
                    return;
                }

                string baseW = roi.BaseImgW.HasValue
                    ? roi.BaseImgW.Value.ToString("0.###", CultureInfo.InvariantCulture)
                    : "-";
                string baseH = roi.BaseImgH.HasValue
                    ? roi.BaseImgH.Value.ToString("0.###", CultureInfo.InvariantCulture)
                    : "-";

                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "{0} slot={1} id='{2}' role={3} L={4:0.###} T={5:0.###} W={6:0.###} H={7:0.###} " +
                    "CX={8:0.###} CY={9:0.###} R={10:0.###} Rin={11:0.###} Ang={12:0.###} Base=({13}x{14})",
                    prefix,
                    slotIndex,
                    roi.Id ?? "<null>",
                    roi.Role,
                    roi.Left,
                    roi.Top,
                    roi.Width,
                    roi.Height,
                    roi.CX,
                    roi.CY,
                    roi.R,
                    roi.RInner,
                    roi.AngleDeg,
                    baseW,
                    baseH));
            }
            catch
            {
                // Logging must never throw.
            }
        }

        private void SetInspectionSlot(ref RoiModel? slot, RoiModel? value, string propertyName)
        {
            if (ReferenceEquals(slot, value))
            {
                return;
            }

            slot = value;
            if (slot is not null)
            {
                slot.IsFrozen = true;
            }

            OnPropertyChanged(propertyName);
        }

        private RoiModel? GetInspectionSlotModel(int index)
        {
            if (_layout == null)
            {
                return null;
            }

            return index switch
            {
                1 => _layout.Inspection1,
                2 => _layout.Inspection2,
                3 => _layout.Inspection3,
                4 => _layout.Inspection4,
                _ => null
            };
        }

        private bool HasInspectionRoi(int index)
        {
            return GetInspectionSlotModel(index) != null;
        }

        private RoiModel? GetInspectionSlotModelClone(int slotIndex)
        {
            var model = GetInspectionSlotModel(slotIndex);
            return model?.Clone();
        }

        private void LogSaveSlot(string phase, int slotIndex, RoiModel? roi)
        {
            try
            {
                if (roi == null)
                {
                    AppendLog($"[save-slot:{phase}] idx={slotIndex} roi=NULL");
                    return;
                }

                static string FormatNullable(double? value) => value.HasValue
                    ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
                    : "null";

                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "[save-slot:{0}] idx={1} id='{2}' role={3} L={4:0.###} T={5:0.###} W={6:0.###} H={7:0.###} " +
                    "CX={8:0.###} CY={9:0.###} R={10:0.###} Rin={11:0.###} Ang={12:0.###} Base=({13}x{14})",
                    phase,
                    slotIndex,
                    roi.Id ?? "<null>",
                    roi.Role,
                    roi.Left,
                    roi.Top,
                    roi.Width,
                    roi.Height,
                    roi.CX,
                    roi.CY,
                    roi.R,
                    roi.RInner,
                    roi.AngleDeg,
                    FormatNullable(roi.BaseImgW),
                    FormatNullable(roi.BaseImgH)));
            }
            catch
            {
                // Logging must not throw.
            }
        }

        private void SetInspectionSlotModel(int index, RoiModel? model, bool updateActive = true)
        {
            if (_layout == null)
            {
                return;
            }

            RoiModel? assigned = null;

            if (model != null)
            {
                RoiDiag(string.Format(
                    CultureInfo.InvariantCulture,
                    "[preset:inspection-layout] slot={0} id={1} layoutBase=({2}x{3}) layoutImg(L={4:0.###},T={5:0.###},W={6:0.###},H={7:0.###},Shape={8})",
                    index,
                    model.Id ?? "<null>",
                    model.BaseImgW.HasValue ? model.BaseImgW.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-",
                    model.BaseImgH.HasValue ? model.BaseImgH.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-",
                    model.Left,
                    model.Top,
                    model.Width,
                    model.Height,
                    model.Shape));
                NormalizeInspectionRoi(model, index);
                assigned = model.Clone();
                RoiDiag(string.Format(
                    CultureInfo.InvariantCulture,
                    "[preset:inspection-applied] slot={0} id={1} appliedBase=({2}x{3}) appliedImg(L={4:0.###},T={5:0.###},W={6:0.###},H={7:0.###},Shape={8})",
                    index,
                    assigned.Id ?? "<null>",
                    assigned.BaseImgW.HasValue ? assigned.BaseImgW.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-",
                    assigned.BaseImgH.HasValue ? assigned.BaseImgH.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-",
                    assigned.Left,
                    assigned.Top,
                    assigned.Width,
                    assigned.Height,
                    assigned.Shape));
            }

            switch (index)
            {
                case 1:
                    _layout.Inspection1 = assigned;
                    break;
                case 2:
                    _layout.Inspection2 = assigned;
                    break;
                case 3:
                    _layout.Inspection3 = assigned;
                    break;
                case 4:
                    _layout.Inspection4 = assigned;
                    break;
            }

            RoiDiag($"[slot-set] idx={index} assignedId={(assigned?.Id ?? "<null>")} srcId={(model?.Id ?? "<null>")}");

            if (updateActive && index == _activeInspectionIndex)
            {
                SyncActiveInspectionToLayout();
            }
        }

        private RoiModel? GetFirstInspectionRoi()
        {
            for (int i = 1; i <= 4; i++)
            {
                var roi = GetInspectionSlotModel(i);
                if (roi != null && IsRoiSaved(roi))
                {
                    return roi;
                }
            }

            return null;
        }

        private void NormalizeInspectionRoi(RoiModel roi, int index)
        {
            if (roi == null)
            {
                return;
            }

            if (IsRoiRepositionFrozen)
            {
                LogRoi("[normalize] skip (frozen)", index, roi);
                return;
            }

            roi.Role = RoiRole.Inspection;

            var expectedId = $"Inspection_{index}";
            if (string.IsNullOrWhiteSpace(roi.Id) || !string.Equals(roi.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                roi.Id = expectedId;
            }

            var desiredLabel = GetInspectionDisplayName(index);
            if (string.IsNullOrWhiteSpace(roi.Label)
                || string.Equals(roi.Label, "Inspection", StringComparison.OrdinalIgnoreCase)
                || LabelMatchesDifferentInspection(roi.Label, index))
            {
                roi.Label = desiredLabel;
            }
        }

        private string GetInspectionDisplayName(int index)
        {
            if (_layout?.InspectionRois != null && _layout.InspectionRois.Count >= index)
            {
                var cfg = _layout.InspectionRois[index - 1];
                if (!string.IsNullOrWhiteSpace(cfg?.Name))
                {
                    return cfg.Name;
                }

                return cfg?.DisplayName ?? $"Inspection {index}";
            }

            var vmCfg = ViewModel?.InspectionRois?.FirstOrDefault(r => r.Index == index);
            if (vmCfg != null && !string.IsNullOrWhiteSpace(vmCfg.Name))
            {
                return vmCfg.Name;
            }

            return $"Inspection {index}";
        }

        private static bool LabelMatchesDifferentInspection(string? label, int index)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            if (!label.StartsWith("Inspection", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return true;
            }

            if (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed != index;
            }

            return true;
        }

        private static int? TryParseInspectionIndex(RoiModel? roi)
        {
            if (roi == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(roi.Id))
            {
                var parts = roi.Id.Split('_');
                if (parts.Length > 1 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
                {
                    return parsedId;
                }
            }

            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                var parts = roi.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLabel))
                {
                    return parsedLabel;
                }
            }

            return null;
        }

        private List<RoiModel> GetInspectionRoiModelsForRescale()
        {
            var list = new List<RoiModel>();
            var seen = new HashSet<RoiModel>();

            void add(RoiModel? roi)
            {
                if (roi != null && seen.Add(roi))
                {
                    list.Add(roi);
                }
            }

            if (_layout != null)
            {
                add(_layout.Inspection1);
                add(_layout.Inspection2);
                add(_layout.Inspection3);
                add(_layout.Inspection4);
                add(_layout.Inspection);
                add(_layout.InspectionBaseline);
            }

            add(_inspectionBaselineFixed);

            if (_preset?.Inspection1 != null)
            {
                add(_preset.Inspection1);
            }

            if (_preset?.Inspection2 != null)
            {
                add(_preset.Inspection2);
            }

            if (_preset?.Rois != null)
            {
                foreach (var roi in _preset.Rois)
                {
                    if (roi?.Role == RoiRole.Inspection)
                    {
                        add(roi);
                    }
                }
            }

            return list;
        }

        private static string BuildOverlayTag(RoiModel? roi)
        {
            if (roi == null)
            {
                return "roi:unknown";
            }

            var id = roi.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                var index = TryParseInspectionIndex(roi);
                id = index.HasValue ? $"Inspection_{index.Value}" : roi.Role.ToString();
            }

            return $"roi:{id}";
        }

        private void SyncActiveInspectionToLayout()
        {
            if (_layout == null)
            {
                return;
            }

            if (IsRoiRepositionFrozen)
            {
                _pendingActiveInspectionSync = true;
                LogRoi("[sync-active] deferred", _activeInspectionIndex, GetInspectionSlotModel(_activeInspectionIndex));
                return;
            }

            _pendingActiveInspectionSync = false;

            var slot = GetInspectionSlotModel(_activeInspectionIndex);
            _layout.Inspection = slot?.Clone();
            if (_layout.Inspection != null)
            {
                NormalizeInspectionRoi(_layout.Inspection, _activeInspectionIndex);
                _layout.Inspection.IsFrozen = false;
                LogRoi("[sync-active] applied", _activeInspectionIndex, _layout.Inspection);
            }
            else
            {
                LogRoi("[sync-active] applied", _activeInspectionIndex, null);
            }
        }

        private void SetActiveInspectionIndex(int index)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetActiveInspectionIndex(index));
                return;
            }

            int clamped = Math.Max(1, Math.Min(4, index));
            if (_activeInspectionIndex == clamped && !_updatingActiveInspection)
            {
                if (IsRoiRepositionFrozen)
                {
                    _pendingActiveInspectionSync = true;
                    LogRoi("[active-index] deferred", clamped, GetInspectionSlotModel(clamped));
                }
                else
                {
                    SyncActiveInspectionToLayout();
                }
                ApplyInspectionInteractionPolicy("active-index:noop");
                return;
            }

            _activeInspectionIndex = clamped;
            if (IsRoiRepositionFrozen)
            {
                _pendingActiveInspectionSync = true;
                LogRoi("[active-index] deferred", clamped, GetInspectionSlotModel(clamped));
            }
            else
            {
                SyncActiveInspectionToLayout();
            }

            if (!_updatingActiveInspection && ViewModel?.SelectedInspectionRoi?.Index != _activeInspectionIndex)
            {
                try
                {
                    _updatingActiveInspection = true;
                    var target = ViewModel?.InspectionRois?.FirstOrDefault(r => r.Index == _activeInspectionIndex);
                    if (target != null)
                    {
                        ViewModel.SelectedInspectionRoi = target;
                    }
                }
                finally
                {
                    _updatingActiveInspection = false;
                }
            }

            RequestRoiVisibilityRefresh();
            UpdateRoiHud();
            RedrawOverlaySafe();
            ApplyInspectionInteractionPolicy("active-index:set");
        }

        private int ResolveActiveInspectionIndex()
        {
            int index = ViewModel?.SelectedInspectionRoi?.Index ?? _activeInspectionIndex;

            if (index < 1 || index > 4)
            {
                index = _activeInspectionIndex;
            }

            if (index < 1)
            {
                index = 1;
            }

            return Math.Max(1, Math.Min(4, index));
        }

        private enum MasterState { DrawM1_Pattern, DrawM1_Search, DrawM2_Pattern, DrawM2_Search, DrawInspection, Ready }
        private enum RoiCorner { TopLeft, TopRight, BottomRight, BottomLeft }
        private MasterState _state = MasterState.DrawM1_Pattern;

        private Preset _preset = new();
        private MasterLayout _layout = new();     // inicio en blanco

        public MasterLayout? CurrentLayout => _layout;

        // RESULTADO PLAN 12: La GUI canoniza el ROI (G1).
        private readonly AppConfig _appConfig = AppConfigLoader.Load();
        private bool _isInitializingOptions;

        private RoiModel? _tmpBuffer;
        private string _currentImagePath = "";
        private string _currentImagePathWin = "";
        private string _currentImagePathBackend = "";
        private BitmapImage? _imgSourceBI;
        private BitmapSource? _currentImageSource;
        private int _imgW, _imgH;
        private bool _hasLoadedImage;
        private bool _isFirstImageLoaded = false;

        private WorkflowViewModel? _workflowViewModel;
        private WorkflowViewModel? ViewModel => _workflowViewModel;
        private BackendClient? _backendClient;
        private string? _dataRoot;
        private double _heatmapOverlayOpacity = 0.6;
        private double _heatmapGain = 1.0;
        private double _heatmapGamma = 1.0;
        private bool _lastIsNg;

        private bool _sharedHeatmapGuardLogged;

        private TextBlock? _batchCaption;

        private int _activeInspectionIndex = 1;
        private bool _updatingActiveInspection;

        private RoiModel? _inspectionSlot1;
        private RoiModel? _inspectionSlot2;
        private RoiModel? _inspectionSlot3;
        private RoiModel? _inspectionSlot4;

        // Expose layout to XAML bindings (ItemsControl -> Layout.InspectionRois)
        public MasterLayout Layout => _layout;

        public LayoutProfilesViewModel? LayoutProfiles { get; private set; }

        public bool LayoutAutosaveEnabled
        {
            get => _layoutAutosaveEnabled;
            set
            {
                if (_layoutAutosaveEnabled != value)
                {
                    _layoutAutosaveEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        // Available model keys (stub: will be replaced by ModelRegistry keys)
        public System.Collections.Generic.IReadOnlyList<string> AvailableModels { get; }
            = new string[] { "default" };

        public bool IsImageLoaded => _workflowViewModel?.IsImageLoaded ?? false;

        public RoiModel? Inspection1
        {
            get => _inspectionSlot1;
            private set => SetInspectionSlot(ref _inspectionSlot1, value, nameof(Inspection1));
        }

        public RoiModel? Inspection2
        {
            get => _inspectionSlot2;
            private set => SetInspectionSlot(ref _inspectionSlot2, value, nameof(Inspection2));
        }

        public RoiModel? Inspection3
        {
            get => _inspectionSlot3;
            private set => SetInspectionSlot(ref _inspectionSlot3, value, nameof(Inspection3));
        }

        public RoiModel? Inspection4
        {
            get => _inspectionSlot4;
            private set => SetInspectionSlot(ref _inspectionSlot4, value, nameof(Inspection4));
        }

        // Available shapes (enum values)
        public System.Collections.Generic.IReadOnlyList<RoiShape> AvailableShapes { get; }
            = (RoiShape[])System.Enum.GetValues(typeof(RoiShape));

        public bool ScaleLock
        {
            get => _scaleLock;
            set
            {
                if (_scaleLock == value)
                {
                    return;
                }

                _scaleLock = value;
                OnPropertyChanged();
                PersistAnalyzeOptions();
            }
        }

        public double AnalyzePosTolerancePx
        {
            get => _analyzePosTolPx;
            set
            {
                var clamped = Math.Max(0.01, value);
                if (Math.Abs(_analyzePosTolPx - clamped) < 1e-6)
                {
                    return;
                }

                _analyzePosTolPx = clamped;
                OnPropertyChanged();
                PersistAnalyzeOptions();
            }
        }

        public double AnalyzeAngToleranceDeg
        {
            get => _analyzeAngTolDeg;
            set
            {
                var clamped = Math.Max(0.01, value);
                if (Math.Abs(_analyzeAngTolDeg - clamped) < 1e-6)
                {
                    return;
                }

                _analyzeAngTolDeg = clamped;
                OnPropertyChanged();
                PersistAnalyzeOptions();
            }
        }

        public double HeatmapOverlayOpacity
        {
            get => _heatmapOverlayOpacity;
            set
            {
                var clamped = Math.Max(0.0, Math.Min(1.0, value));
                if (Math.Abs(_heatmapOverlayOpacity - clamped) < 1e-6)
                {
                    return;
                }

                _heatmapOverlayOpacity = clamped;
                OnPropertyChanged();

                HeatmapOverlay?.SetCurrentValue(UIElement.OpacityProperty, _heatmapOverlayOpacity);

                if (_workflowViewModel != null)
                {
                    _workflowViewModel.HeatmapOpacity = _heatmapOverlayOpacity;
                }

                PersistUiOptions();
            }
        }

        public double HeatmapGain
        {
            get => _heatmapGain;
            set
            {
                var clamped = Math.Clamp(value, 0.25, 4.0);
                if (Math.Abs(_heatmapGain - clamped) < 1e-6)
                {
                    return;
                }

                _heatmapGain = clamped;
                OnPropertyChanged();
                PersistUiOptions();
            }
        }

        public double HeatmapGamma
        {
            get => _heatmapGamma;
            set
            {
                var clamped = Math.Clamp(value, 0.25, 4.0);
                if (Math.Abs(_heatmapGamma - clamped) < 1e-6)
                {
                    return;
                }

                _heatmapGamma = clamped;
                OnPropertyChanged();
                PersistUiOptions();
            }
        }

        private void PersistAnalyzeOptions()
        {
            if (_layout == null || _isInitializingOptions)
            {
                return;
            }

            _layout.Analyze ??= new AnalyzeOptions();
            _layout.Analyze.PosTolPx = _analyzePosTolPx;
            _layout.Analyze.AngTolDeg = _analyzeAngTolDeg;
            _layout.Analyze.ScaleLock = _scaleLock;
            _layout.Analyze.UseLocalMatcher = (ChkUseLocalMatcher?.IsChecked == true);

            TryPersistLayout();
        }

        private void PersistUiOptions()
        {
            if (_layout == null || _isInitializingOptions)
            {
                return;
            }

            _layout.Ui ??= new UiOptions();
            _layout.Ui.HeatmapOverlayOpacity = _heatmapOverlayOpacity;
            _layout.Ui.HeatmapGain = _heatmapGain;
            _layout.Ui.HeatmapGamma = _heatmapGamma;

            TryPersistLayout();
        }

        private bool IsLayoutAutosaveEnabled() => LayoutAutosaveEnabled;

        private void TryPersistLayout()
        {
            if (_layout == null || _preset == null || _isInitializingOptions || !IsLayoutAutosaveEnabled())
            {
                return;
            }

            TryAutosaveLayout("persist layout options");
        }

        private static bool MastersReady(MasterLayout? layout)
            => layout?.Master1Pattern != null && layout.Master1Search != null;

        private IDisposable SuppressAutoSaves()
            => new ActionOnDispose(
                () => System.Threading.Interlocked.Increment(ref _suppressSaves),
                () => System.Threading.Interlocked.Decrement(ref _suppressSaves));

        private bool TrySaveLayoutGuarded(string prefix, string context, bool requireMasters, out string? errorMessage)
        {
            errorMessage = null;

            if (_preset == null || _layout == null)
            {
                AppendLog($"{prefix} skipped ({context}) layout/preset null");
                errorMessage = "Layout o preset no inicializados.";
                return false;
            }

            if (ViewModel?.IsLayoutPersistenceLocked == true)
            {
                AppendLog("[batch] layout-persist:SKIP");
                errorMessage = "Layout persistence locked.";
                return false;
            }

            using var layoutIo = ViewModel?.BeginLayoutIo(context);

            string layoutPath = MasterLayoutManager.GetDefaultPath(_preset);
            AppendLog($"[save] requested reason={context} path={layoutPath} suppress={_suppressSaves}");

            if (_suppressSaves > 0)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[save] suppressed programmatic change");
                return false;
            }

            if (requireMasters && !MastersReady(_layout))
            {
                AppendLog($"{prefix} skipped ({context}) Master1 incompleto");
                errorMessage = "Master 1 incompleto.";
                return false;
            }

            try
            {
                var snapshot = BuildLayoutSnapshotForSave();
                AppendLog($"[layout] saving ROI-model snapshot ({context})"); // CODEX: persist layout straight from ROI models, never from batch visuals.
                MasterLayoutManager.Save(_preset, snapshot);
                AppendLog($"{prefix} layout saved ({context})");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog($"{prefix} layout-save ERROR ({context}): {ex.Message}");
                errorMessage = ex.Message;
                return false;
            }
        }

        private bool TryAutosaveLayout(string context)
        {
            if (!IsLayoutAutosaveEnabled())
            {
                AppendLog($"[autosave] skipped ({context}) LayoutAutosaveEnabled=false");
                return false;
            }

            return TrySaveLayoutGuarded("[autosave]", context, requireMasters: true, out _);
        }

        public void ApplyLayout(MasterLayout? layout, string sourceContext)
        {
            ApplyLayoutSnapshot(layout, sourceContext);
        }

        private void ApplyLayoutSnapshot(MasterLayout? layout, string sourceContext)
        {
            if (layout == null)
            {
                AppendLog($"[layout] ApplyLayout skipped ({sourceContext}): layout is null");
                return;
            }

            CancelMasterEditing(redraw: false);

            ResetInspectionEditingFlags();
            ResetEditState();
            _editModeActive = false;
            UpdateInspectionEditButtons();

            _layout = layout;

            FreezeAllRois(_layout);
            RemoveAllRoiAdorners();

            InitializeOptionsFromConfig();
            _workflowViewModel?.SetMasterLayout(_layout);
            _workflowViewModel?.SetInspectionRoisCollection(_layout?.InspectionRois);
            RefreshInspectionSlotsFromLayout();

            if (_hasLoadedImage)
            {
                ReseedInspectionBaselineFromLayout($"layout:{sourceContext}");
            }

            UpdateWizardState();
            RequestRoiVisibilityRefresh();
            RedrawOverlaySafe();
            UpdateRoiHud();
        }

        private void FreezeAllRois(MasterLayout? layout)
        {
            if (layout == null)
            {
                return;
            }

            foreach (var roi in EnumerateLayoutRois(layout))
            {
                if (roi != null)
                {
                    roi.IsFrozen = true;
                }
            }

            if (layout.InspectionBaselinesByImage != null)
            {
                foreach (var snapshot in layout.InspectionBaselinesByImage.Values)
                {
                    if (snapshot == null)
                    {
                        continue;
                    }

                    foreach (var roi in snapshot)
                    {
                        if (roi != null)
                        {
                            roi.IsFrozen = true;
                        }
                    }
                }
            }
        }

        private static IEnumerable<RoiModel?> EnumerateLayoutRois(MasterLayout layout)
        {
            yield return layout.Master1Pattern;
            yield return layout.Master1Search;
            yield return layout.Master2Pattern;
            yield return layout.Master2Search;
            yield return layout.Inspection;
            yield return layout.InspectionBaseline;
            yield return layout.Inspection1;
            yield return layout.Inspection2;
            yield return layout.Inspection3;
            yield return layout.Inspection4;
        }

        private void RefreshInspectionSlotsFromLayout()
        {
            var sources = new List<RoiModel?>(capacity: 4);
            for (int index = 1; index <= 4; index++)
            {
                var roi = GetInspectionSlotModel(index) ?? FindInspectionBaselineForIndex(index);
                if (roi == null && _layout?.InspectionRois != null)
                {
                    var cfg = _layout.InspectionRois.FirstOrDefault(r => r != null && r.Index == index);
                    if (cfg?.Enabled == true && !string.IsNullOrWhiteSpace(cfg.Id))
                    {
                        roi = FindInspectionBaselineById(cfg.Id);
                    }
                }
                if (roi != null)
                {
                    roi.IsFrozen = true;
                }

                sources.Add(roi);
            }

            RefreshInspectionRoiSlots(sources);
        }

        private RoiModel? FindInspectionBaselineForIndex(int index)
        {
            if (_layout?.InspectionBaselinesByImage == null)
            {
                return null;
            }

            var id = $"Inspection_{index}";

            foreach (var kv in _layout.InspectionBaselinesByImage)
            {
                var match = kv.Value?
                    .FirstOrDefault(r => r != null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    var clone = match.Clone();
                    clone.IsFrozen = true;
                    return clone;
                }
            }

            return null;
        }

        private RoiModel? FindInspectionBaselineById(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || _layout?.InspectionBaselinesByImage == null)
            {
                return null;
            }

            foreach (var kv in _layout.InspectionBaselinesByImage)
            {
                var match = kv.Value?
                    .FirstOrDefault(r => r != null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    var clone = match.Clone();
                    clone.IsFrozen = true;
                    return clone;
                }
            }

            return null;
        }

        private void RescaleInspectionRoisToNewImageSize(double newW, double newH)
        {
            if (newW <= 0 || newH <= 0)
            {
                return;
            }

            var rois = GetInspectionRoiModelsForRescale();
            if (rois.Count == 0)
            {
                return;
            }

            bool anyScaled = false;

            foreach (var roi in rois)
            {
                if (roi == null)
                {
                    continue;
                }

                int? index = TryParseInspectionIndex(roi);
                if (!index.HasValue && _layout != null)
                {
                    if (ReferenceEquals(roi, _layout.Inspection1)) index = 1;
                    else if (ReferenceEquals(roi, _layout.Inspection2)) index = 2;
                    else if (ReferenceEquals(roi, _layout.Inspection3)) index = 3;
                    else if (ReferenceEquals(roi, _layout.Inspection4)) index = 4;
                }

                var config = index.HasValue ? GetInspectionConfigByIndex(index.Value) : null;
                string slotLabel = index.HasValue ? index.Value.ToString(CultureInfo.InvariantCulture) : "?";
                string roiId = !string.IsNullOrWhiteSpace(roi.Id) ? roi.Id : "<null>";

                double oldW = roi.BaseImgW ?? 0.0;
                double oldH = roi.BaseImgH ?? 0.0;

                if ((oldW <= 0.0 || oldH <= 0.0) && config != null)
                {
                    oldW = config.BaseImgW ?? 0.0;
                    oldH = config.BaseImgH ?? 0.0;
                }

                if (oldW <= 0.0 || oldH <= 0.0)
                {
                    roi.BaseImgW ??= newW;
                    roi.BaseImgH ??= newH;
                    if (config != null)
                    {
                        config.BaseImgW ??= newW;
                        config.BaseImgH ??= newH;
                    }
                    AppendLog(string.Format(CultureInfo.InvariantCulture,
                        "[scale] slot={0} id={1} base-missing -> base set to ({2:0.###}x{3:0.###})",
                        slotLabel,
                        roiId,
                        newW,
                        newH));
                    continue;
                }

                double sx = newW / oldW;
                double sy = newH / oldH;

                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "[scale] slot={0} id={1} sx={2:0.###} sy={3:0.###} base=({4:0.###}x{5:0.###}) -> ({6:0.###}x{7:0.###})",
                    slotLabel,
                    roiId,
                    sx,
                    sy,
                    oldW,
                    oldH,
                    newW,
                    newH));

                if (Math.Abs(sx - 1.0) < 1e-9 && Math.Abs(sy - 1.0) < 1e-9)
                {
                    roi.BaseImgW = newW;
                    roi.BaseImgH = newH;
                    if (config != null)
                    {
                        config.BaseImgW = newW;
                        config.BaseImgH = newH;
                    }
                    continue;
                }

                switch (roi.Shape)
                {
                    case RoiShape.Rectangle:
                        roi.X *= sx;
                        roi.Y *= sy;
                        roi.Width *= sx;
                        roi.Height *= sy;
                        break;

                    case RoiShape.Circle:
                    case RoiShape.Annulus:
                        roi.CX *= sx;
                        roi.CY *= sy;
                        roi.X *= sx;
                        roi.Y *= sy;
                        double s = Math.Min(sx, sy);
                        roi.R *= s;
                        if (roi.Shape == RoiShape.Annulus)
                        {
                            roi.RInner *= s;
                        }
                        roi.Width *= s;
                        roi.Height *= s;
                        break;
                }

                roi.BaseImgW = newW;
                roi.BaseImgH = newH;

                if (config != null)
                {
                    config.BaseImgW = newW;
                    config.BaseImgH = newH;
                }

                anyScaled = true;
            }

            if (anyScaled)
            {
                TryPersistLayout();
            }
        }

        // === ROI diagnostics ===
        private static readonly string RoiDiagLogPath =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrakeDiscInspector", "logs", "roi_load_coords.log");

        private readonly object _roiDiagLock = new object();
        private string _roiDiagSessionId = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
        private int _roiDiagEventSeq = 0;
        private bool _roiDiagEnabled = true;   // flip to false to silence

        private System.Windows.Media.Imaging.BitmapSource _lastHeatmapBmp;          // heatmap image in image space
        private HeatmapRoiModel _lastHeatmapRoi;              // ROI (image-space) that defines the heatmap clipping area
        // --- Fixed baseline per image (no drift) ---
        private bool _useFixedInspectionBaseline = true;        // keep using fixed baseline (must be true)
        private RoiModel? _inspectionBaselineFixed = null;      // the fixed baseline for the current image
        private bool _inspectionBaselineSeededForImage = false; // has the baseline been seeded for the current image?
        private readonly Dictionary<string, List<RoiModel>> _inspectionBaselinesByImage
            = new(StringComparer.OrdinalIgnoreCase);
        private string _currentImageHash = string.Empty;
        private string _lastImageSeedKey = "";                  // “signature” of the currently loaded image

        // --- Per-image master baselines (seeded on load) ---
        private string _imageKeyForMasters = "";
        private bool _mastersSeededForImage = false;
        private double _m1BaseX = 0, _m1BaseY = 0;
        private double _m2BaseX = 0, _m2BaseY = 0;

        // --- Last accepted detections on this image (for idempotence) ---
        private double _lastAccM1X = double.NaN, _lastAccM1Y = double.NaN;
        private double _lastAccM2X = double.NaN, _lastAccM2Y = double.NaN;
        private bool _lastDetectionAccepted = false;

        // Tolerances (pixels / degrees). Tune if needed.
        private double _analyzePosTolPx = 1.0;    // <=1 px considered the same
        private double _analyzeAngTolDeg = 0.5;   // <=0.5° considered the same

        private bool _scaleLock = true;                  // Size lock already in use; keep it true

        // ============================
        // Global switches / options
        // ============================

        // Respect "scale lock" by default. If you want to allow scaling of the Inspection ROI
        // *even when* the lock is ON, set this to true at runtime (e.g., via a checkbox).
        private bool _allowInspectionScaleOverride = false;

        // Freeze Master*Search movement on Analyze Master
        private const bool FREEZE_MASTER_SEARCH_ON_ANALYZE = true;

        private static readonly SolidColorBrush RoiHudAccentBrush = new SolidColorBrush(Color.FromRgb(0x39, 0xFF, 0x14));

        // ============================
        // Logging helpers (safe everywhere)
        // ============================
        [System.Diagnostics.Conditional("DEBUG")]
        private void Dbg(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
        }

        // -------- Logging shim (works with or without _log) --------
        [System.Diagnostics.Conditional("DEBUG")]
        private void LogDebug(string msg)
        {
            try
            {
                var loggerField = typeof(MainWindow).GetField("_log", BindingFlags.Instance | BindingFlags.NonPublic);
                var logger = loggerField?.GetValue(this);
                if (logger != null)
                {
                    var infoMethod = logger.GetType().GetMethod("Info", new[] { typeof(string) });
                    infoMethod?.Invoke(logger, new object[] { msg });
                }
            }
            catch
            {
                // ignore shim errors
            }

            Debug.WriteLine(msg);
        }

        // -------- Hashing / Pixel helpers --------
        private static string HashSHA256(byte[] data)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(data);
            return BitConverter.ToString(h).Replace("-", string.Empty);
        }

        private static byte[] GetPixels(BitmapSource bmp)
        {
            int stride = (bmp.PixelWidth * bmp.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        private static BitmapSource CropToBitmap(BitmapSource src, System.Windows.Int32Rect rect)
        {
            rect.X = Math.Max(0, Math.Min(rect.X, src.PixelWidth - 1));
            rect.Y = Math.Max(0, Math.Min(rect.Y, src.PixelHeight - 1));
            rect.Width = Math.Max(1, Math.Min(rect.Width, src.PixelWidth - rect.X));
            rect.Height = Math.Max(1, Math.Min(rect.Height, src.PixelHeight - rect.Y));
            var cropped = new CroppedBitmap(src, rect);
            if (cropped.CanFreeze && !cropped.IsFrozen)
            {
                try { cropped.Freeze(); } catch { }
            }
            return cropped;
        }

        private void OnImageLoaded_SetCurrentSource(BitmapSource? src)
        {
            if (src == null)
            {
                _currentImageSource = null;
                LogDebug("[ImageLoad] currentImage cleared (null).");
                return;
            }

            BitmapSource snapshot = src;
            if (!snapshot.IsFrozen)
            {
                try
                {
                    if (snapshot.CanFreeze)
                    {
                        snapshot.Freeze();
                    }
                    else
                    {
                        snapshot = snapshot.Clone();
                        if (snapshot.CanFreeze && !snapshot.IsFrozen)
                        {
                            snapshot.Freeze();
                        }
                    }
                }
                catch
                {
                    try
                    {
                        snapshot = snapshot.Clone();
                        if (snapshot.CanFreeze && !snapshot.IsFrozen)
                        {
                            snapshot.Freeze();
                        }
                    }
                    catch
                    {
                        // swallow clone/freeze issues
                    }
                }
            }

            _currentImageSource = snapshot;
            LogDebug($"[ImageLoad] currentImage set: {_currentImageSource?.PixelWidth}x{_currentImageSource?.PixelHeight} fmt={_currentImageSource?.Format}");
        }

        private static System.Windows.Int32Rect RoiRectImageSpace(RoiModel r)
        {
            double cx = !double.IsNaN(r.CX) ? r.CX : r.X;
            double cy = !double.IsNaN(r.CY) ? r.CY : r.Y;
            double w = r.Width > 0 ? r.Width : (r.R > 0 ? 2 * r.R : 1);
            double h = r.Height > 0 ? r.Height : (r.R > 0 ? 2 * r.R : 1);

            int x = (int)Math.Round(cx - w / 2.0);
            int y = (int)Math.Round(cy - h / 2.0);
            int W = Math.Max(1, (int)Math.Round(w));
            int H = Math.Max(1, (int)Math.Round(h));
            return new System.Windows.Int32Rect(x, y, W, H);
        }

        private static double NormalizeAngleRad(double ang)
        {
            // Normalize to [-pi, pi)
            ang = (ang + Math.PI) % (2.0 * Math.PI);
            if (ang < 0) ang += 2.0 * Math.PI;
            return ang - Math.PI;
        }

        private static double AngleOf(SWVector v) => Math.Atan2(v.Y, v.X);

        private static SWVector Normalize(SWVector v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 1e-12) return new SWVector(0, 0);
            return new SWVector(v.X / len, v.Y / len);
        }

        // True geometric center by shape; safe even if RoiModel lacks a GetCenter() helper.
        private static (double cx, double cy) GetCenterShapeAware(RoiModel r)
        {
            switch (r.Shape)
            {
                case RoiShape.Rectangle:
                    return (r.X, r.Y);   // X,Y used as rectangle center in this project
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    // CX/CY store circle/annulus center
                    return (r.CX, r.CY);
                default:
                    // Fallback: prefer CX/CY if set; else X/Y
                    if (!double.IsNaN(r.CX) && !double.IsNaN(r.CY)) return (r.CX, r.CY);
                    return (r.X, r.Y);
            }
        }

        // Set center in IMAGE space; updates CX/CY and Left/Top
        private static void SetRoiCenterImg(RoiModel r, double cx, double cy)
        {
            r.CX = cx; r.CY = cy;
            r.Left = cx - r.Width * 0.5;
            r.Top  = cy - r.Height * 0.5;
        }

        private static SWPoint MapBySt(SWPoint m1Base, SWPoint m2Base, SWPoint m1New, SWPoint m2New,
                                       SWPoint roiBase, bool scaleLock)
        {
            var u0 = m2Base - m1Base;
            var v0 = new SWVector(-u0.Y, u0.X);
            var L0 = Math.Sqrt(u0.X * u0.X + u0.Y * u0.Y);
            if (L0 < 1e-9) return roiBase;

            u0 = new SWVector(u0.X / L0, u0.Y / L0);
            var v0n = Normalize(v0);

            var d0 = roiBase - m1Base;
            double s = d0.X * u0.X + d0.Y * u0.Y;
            double t = d0.X * v0n.X + d0.Y * v0n.Y;

            var u1 = m2New - m1New;
            var L1 = Math.Sqrt(u1.X * u1.X + u1.Y * u1.Y);
            if (L1 < 1e-9) return roiBase;

            var u1n = new SWVector(u1.X / L1, u1.Y / L1);
            var v1n = new SWVector(-u1n.Y, u1n.X);

            if (!scaleLock)
            {
                double k = L1 / L0;
                s *= k;
                t *= k;
            }

            return new SWPoint(
                m1New.X + s * u1n.X + t * v1n.X,
                m1New.Y + s * u1n.Y + t * v1n.Y);
        }

        private static double DeltaAngleFromFrames(SWPoint m1Base, SWPoint m2Base, SWPoint m1New, SWPoint m2New)
        {
            var a0 = AngleOf(m2Base - m1Base);
            var a1 = AngleOf(m2New - m1New);
            return NormalizeAngleRad(a1 - a0);
        }

        private void RepositionMastersToCrosses(SWPoint m1Cross, SWPoint m2Cross, bool scaleLock,
                                                RoiModel? master1Baseline = null, RoiModel? master2Baseline = null)
        {
            if (_layout == null)
                return;

            var baselineM1 = master1Baseline ?? _layout.Master1Pattern?.Clone();
            var baselineM2 = master2Baseline ?? _layout.Master2Pattern?.Clone();

            if (_layout.Master1Pattern != null)
                SetRoiCenterImg(_layout.Master1Pattern, m1Cross.X, m1Cross.Y);
            if (_layout.Master2Pattern != null)
                SetRoiCenterImg(_layout.Master2Pattern, m2Cross.X, m2Cross.Y);

            if (baselineM1 == null || baselineM2 == null)
                return;

            var (m1bX, m1bY) = GetCenterShapeAware(baselineM1);
            var (m2bX, m2bY) = GetCenterShapeAware(baselineM2);
            var m1Base = new SWPoint(m1bX, m1bY);
            var m2Base = new SWPoint(m2bX, m2bY);

            double dAng = DeltaAngleFromFrames(m1Base, m2Base, m1Cross, m2Cross);

            if (_layout.Master1Pattern != null && _layout.Master1Pattern.Shape == RoiShape.Rectangle)
                _layout.Master1Pattern.AngleDeg = baselineM1.AngleDeg + dAng * (180.0 / Math.PI);

            if (_layout.Master2Pattern != null && _layout.Master2Pattern.Shape == RoiShape.Rectangle)
                _layout.Master2Pattern.AngleDeg = baselineM2.AngleDeg + dAng * (180.0 / Math.PI);
        }

        private void RepositionInspectionUsingSt(SWPoint m1Cross, SWPoint m2Cross, bool scaleLock,
                                                 RoiModel? master1Baseline = null, RoiModel? master2Baseline = null)
        {
            if (_layout == null)
                return;

            var baselineM1 = master1Baseline ?? _layout.Master1Pattern?.Clone();
            var baselineM2 = master2Baseline ?? _layout.Master2Pattern?.Clone();
            if (baselineM1 == null || baselineM2 == null)
                return;

            var (m1bX, m1bY) = GetCenterShapeAware(baselineM1);
            var (m2bX, m2bY) = GetCenterShapeAware(baselineM2);
            var m1Base = new SWPoint(m1bX, m1bY);
            var m2Base = new SWPoint(m2bX, m2bY);

            var baseVec = m2Base - m1Base;
            var newVec = m2Cross - m1Cross;
            double len0 = Math.Sqrt(baseVec.X * baseVec.X + baseVec.Y * baseVec.Y);
            double len1 = Math.Sqrt(newVec.X * newVec.X + newVec.Y * newVec.Y);
            if (len0 < 1e-9 || len1 < 1e-9)
                return;

            double scaleRatio = len1 / len0;
            bool effectiveScaleLock = scaleLock && !_allowInspectionScaleOverride;
            double angleDelta = DeltaAngleFromFrames(m1Base, m2Base, m1Cross, m2Cross);

            var roisToMove = new List<(RoiModel target, RoiModel baseline)>();
            var seen = new HashSet<RoiModel>();

            int moved = 0;
            int skippedFrozen = 0;
            var movedLabels = new List<string>();

            void EnqueueInspection(RoiModel? roi, bool always = false)
            {
                if (roi == null || !seen.Add(roi))
                    return;

                if (!always && roi.IsFrozen)
                {
                    skippedFrozen++;
                    return;
                }

                roisToMove.Add((roi, roi.Clone()));
            }

            EnqueueInspection(_layout.Inspection1, always: true);
            EnqueueInspection(_layout.Inspection2, always: true);
            EnqueueInspection(_layout.Inspection3, always: true);
            EnqueueInspection(_layout.Inspection4, always: true);
            EnqueueInspection(_layout.Inspection, always: true);

            foreach (var (target, baseline) in roisToMove)
            {
                if (target == null || baseline == null)
                    continue;

                double scaleFactor = effectiveScaleLock ? 1.0 : scaleRatio;

                switch (target.Shape)
                {
                    case RoiShape.Rectangle:
                        target.Width = baseline.Width * scaleFactor;
                        target.Height = baseline.Height * scaleFactor;
                        break;
                    case RoiShape.Circle:
                        target.R = baseline.R * scaleFactor;
                        target.Width = baseline.Width * scaleFactor;
                        target.Height = baseline.Height * scaleFactor;
                        break;
                    case RoiShape.Annulus:
                        target.R = baseline.R * scaleFactor;
                        target.RInner = baseline.RInner * scaleFactor;
                        target.Width = baseline.Width * scaleFactor;
                        target.Height = baseline.Height * scaleFactor;
                        break;
                }

                var (cx, cy) = GetCenterShapeAware(baseline);
                var mapped = MapBySt(m1Base, m2Base, m1Cross, m2Cross, new SWPoint(cx, cy), effectiveScaleLock);
                SetRoiCenterImg(target, mapped.X, mapped.Y);

                target.AngleDeg = baseline.AngleDeg + angleDelta * (180.0 / Math.PI);

                moved++;
                if (!string.IsNullOrEmpty(target?.Label))
                {
                    movedLabels.Add(target.Label!);
                }
                else if (target != null)
                {
                    movedLabels.Add(target.Role.ToString());
                }
                else
                {
                    movedLabels.Add("Inspection");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[AnalyzeMaster] moved={moved} skipped_frozen={skippedFrozen} moved_labels={string.Join(", ", movedLabels)}");
            AppendLog($"[AnalyzeMaster] moved={moved} skipped_frozen={skippedFrozen} moved_labels={string.Join(", ", movedLabels)}");
        }

        private bool AcceptNewDetectionIfDifferent(SWPoint newM1, SWPoint newM2, bool scaleLock,
                                                   RoiModel? master1Baseline = null, RoiModel? master2Baseline = null)
        {
            if (_layout == null)
            {
                _lastDetectionAccepted = false;
                return false;
            }

            var baselineM1 = master1Baseline ?? _layout.Master1Pattern?.Clone();
            var baselineM2 = master2Baseline ?? _layout.Master2Pattern?.Clone();

            bool hasLast = !double.IsNaN(_lastAccM1X) && !double.IsNaN(_lastAccM2X);
            double posTol = _layout?.Analyze?.PosTolPx ?? _analyzePosTolPx;
            double angTol = _layout?.Analyze?.AngTolDeg ?? _analyzeAngTolDeg;

            bool accept = !hasLast;

            if (hasLast)
            {
                double dM1 = Dist(newM1.X, newM1.Y, _lastAccM1X, _lastAccM1Y);
                double dM2 = Dist(newM2.X, newM2.Y, _lastAccM2X, _lastAccM2Y);
                double angOld = AngleDeg(_lastAccM2Y - _lastAccM1Y, _lastAccM2X - _lastAccM1X);
                double angNew = AngleDeg(newM2.Y - newM1.Y, newM2.X - newM1.X);
                double dAng = Math.Abs(angNew - angOld);
                if (dAng > 180.0)
                    dAng = 360.0 - dAng;

                bool acceptByPos = posTol <= 0 || dM1 > posTol || dM2 > posTol;
                bool acceptByAng = angTol <= 0 || dAng > angTol;

                accept = acceptByPos || acceptByAng;
            }

            var m1ToApply = (!hasLast || accept) ? newM1 : new SWPoint(_lastAccM1X, _lastAccM1Y);
            var m2ToApply = (!hasLast || accept) ? newM2 : new SWPoint(_lastAccM2X, _lastAccM2Y);

            RepositionMastersToCrosses(m1ToApply, m2ToApply, scaleLock, baselineM1, baselineM2);
            RepositionInspectionUsingSt(m1ToApply, m2ToApply, scaleLock, baselineM1, baselineM2);

            RedrawAllRois();
            UpdateRoiHud();
            try { RedrawAnalysisCrosses(); }
            catch { }

            if (accept || !hasLast)
            {
                _lastAccM1X = newM1.X; _lastAccM1Y = newM1.Y;
                _lastAccM2X = newM2.X; _lastAccM2Y = newM2.Y;
            }

            _lastDetectionAccepted = accept;
            return accept;
        }

        private static bool IsRoiSaved(RoiModel? r)
        {
            if (r == null) return false;

            static bool finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

            switch (r.Shape)
            {
                case RoiShape.Rectangle:
                    return finite(r.X) && finite(r.Y) && r.Width > 0 && r.Height > 0;
                case RoiShape.Circle:
                    return finite(r.CX) && finite(r.CY) && r.R > 0;
                case RoiShape.Annulus:
                    return finite(r.CX) && finite(r.CY) && r.R > 0 && r.RInner >= 0 && r.R > r.RInner;
                default:
                    return false;
            }
        }

        private bool HasAllMastersAndInspectionsDefined()
        {
            if (_layout == null)
                return false;

            if (!IsRoiSaved(_layout.Master1Pattern) || !IsRoiSaved(_layout.Master2Pattern))
                return false;

            if (!TryGetMasterInspection(1, out var master1Inspection) || !IsRoiSaved(master1Inspection))
                return false;

            if (!TryGetMasterInspection(2, out var master2Inspection) || !IsRoiSaved(master2Inspection))
                return false;

            var savedInspectionRois = CollectSavedInspectionRois();
            return savedInspectionRois.Count > 0;
        }

        // Place a label tangent to a circle/annulus at angle thetaDeg (IMAGE -> CANVAS)
        private void PlaceLabelOnCircle(FrameworkElement label, RoiModel circle, double thetaDeg)
        {
            double theta = thetaDeg * Math.PI / 180.0;
            double r = circle.Shape == RoiShape.Annulus
                ? Math.Max(circle.R, circle.RInner)
                : (circle.R > 0 ? circle.R : Math.Max(circle.Width, circle.Height) * 0.5);

            double px = circle.CX + r * Math.Cos(theta);
            double py = circle.CY + r * Math.Sin(theta);
            var canvasPt = ImagePxToCanvasPt(px, py);

            // Center the label and orient tangent
            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, canvasPt.X - (label.DesiredSize.Width * 0.5));
            Canvas.SetTop(label,  canvasPt.Y - (label.DesiredSize.Height * 0.5));
            label.RenderTransform = new System.Windows.Media.RotateTransform(thetaDeg + 90.0,
                label.DesiredSize.Width * 0.5, label.DesiredSize.Height * 0.5);
        }

        // Modern label factory: Border(black + neon-green) + TextBlock(white)
        private FrameworkElement CreateStyledLabel(string text)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = text,
                Foreground = System.Windows.Media.Brushes.Lime,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0),
                Padding = new System.Windows.Thickness(6, 2, 6, 2)
            };

            var neon = (System.Windows.Media.SolidColorBrush)
                (new System.Windows.Media.BrushConverter().ConvertFromString("#39FF14"));

            var border = new System.Windows.Controls.Border
            {
                Child = tb,
                Background = System.Windows.Media.Brushes.Black,
                BorderBrush = neon,
                BorderThickness = new System.Windows.Thickness(1.5),
                CornerRadius = new System.Windows.CornerRadius(4)
            };
            System.Windows.Controls.Panel.SetZIndex(border, int.MaxValue);
            return border;
        }

        private static readonly string InspAlignLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector", "logs", "roi_analyze_master.log");
        private readonly object _inspLogLock = new object();

        // IMAGE-space centers (pixels) of found masters
        private CvPoint? _lastM1CenterPx;
        private CvPoint? _lastM2CenterPx;
        private CvPoint? _lastMidCenterPx;

        private Shape? _previewShape;
        private bool _isDrawing;
        private SWPoint _p0;
        private RoiShape _currentShape = RoiShape.Rectangle;

        private DispatcherTimer? _trainTimer;

        // ==== Drag de ROI (mover) ====
        private Shape? _dragShape;
        private System.Windows.Point _dragStart;
        private double _dragOrigX, _dragOrigY;

        private readonly Dictionary<RoiModel, DragLogState> _dragLogStates = new();
        private const double DragLogMovementThreshold = 5.0; // px (canvas)
        private const double DragLogAngleThreshold = 1.0;    // grados

        private const double AnnulusLogThreshold = 0.5;
        private double _lastLoggedAnnulusOuterRadius = double.NaN;
        private double _lastLoggedAnnulusInnerProposed = double.NaN;
        private double _lastLoggedAnnulusInnerFinal = double.NaN;
        private bool _annulusResetLogged;

        // Cache de la última sincronización del overlay
        private double _canvasLeftPx = 0;
        private double _canvasTopPx = 0;
        private double _canvasWpx = 0;
        private double _canvasHpx = 0;
        private double _sx = 1.0;   // escala imagen->canvas en X
        private double _sy = 1.0;   // escala imagen->canvas en Y


        // === File Logger ===
        private readonly object _fileLogLock = new object();
        private readonly string _fileLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "brakedisc_localmatcher.log");
        private static readonly string _resizeLogDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrakeDiscInspector", "logs");
        // Si tu overlay se llama distinto, ajusta esta propiedad (o referencia directa en los métodos).
        // Por ejemplo, si en XAML tienes <Canvas x:Name="Overlay"> usa ese nombre aquí.
        private Canvas OverlayCanvas => CanvasROI;

        private const string ANALYSIS_TAG = "analysis-mark";
        // === Helpers de overlay ===
        private const double LabelOffsetX = 10;   // desplazamiento a la derecha de la cruz
        private const double LabelOffsetY = -20;  // desplazamiento hacia arriba de la cruz

        private LegacyROI CurrentRoi = new LegacyROI
        {
            X = 200,
            Y = 150,
            CX = 200,
            CY = 150,
            Width = 100,
            Height = 80,
            AngleDeg = 0,
            Legend = string.Empty,
            Shape = RoiShape.Rectangle,
            R = 50,
            RInner = 0
        };
        private Mat? bgrFrame; // tu frame actual
        private bool UseAnnulus = false;

        private bool _loadedOnce;
        private RoiShape _currentDrawTool = RoiShape.Rectangle;
        private bool _updatingDrawToolUi;

        private readonly Dictionary<string, Shape> _roiShapesById = new();
        private readonly Dictionary<Shape, FrameworkElement> _roiLabels = new();
        private readonly Dictionary<RoiRole, bool> _roiCheckboxHasRoi = new();
        private bool _roiVisibilityRefreshPending;

        // Overlay visibility flags (do not affect freeze/geometry)
        private bool _showMaster1PatternOverlay = true;
        private bool _showMaster2PatternOverlay = true;
        private bool _showMaster1SearchOverlay  = true;
        private bool _showMaster2SearchOverlay  = true;

        private System.Windows.Controls.StackPanel _roiChecksPanel;
        private CheckBox? _chkHeatmap;
        private Slider? _sldHeatmapScale;
        private Slider? _sldHeatmapOpacity;
        private TextBlock? _lblHeatmapScale;
        private bool _heatmapCheckboxEventsHooked;
        private bool _heatmapSliderEventsHooked;
        private double _heatmapNormMax = 1.0; // Global heatmap scale (1.0 = default). Lower -> brighter, Higher -> darker.

        private static void UILog(string msg)
        {
            Debug.WriteLine(msg);
            GuiLog.Info($"{msg}"); // CODEX: ensure string logging overload receives the interpolated message.
        }

        private bool _batchUiHandlersAttached = false;
        private bool _batchSizeHandlersHooked = false; // CODEX: wire size events once to prevent duplicate scheduling.
        private WorkflowViewModel? _batchVmCached = null;
        private DispatcherOperation? _pendingBatchPlacementOp; // CODEX: coalesce pending batch placements to avoid stale ROI indices.
        private TextBlock? _batchInfoOverlay;

        // Cache of last gray heatmap to recolor on-the-fly
        private byte[]? _lastHeatmapGray;
        private int _lastHeatmapW, _lastHeatmapH;

        private IEnumerable<RoiModel> SavedRois
        {
            get
            {
                if (_layout.Master1Pattern != null && _showMaster1PatternOverlay)
                    yield return _layout.Master1Pattern;

                if (_layout.Master2Pattern != null && _showMaster2PatternOverlay)
                    yield return _layout.Master2Pattern;

                if (_layout.Master1Search != null && _showMaster1SearchOverlay)
                    yield return _layout.Master1Search;

                if (_layout.Master2Search != null && _showMaster2SearchOverlay)
                    yield return _layout.Master2Search;

                for (int index = 1; index <= 4; index++)
                {
                    var inspection = GetInspectionSlotModel(index);
                    if (inspection != null && IsRoiSaved(inspection))
                    {
                        yield return inspection;
                    }
                }
            }
        }

        private sealed class HeatmapRoiModel : RoiModel
        {
            public static HeatmapRoiModel From(RoiModel src)
            {
                if (src == null)
                    return null;

                if (src is HeatmapRoiModel existing)
                    return existing;

                return new HeatmapRoiModel
                {
                    Id = src.Id,
                    Label = src.Label,
                    Shape = src.Shape,
                    Role = src.Role,
                    AngleDeg = src.AngleDeg,
                    X = src.X,
                    Y = src.Y,
                    Width = src.Width,
                    Height = src.Height,
                    CX = src.CX,
                    CY = src.CY,
                    R = src.R,
                    RInner = src.RInner
                };
            }

            public RoiShapeType ShapeType => (RoiShapeType)Shape;
        }
        // ---------- Logging helpers ----------

        private void RoiDiag(string line)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                sb.Append("[").Append(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ").Append(line);
                var dir = System.IO.Path.GetDirectoryName(RoiDiagLogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.AppendAllText(RoiDiagLogPath, sb.ToString() + System.Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { /* never throw */ }
        }

        private void LogImgLoadAndRois(string stageTag)
        {
            RoiDiag($"[{stageTag}] img=({_imgW}x{_imgH})");
            var t = GetImageToCanvasTransform();
            RoiDiag($"[{stageTag}] transform sx={t.sx:F6} sy={t.sy:F6} off=({t.offX:F2},{t.offY:F2})");

            foreach (var roi in GetInspectionRoiModelsForRescale())
            {
                if (roi == null || roi.Role != RoiRole.Inspection)
                {
                    continue;
                }

                string baseW = roi.BaseImgW.HasValue ? roi.BaseImgW.Value.ToString("F1", CultureInfo.InvariantCulture) : "-";
                string baseH = roi.BaseImgH.HasValue ? roi.BaseImgH.Value.ToString("F1", CultureInfo.InvariantCulture) : "-";

                if (roi.Shape == RoiShape.Rectangle)
                {
                    RoiDiag(string.Format(CultureInfo.InvariantCulture,
                        "[{0}] INSPECT_RECT id={1} x={2:F1} y={3:F1} w={4:F1} h={5:F1} Base=({6}x{7})",
                        stageTag,
                        roi.Id ?? "<null>",
                        roi.X,
                        roi.Y,
                        roi.Width,
                        roi.Height,
                        baseW,
                        baseH));
                }
                else
                {
                    double rInner = roi.Shape == RoiShape.Annulus ? roi.RInner : 0.0;
                    RoiDiag(string.Format(CultureInfo.InvariantCulture,
                        "[{0}] INSPECT_CIRC id={1} cx={2:F1} cy={3:F1} R={4:F1} RInner={5:F1} Base=({6}x{7})",
                        stageTag,
                        roi.Id ?? "<null>",
                        roi.CX,
                        roi.CY,
                        roi.R,
                        rInner,
                        baseW,
                        baseH));
                }
            }
        }

        private FrameworkElement? FindInspectionShapeById(string id)
        {
            var canvas = CanvasROI;
            if (canvas == null)
            {
                return null;
            }

            foreach (var child in canvas.Children)
            {
                if (child is FrameworkElement fe)
                {
                    if (fe.Tag is RoiModel model && string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return fe;
                    }

                    if (fe.Tag is string tag && string.Equals(tag, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return fe;
                    }

                    if (!string.IsNullOrWhiteSpace(fe.Name) && string.Equals(fe.Name, "roiShape_" + id, StringComparison.OrdinalIgnoreCase))
                    {
                        return fe;
                    }
                }
            }

            return null;
        }

        private RoiModel? FindInspectionRoiModel(string roiId)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return null;
            }

            for (int index = 1; index <= 4; index++)
            {
                var roi = GetInspectionSlotModel(index);
                if (roi != null && string.Equals(roi.Id, roiId, StringComparison.OrdinalIgnoreCase))
                {
                    return roi;
                }
            }

            return null;
        }

        private Shape? FindInspectionShape(string roiId)
        {
            return FindInspectionShapeById(roiId) as Shape;
        }

        private void DumpUiShapesMap(string stageTag)
        {
            var canvas = CanvasROI;
            if (canvas == null)
            {
                RoiDiag($"[{stageTag}] UI-SHAPE canvas=<null>");
                return;
            }

            foreach (var child in canvas.Children)
            {
                if (child is FrameworkElement fe)
                {
                    double L = System.Windows.Controls.Canvas.GetLeft(fe);
                    double T = System.Windows.Controls.Canvas.GetTop(fe);
                    if (double.IsNaN(L)) L = 0;
                    if (double.IsNaN(T)) T = 0;
                    double W = fe.ActualWidth;
                    double H = fe.ActualHeight;
                    if ((double.IsNaN(W) || W <= 0) && fe is Shape shapeW && !double.IsNaN(shapeW.Width) && shapeW.Width > 0)
                    {
                        W = shapeW.Width;
                    }
                    if ((double.IsNaN(H) || H <= 0) && fe is Shape shapeH && !double.IsNaN(shapeH.Height) && shapeH.Height > 0)
                    {
                        H = shapeH.Height;
                    }
                    var name = fe.Name;
                    var tag = fe.Tag?.ToString();
                    RoiDiag($"[{stageTag}] UI-SHAPE name='{name}' tag='{tag}' L={L:F1} T={T:F1} W={W:F1} H={H:F1}");
                }
            }
        }

        private void LogDeltaAgainstShape(string stageTag, string inspectionId, System.Windows.Rect expectedCanvasRect)
        {
            if (string.IsNullOrWhiteSpace(inspectionId))
            {
                RoiDiag($"[{stageTag}] DELTA id=<null>");
                return;
            }

            var fe = FindInspectionShapeById(inspectionId);
            if (fe == null)
            {
                RoiDiag($"[{stageTag}] DELTA id={inspectionId} shape=NOT_FOUND");
                return;
            }

            double L = System.Windows.Controls.Canvas.GetLeft(fe);
            double T = System.Windows.Controls.Canvas.GetTop(fe);
            if (double.IsNaN(L)) L = 0;
            if (double.IsNaN(T)) T = 0;
            double W = fe.ActualWidth;
            double H = fe.ActualHeight;
            if ((double.IsNaN(W) || W <= 0) && fe is Shape shapeW && !double.IsNaN(shapeW.Width) && shapeW.Width > 0)
            {
                W = shapeW.Width;
            }
            if ((double.IsNaN(H) || H <= 0) && fe is Shape shapeH && !double.IsNaN(shapeH.Height) && shapeH.Height > 0)
            {
                H = shapeH.Height;
            }
            var deltaPos = (expectedCanvasRect.X - L, expectedCanvasRect.Y - T);
            var deltaSize = (expectedCanvasRect.Width - W, expectedCanvasRect.Height - H);
            RoiDiag($"[{stageTag}] DELTA id={inspectionId} Δpos=({deltaPos.Item1:F1},{deltaPos.Item2:F1}) Δsize=({deltaSize.Item1:F1},{deltaSize.Item2:F1})");
        }

        private void RoiDiagLog(string line)
        {
            if (!_roiDiagEnabled) return;
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("#").Append(++_roiDiagEventSeq).Append(" ");
                sb.Append("[").Append(_roiDiagSessionId).Append("] ");
                sb.Append(line);
                var formatted = sb.ToString();
                lock (_roiDiagLock)
                {
                    RoiDiag(formatted);
                }
            }
            catch { /* never throw from logging */ }
        }

        // Pretty print rectangle and center
        private static string FRect(double L, double T, double W, double H)
            => $"L={L:F3},T={T:F3},W={W:F3},H={H:F3},CX={(L+W*0.5):F3},CY={(T+H*0.5):F3})";

        private static string FRoiImg(RoiModel r)
        {
            if (r == null) return "<null>";
            return $"Role={r.Role} Img(L={r.Left:F3},T={r.Top:F3},W={r.Width:F3},H={r.Height:F3},CX={r.CX:F3},CY={r.CY:F3},R={r.R:F3},Rin={r.RInner:F3})";
        }

        private struct ImgToCanvas
        {
            public double sx, sy, offX, offY;
        }

        private ImgToCanvas GetImageToCanvasTransform()
        {
            var bs = ImgMain?.Source as BitmapSource;
            if (bs == null || ImgMain == null)
                return new ImgToCanvas { sx = 1, sy = 1, offX = 0, offY = 0 };

            double imgW = bs.PixelWidth;
            double imgH = bs.PixelHeight;
            double viewW = ImgMain.ActualWidth;
            double viewH = ImgMain.ActualHeight;
            if (imgW <= 0 || imgH <= 0 || viewW <= 0 || viewH <= 0)
                return new ImgToCanvas { sx = 1, sy = 1, offX = 0, offY = 0 };

            double scale = Math.Min(viewW / imgW, viewH / imgH);
            double drawnW = imgW * scale;
            double drawnH = imgH * scale;
            double offX = (viewW - drawnW) * 0.5;
            double offY = (viewH - drawnH) * 0.5;

            return new ImgToCanvas
            {
                sx = scale,
                sy = scale,
                offX = offX,
                offY = offY
            };
        }

        private static double R(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

        private SWRect MapImageRectToCanvas(SWRect imageRect)
        {
            var t = GetImageToCanvasTransform();
            double L = t.offX + t.sx * imageRect.X;
            double T = t.offY + t.sy * imageRect.Y;
            double W = t.sx * imageRect.Width;
            double H = t.sy * imageRect.Height;
            return new SWRect(R(L), R(T), R(W), R(H));
        }

        private SWPoint MapImagePointToCanvas(SWPoint p)
        {
            var t = GetImageToCanvasTransform();
            return new SWPoint(R(t.offX + t.sx * p.X), R(t.offY + t.sy * p.Y));
        }

        private (SWPoint c, double rOuter, double rInner) MapImageCircleToCanvas(double cx, double cy, double rOuter, double rInner)
        {
            var t = GetImageToCanvasTransform();
            var c = new SWPoint(R(t.offX + t.sx * cx), R(t.offY + t.sy * cy));
            return (c, R(t.sx * rOuter), R(t.sx * rInner));
        }

        private void RoiDiagDumpTransform(string where)
        {
            try
            {
                int srcW = 0, srcH = 0;
                try
                {
                    var bs = ImgMain?.Source as System.Windows.Media.Imaging.BitmapSource;
                    if (bs != null) { srcW = bs.PixelWidth; srcH = bs.PixelHeight; }
                }
                catch { }

                double imgVW = ImgMain?.ActualWidth ?? 0;
                double imgVH = ImgMain?.ActualHeight ?? 0;
                double canW = CanvasROI?.ActualWidth ?? 0;
                double canH = CanvasROI?.ActualHeight ?? 0;

                var t = GetImageToCanvasTransform();
                double sx = t.sx, sy = t.sy, offX = t.offX, offY = t.offY;

                RoiDiagLog($"[{where}] ImgSrc={srcW}x{srcH} ImgView={imgVW:F3}x{imgVH:F3} CanvasROI={canW:F3}x{canH:F3}  Transform: sx={sx:F9}, sy={sy:F9}, offX={offX:F3}, offY={offY:F3}  Stretch={ImgMain?.Stretch}");
            }
            catch (System.Exception ex)
            {
                RoiDiagLog($"[{where}] DumpTransform EX: {ex.Message}");
            }
        }

        // Convert image→canvas for a RoiModel using existing project conversion
        private System.Windows.Rect RoiDiagImageToCanvasRect(RoiModel r)
        {
            // Use existing method that the app already relies on
            var rc = ImageToCanvas(r);
            return new System.Windows.Rect(rc.Left, rc.Top, rc.Width, rc.Height);
        }

        // Try to find a UI element for a ROI and read its actual canvas placement
        private bool RoiDiagTryFindUiRect(RoiModel r, out System.Windows.Rect uiRect, out string name)
        {
            uiRect = new System.Windows.Rect(); name = "";
            try
            {
                if (CanvasROI == null || r == null) return false;
                // Heuristics: children that are FrameworkElement with Name/Tag containing the Role or "roi"
                foreach (var o in CanvasROI.Children)
                {
                    if (o is System.Windows.FrameworkElement fe)
                    {
                        var tag = fe.Tag as string;
                        var nm  = fe.Name ?? "";
                        bool matches =
                            (!string.IsNullOrEmpty(tag) && tag.IndexOf("roi", System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (!string.IsNullOrEmpty(nm)  && nm.IndexOf(r.Role.ToString(), System.StringComparison.OrdinalIgnoreCase) >= 0);

                        if (!matches) continue;

                        double L = System.Windows.Controls.Canvas.GetLeft(fe);
                        double T = System.Windows.Controls.Canvas.GetTop(fe);
                        double W = fe.ActualWidth;
                        double H = fe.ActualHeight;
                        if (double.IsNaN(L)) L = 0;
                        if (double.IsNaN(T)) T = 0;
                        uiRect = new System.Windows.Rect(L, T, W, H);
                        name = string.IsNullOrEmpty(nm) ? (tag ?? fe.GetType().Name) : nm;
                        return true;
                    }
                }
            }
            catch {}
            return false;
        }

        // Dump expected canvas rect vs. actual UI rect (if any)
        private void RoiDiagDumpRoi(string where, string label, RoiModel r)
        {
            try
            {
                if (r == null)
                {
                    RoiDiagLog($"[{where}] {label}: <null>");
                    return;
                }
                var rcExp = RoiDiagImageToCanvasRect(r);
                string line = $"[{where}] {label}: IMG({FRoiImg(r)})  EXP-CANVAS({FRect(rcExp.Left, rcExp.Top, rcExp.Width, rcExp.Height)})";

                if (RoiDiagTryFindUiRect(r, out var rcUi, out var nm))
                {
                    double dx = rcUi.Left - rcExp.Left;
                    double dy = rcUi.Top  - rcExp.Top;
                    double dw = rcUi.Width  - rcExp.Width;
                    double dh = rcUi.Height - rcExp.Height;
                    line += $"  UI[{nm}]({FRect(rcUi.Left, rcUi.Top, rcUi.Width, rcUi.Height)})  Δpos=({dx:F3},{dy:F3}) Δsize=({dw:F3},{dh:F3})";
                }
                RoiDiagLog(line);
            }
            catch (System.Exception ex)
            {
                RoiDiagLog($"[{where}] {label}: EX: {ex.Message}");
            }
        }

        // Snapshot of ALL canvas children (for forensic inspection)
        private void RoiDiagDumpCanvasChildren(string where)
        {
            try
            {
                if (CanvasROI == null) { RoiDiagLog($"[{where}] CanvasROI=<null>"); return; }
                RoiDiagLog($"[{where}] CanvasROI children count = {CanvasROI.Children.Count}");
                foreach (var o in CanvasROI.Children)
                {
                    if (o is System.Windows.FrameworkElement fe)
                    {
                        double L = System.Windows.Controls.Canvas.GetLeft(fe);
                        double T = System.Windows.Controls.Canvas.GetTop(fe);
                        double W = fe.ActualWidth;
                        double H = fe.ActualHeight;
                        if (double.IsNaN(L)) L = 0;
                        if (double.IsNaN(T)) T = 0;
                        string nm = fe.Name ?? fe.GetType().Name;
                        string tg = fe.Tag?.ToString() ?? "";
                        RoiDiagLog($"    FE: {nm}  Tag='{tg}'  {FRect(L,T,W,H)}  Z={System.Windows.Controls.Panel.GetZIndex(fe)}");
                    }
                    else
                    {
                        RoiDiagLog($"    Child: {o?.GetType().Name ?? "<null>"}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                RoiDiagLog($"[{where}] DumpCanvasChildren EX: {ex.Message}");
            }
        }

        private static readonly string HeatmapLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector", "logs", "gui_heatmap.log");

        private static void LogHeatmap(string msg)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(HeatmapLogPath);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);
                System.IO.File.AppendAllText(HeatmapLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { /* swallow logging errors */ }
        }

        private void KeepOnlyMaster2InCanvas()
        {
            if (CanvasROI == null) return;

            var toRemove = new System.Collections.Generic.List<System.Windows.UIElement>();
            int kept = 0, removed = 0;

            foreach (var el in CanvasROI.Children.Cast<System.Windows.UIElement>())
            {
                switch (el)
                {
                    case System.Windows.Shapes.Shape s:
                    {
                        // Shapes de ROI llevan Tag = RoiModel; los de análisis suelen llevar string ("analysis-mark"/"AnalysisCross") o null
                        if (s.Tag is RoiModel rm)
                        {
                            bool keep = (rm.Role == RoiRole.Master2Pattern) || (rm.Role == RoiRole.Master2Search);
                            if (!keep) { toRemove.Add(s); removed++; } else { kept++; }
                        }
                        else
                        {
                            // Cualquier shape sin RoiModel en Tag NO pertenece a ROI Master 2 (líneas/analysis/etc.) → eliminar
                            toRemove.Add(s); removed++;
                        }
                        break;
                    }

                    case FrameworkElement fe when fe.Name != null && fe.Name.StartsWith("roiLabel_"):
                    {
                        // Las etiquetas de ROI no usan Tag; se nombran como "roiLabel_<texto_sin_espacios>"
                        // Para "Master 2" el Name es "roiLabel_Master_2"
                        string name = fe.Name;
                        bool keep = name.StartsWith("roiLabel_Master_2", System.StringComparison.OrdinalIgnoreCase);
                        if (!keep) { toRemove.Add(fe); removed++; } else { kept++; }
                        break;
                    }

                    default:
                        // Cualquier otro UIElement (Borders de análisis, etc.) → eliminar
                        toRemove.Add(el); removed++;
                        break;
                }
            }

            foreach (var el in toRemove)
                CanvasROI.Children.Remove(el);

            try { LogHeatmap($"KeepOnlyMaster2InCanvas: kept={kept}, removed={removed}"); } catch {}
        }

        private static string RoiDebug(RoiModel r)
        {
            if (r == null) return "<null>";
            string shape = InferShapeName(r);
            return $"Role={r.Role}, Shape={shape}, Img(L={r.Left},T={r.Top},W={r.Width},H={r.Height},CX={r.CX},CY={r.CY},R={r.R},Rin={r.RInner})";
        }

        private static string InferShapeName(RoiModel r)
        {
            try
            {
                // Annulus if both outer and inner radii are present/positive
                if (r.RInner > 0 && r.R > 0) return "Annulus";

                // Circle if we have a positive radius OR bbox looks square-ish
                if (r.R > 0) return "Circle";

                // As a fallback, detect square-ish bbox as circle, else rectangle
                // (Tolerance = 2% of the larger side)
                double w = r.Width, h = r.Height;
                double tol = 0.02 * System.Math.Max(System.Math.Abs(w), System.Math.Abs(h));
                if (System.Math.Abs(w - h) <= tol && w > 0 && h > 0) return "Circle";

                return "Rectangle";
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string RectDbg(System.Windows.Rect rc)
            => $"(X={rc.X:F2},Y={rc.Y:F2},W={rc.Width:F2},H={rc.Height:F2})";
        // ---------- End helpers ----------


        private void UpdateRoiLabelPosition(Shape shape)
        {
            if (shape == null) return;

            // Accept both ROI (Legend) and RoiModel (Label)
            object tag = shape.Tag;
            string legendOrLabel = null;
            if (tag is ROI rTag)        legendOrLabel = rTag.Legend;
            else if (tag is RoiModel m) legendOrLabel = m.Label;

            string labelName = "roiLabel_" + ((legendOrLabel ?? string.Empty).Replace(" ", "_"));
            var label = CanvasROI.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(fe => fe.Name == labelName);
            if (label == null) return;

            // If Left/Top are not ready yet, defer positioning to next layout pass
            double left = Canvas.GetLeft(shape);
            double top  = Canvas.GetTop(shape);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateRoiLabelPosition(shape)), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            RoiModel? roi = null;
            if (tag is RoiModel modelTag)
                roi = modelTag;
            else if (tag is ROI legacy && legacy is not null)
            {
                // legacy ROI lacks shape info; best-effort fallback using rectangle bbox
                roi = new RoiModel
                {
                    Shape = RoiShape.Rectangle,
                    Left = Canvas.GetLeft(shape),
                    Top = Canvas.GetTop(shape),
                    Width = shape.Width,
                    Height = shape.Height
                };
            }

            if (roi == null)
                return;

            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            switch (roi.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    var roiImg = CanvasToImage(roi);
                    PlaceLabelOnCircle(label, roiImg, 30.0);
                    break;
                default:
                    Canvas.SetLeft(label, roi.Left + 6);
                    Canvas.SetTop(label,  roi.Top - 6 - label.DesiredSize.Height);
                    label.RenderTransform = null;
                    break;
            }
        }

        private void EnsureRoiLabel(Shape shape, object roi)
        {
            if (CanvasROI == null)
                return;

            _roiLabels.TryGetValue(shape, out var previous);

            // Build label text and key supporting both types
            string _lbl;
            if (roi is LegacyROI rObj)
                _lbl = string.IsNullOrWhiteSpace(rObj.Legend) ? "ROI" : rObj.Legend;
            else if (roi is RoiModel mObj)
                _lbl = string.IsNullOrWhiteSpace(mObj.Label) ? "ROI" : mObj.Label;
            else
                _lbl = "ROI";

            string labelName = "roiLabel_" + _lbl.Replace(" ", "_");
            var existing = CanvasROI.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(fe => fe.Name == labelName);
            FrameworkElement label;

            if (existing == null)
            {
                label = CreateStyledLabel(_lbl);
                label.Name = labelName;
                label.IsHitTestVisible = false;
            }
            else
            {
                label = existing;
                label.IsHitTestVisible = false;
                if (label is System.Windows.Controls.Border border && border.Child is System.Windows.Controls.TextBlock tb)
                {
                    tb.Text = _lbl;
                }
            }

            var overlayTag = BuildOverlayTag(roi as RoiModel);
            if (overlayTag == "roi:unknown" && !string.IsNullOrWhiteSpace(_lbl))
            {
                var sanitized = new string(_lbl.Where(char.IsLetterOrDigit).ToArray());
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    sanitized = Guid.NewGuid().ToString("N");
                }

                overlayTag = $"roi:{sanitized}";
            }

            label.Tag = overlayTag;

            EnsureRoiCheckbox(_lbl);

            if (existing == null)
            {
                CanvasROI.Children.Add(label);
                Panel.SetZIndex(label, int.MaxValue);
            }

            if (previous != null && !ReferenceEquals(previous, label))
            {
                bool usedElsewhere = _roiLabels.Any(kv => !ReferenceEquals(kv.Key, shape) && ReferenceEquals(kv.Value, previous));
                if (!usedElsewhere && CanvasROI.Children.Contains(previous))
                {
                    CanvasROI.Children.Remove(previous);
                }
            }

            _roiLabels[shape] = label;
        }

        private System.Windows.Controls.StackPanel GetOrCreateRoiChecksHost()
        {
            if (_roiChecksPanel != null) return _roiChecksPanel;

            // Intentar ubicar un panel de controles existente por nombre común
            string[] knownHosts = { "ControlsPanel", "RightPanel", "SidebarPanel", "RightToolbar" };
            foreach (var hostName in knownHosts)
            {
                if (this.FindName(hostName) is System.Windows.Controls.Panel p)
                {
                    var sp = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Vertical,
                        Margin = new System.Windows.Thickness(6)
                    };
                    p.Children.Add(new System.Windows.Controls.GroupBox
                    {
                        Header = "Overlays",
                        Content = sp,
                        Margin = new System.Windows.Thickness(6,12,6,6)
                    });
                    _roiChecksPanel = sp;
                    return _roiChecksPanel;
                }
            }

            // Fallback: crear un host flotante en la esquina superior derecha del root Grid
            if (this.Content is System.Windows.Controls.Grid rootGrid)
            {
                var sp = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    Margin = new System.Windows.Thickness(8)
                };
                var box = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 30, 30, 30)),
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Padding = new System.Windows.Thickness(8),
                    Child = new System.Windows.Controls.GroupBox
                    {
                        Header = "Overlays",
                        Content = sp
                    },
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top
                };
                System.Windows.Controls.Grid.SetRow(box, 0);
                rootGrid.Children.Add(box);
                System.Windows.Controls.Panel.SetZIndex(box, 9999);
                _roiChecksPanel = sp;
                return _roiChecksPanel;
            }

            // Último recurso: crear un panel local (no persistente visualmente si no hay contenedor)
            _roiChecksPanel = new System.Windows.Controls.StackPanel();
            return _roiChecksPanel;
        }

        private void WireExistingHeatmapControls()
        {
            _chkHeatmap ??= FindName("ChkHeatmap") as CheckBox;
            if (_chkHeatmap != null && !_heatmapCheckboxEventsHooked)
            {
                _chkHeatmap.Checked += (_, __) =>
                {
                    if (HeatmapOverlay != null) HeatmapOverlay.Visibility = Visibility.Visible;
                };
                _chkHeatmap.Unchecked += (_, __) =>
                {
                    if (HeatmapOverlay != null) HeatmapOverlay.Visibility = Visibility.Collapsed;
                };
                _heatmapCheckboxEventsHooked = true;
            }

            _sldHeatmapScale ??= FindName("HeatmapScaleSlider") as Slider;
            _sldHeatmapOpacity ??= FindName("HeatmapOpacitySlider") as Slider;
            _lblHeatmapScale ??= FindName("HeatmapScaleLabel") as TextBlock;

            if (_sldHeatmapScale != null)
            {
                _sldHeatmapScale.Minimum = 0.10;
                _sldHeatmapScale.Maximum = 2.00;
                _sldHeatmapScale.Value = _heatmapNormMax;

                if (!_heatmapSliderEventsHooked)
                {
                    _sldHeatmapScale.ValueChanged += (_, __) =>
                    {
                        _heatmapNormMax = _sldHeatmapScale!.Value;
                        if (_lblHeatmapScale != null)
                            _lblHeatmapScale.Text = $"Heatmap Scale: {_heatmapNormMax:0.00}";
                        try { RebuildHeatmapOverlayFromCache(); } catch { /* safe no-op */ }
                    };
                    _sldHeatmapScale.ValueChanged += HeatmapScaleSlider_ValueChangedSync;
                    _heatmapSliderEventsHooked = true;
                }
            }

            if (_sldHeatmapOpacity != null)
            {
                _sldHeatmapOpacity.Minimum = 0.0;
                _sldHeatmapOpacity.Maximum = 1.0;
                _sldHeatmapOpacity.SetCurrentValue(RangeBase.ValueProperty, _heatmapOverlayOpacity);
            }

            if (_lblHeatmapScale != null)
            {
                _lblHeatmapScale.Text = $"Heatmap Scale: {_heatmapNormMax:0.00}";
            }

            if (_chkHeatmap != null && HeatmapOverlay != null)
            {
                HeatmapOverlay.Visibility = _chkHeatmap.IsChecked == true
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void EnsureRoiCheckbox(string labelText)
        {
            if (string.IsNullOrWhiteSpace(labelText)) return;
            var host = GetOrCreateRoiChecksHost();

            // Buscar si ya existe un CheckBox con ese mismo texto
            foreach (var child in host.Children)
            {
                if (child is System.Windows.Controls.CheckBox cb && cb.Content is string s && s.Equals(labelText, System.StringComparison.OrdinalIgnoreCase))
                {
                    // Ya existe; nada que hacer
                    return;
                }
            }

            // Crear nuevo checkbox (sin lógica de toggle sobre shapes; solo UI, según requisito)
            var chk = new System.Windows.Controls.CheckBox
            {
                Content = labelText,
                IsChecked = true,
                Margin = new System.Windows.Thickness(2, 0, 2, 2)
            };
            chk.Foreground = Brushes.White;
            chk.FontSize = 13;
            host.Children.Add(chk);
        }

        private void RemoveRoiLabel(Shape shape)
        {
            if (CanvasROI == null)
                return;

            if (_roiLabels.TryGetValue(shape, out var label))
            {
                CanvasROI.Children.Remove(label);
                _roiLabels.Remove(shape);
            }
        }

        private bool _syncScheduled;
        private bool _isSyncRunning;
        private int _syncSeq;
        private System.Windows.Rect _lastDisplayRect = System.Windows.Rect.Empty;
        private const double DisplayEps = 0.5;
        private int _suppressSaves;
        // overlay diferido
        private bool _overlayNeedsRedraw;
        private bool _adornerHadDelta;
        private bool _analysisViewActive;

        private void AppendResizeLog(string msg)
        {
            try
            {
                if (!System.IO.Directory.Exists(_resizeLogDir))
                    System.IO.Directory.CreateDirectory(_resizeLogDir);
                string path = System.IO.Path.Combine(_resizeLogDir, $"resize-debug-{DateTime.Now:yyyyMMdd}.txt");
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // swallow logging errors
            }
        }
        public MainWindow()
        {
            // CODEX: logs de arranque de ventana
            GuiLog.Info($"[BOOT] MainWindow ctor ENTER"); // CODEX: string interpolation compatibility.

            try
            {
                GuiLog.Info($"[BOOT] MainWindow ctor → InitializeComponent()"); // CODEX: string interpolation compatibility.
                InitializeComponent();
                GuiLog.Info($"[BOOT] MainWindow ctor → InitializeComponent() OK"); // CODEX: string interpolation compatibility.

                if (this.DataContext == null)
                {
                    this.DataContext = this;
                    GuiLog.Info($"[BOOT] MainWindow ctor → DataContext asignado a self"); // CODEX: string interpolation compatibility.
                }
                else
                {
                    GuiLog.Info($"[BOOT] MainWindow ctor → DataContext ya estaba asignado"); // CODEX: string interpolation compatibility.
                }

                GuiLog.Info($"[BOOT] MainWindow ctor → ApplyDrawToolSelection()"); // CODEX: string interpolation compatibility.
                ApplyDrawToolSelection(_currentDrawTool, updateViewModel: false);
                GuiLog.Info($"[BOOT] MainWindow ctor → ApplyDrawToolSelection OK"); // CODEX: string interpolation compatibility.

                this.SizeChanged += (s,e) =>
                {
                    try
                    {
                        RoiDiagDumpTransform("sizechanged");
                        if (_layout != null)
                        {
                            RoiDiagDumpRoi("sizechanged", "Master1Pattern", _layout.Master1Pattern);
                            RoiDiagDumpRoi("sizechanged", "Master1Search ", _layout.Master1Search);
                            RoiDiagDumpRoi("sizechanged", "Master2Pattern", _layout.Master2Pattern);
                            RoiDiagDumpRoi("sizechanged", "Master2Search ", _layout.Master2Search);
                            RoiDiagDumpRoi("sizechanged", "Inspection   ", _layout.Inspection);
                        }
                        if (_lastHeatmapRoi != null)
                        {
                            RoiDiagDumpRoi("sizechanged", "HeatmapROI   ", _lastHeatmapRoi);
                        }
                        Dispatcher.InvokeAsync(() => RoiDiagDumpCanvasChildren("sizechanged:UI-snapshot"),
                                               System.Windows.Threading.DispatcherPriority.Render);
                    }
                    catch {}
                };

                try
                {
                    var ps = System.Windows.PresentationSource.FromVisual(this);
                    if (ps?.CompositionTarget != null)
                    {
                        Matrix m = ps.CompositionTarget.TransformToDevice;
                        LogHeatmap($"DPI Scale = ({m.M11:F3}, {m.M22:F3})");
                    }
                }
                catch {}

                // RoiOverlay disabled: labels are now drawn on Canvas only
                // RoiOverlay.BindToImage(ImgMain);

                // RoiOverlay disabled: labels are now drawn on Canvas only
                // ImgMain.SizeChanged += (_, __) => RoiOverlay.InvalidateOverlay();
                // SizeChanged += (_, __) => RoiOverlay.InvalidateOverlay();
                _preset = PresetManager.LoadOrDefault(_preset);

                // Start with an empty in-memory layout; users can load a specific layout
                // explicitly via the "Load Selected" action in the Layouts panel.
                _layout = new MasterLayout();

                var layoutStore = new FileLayoutProfileStore(_preset);
                LayoutProfiles = new LayoutProfilesViewModel(layoutStore, this);
                OnPropertyChanged(nameof(LayoutProfiles));
                LayoutProfiles.RefreshCommand.Execute(null);
                ApplyInspectionPresetToUI(_preset);
                InitializeOptionsFromConfig();

                InitUI();
                InitTrainPollingTimer();
                HookCanvasInput();
                InitWorkflow();

                ImgMain.SizeChanged += ImgMain_SizeChanged;
                this.SizeChanged += MainWindow_SizeChanged;
                this.Loaded += MainWindow_Loaded;

                RemoveAllRoiAdorners();

                GuiLog.Info($"[BOOT] MainWindow ctor EXIT OK"); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[BOOT] MainWindow ctor FAILED", ex); // CODEX: string interpolation compatibility.
                throw; // importante: re-lanzar para que VS lo muestre
            }
        }

        private void InitializeOptionsFromConfig()
        {
            double defaultPosTol = _appConfig.Analyze?.PosTolPx > 0 ? _appConfig.Analyze.PosTolPx : 1.0;
            double defaultAngTol = _appConfig.Analyze?.AngTolDeg > 0 ? _appConfig.Analyze.AngTolDeg : 0.5;
            bool defaultScaleLock = _appConfig.Analyze?.ScaleLockDefault ?? true;
            double defaultOpacity = _appConfig.UI?.HeatmapOverlayOpacity ?? 0.6;
            defaultOpacity = Math.Max(0.0, Math.Min(1.0, defaultOpacity));

            var layoutAnalyze = _layout?.Analyze;
            var layoutUi = _layout?.Ui;

            double posTol = layoutAnalyze != null && layoutAnalyze.PosTolPx > 0 ? layoutAnalyze.PosTolPx : defaultPosTol;
            double angTol = layoutAnalyze != null && layoutAnalyze.AngTolDeg > 0 ? layoutAnalyze.AngTolDeg : defaultAngTol;
            bool scaleLock = layoutAnalyze?.ScaleLock ?? defaultScaleLock;
            double opacity = layoutUi != null && layoutUi.HeatmapOverlayOpacity >= 0
                ? Math.Max(0.0, Math.Min(1.0, layoutUi.HeatmapOverlayOpacity))
                : defaultOpacity;
            double gain = layoutUi != null && layoutUi.HeatmapGain > 0
                ? layoutUi.HeatmapGain
                : 1.0;
            double gamma = layoutUi != null && layoutUi.HeatmapGamma > 0
                ? layoutUi.HeatmapGamma
                : 1.0;
            gain = Math.Clamp(gain, 0.25, 4.0);
            gamma = Math.Clamp(gamma, 0.25, 4.0);

            _inspectionBaselinesByImage.Clear();
            if (_layout?.InspectionBaselinesByImage != null)
            {
                foreach (var kv in _layout.InspectionBaselinesByImage)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    {
                        continue;
                    }

                    _inspectionBaselinesByImage[kv.Key] = kv.Value
                        .Where(r => r != null)
                        .Select(r => r.Clone())
                        .ToList();
                }
            }

            _isInitializingOptions = true;
            try
            {
                ScaleLock = scaleLock;
                AnalyzePosTolerancePx = posTol;
                AnalyzeAngToleranceDeg = angTol;
                HeatmapGain = gain;
                HeatmapGamma = gamma;
                HeatmapOverlayOpacity = opacity;
                if (ChkUseLocalMatcher != null)
                {
                    ChkUseLocalMatcher.IsChecked = layoutAnalyze?.UseLocalMatcher ?? true;
                }
            }
            finally
            {
                _isInitializingOptions = false;
            }

            PersistAnalyzeOptions();
            PersistUiOptions();
        }

        private string EnsureDataRoot()
        {
            string? configured = _appConfig.Dataset?.Root;
            string root;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var trimmed = configured.Trim();
                root = Path.IsPathRooted(trimmed)
                    ? trimmed
                    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
            }
            else
            {
                root = Path.Combine(AppContext.BaseDirectory, "data");
            }

            Directory.CreateDirectory(root);

            var imagesRoot = Path.Combine(root, "images");
            Directory.CreateDirectory(imagesRoot);
            Directory.CreateDirectory(Path.Combine(imagesRoot, "ok"));
            Directory.CreateDirectory(Path.Combine(imagesRoot, "ng"));

            Directory.CreateDirectory(Path.Combine(root, "rois"));

            return root;
        }

        private void EnsureInspectionDatasetStructure()
        {
            if (_layout?.InspectionRois == null)
            {
                return;
            }

            _dataRoot ??= EnsureDataRoot();

            var roisRoot = Path.Combine(_dataRoot, "rois");
            Directory.CreateDirectory(roisRoot);

            foreach (var roi in _layout.InspectionRois)
            {
                var folderName = $"Inspection_{roi.Index}";
                var roiDir = Path.Combine(roisRoot, folderName);
                Directory.CreateDirectory(roiDir);
                Directory.CreateDirectory(Path.Combine(roiDir, "ok"));
                Directory.CreateDirectory(Path.Combine(roiDir, "ng"));
                Directory.CreateDirectory(Path.Combine(roiDir, "Model"));

                if (!string.Equals(roi.DatasetPath, roiDir, StringComparison.OrdinalIgnoreCase))
                {
                    roi.DatasetPath = roiDir;
                }
            }
        }

        private string GetInspectionModelFolder(int inspectionIndex)
        {
            _dataRoot ??= EnsureDataRoot();

            int clamped = Math.Max(1, Math.Min(4, inspectionIndex));
            var roisRoot = Path.Combine(_dataRoot, "rois");
            Directory.CreateDirectory(roisRoot);

            var folderName = $"Inspection_{clamped}";
            var roiDir = Path.Combine(roisRoot, folderName);
            Directory.CreateDirectory(roiDir);

            var modelDir = Path.Combine(roiDir, "Model");
            Directory.CreateDirectory(modelDir);
            return modelDir;
        }

        private string? ResolveInspectionModelDirectory(InspectionRoiConfig roi)
            => roi == null ? null : GetInspectionModelFolder(roi.Index);

        private void InitWorkflow()
        {
            try
            {
                _dataRoot = EnsureDataRoot();

                var backendClient = new Workflow.BackendClient();
                if (!string.IsNullOrWhiteSpace(BackendAPI.BaseUrl))
                {
                    backendClient.BaseUrl = BackendAPI.BaseUrl;
                }

                _backendClient = backendClient;

                var datasetManager = new DatasetManager(_dataRoot);
                if (_workflowViewModel != null)
                {
                    _workflowViewModel.PropertyChanged -= WorkflowViewModelOnPropertyChanged;
                    _workflowViewModel.OverlayVisibilityChanged -= WorkflowViewModelOnOverlayVisibilityChanged;
                    _workflowViewModel.BatchStarted -= WorkflowViewModelOnBatchStarted;
                    _workflowViewModel.BatchEnded -= WorkflowViewModelOnBatchEnded;
                }

                _workflowViewModel = new WorkflowViewModel(
                    backendClient,
                    datasetManager,
                    ExportCurrentRoiCanonicalAsync,
                    () => _currentImagePathWin,
                    AppendLog,
                    ShowHeatmapAsync,
                    ClearHeatmapOverlay,
                    UpdateGlobalBadge,
                    SetActiveInspectionIndex,
                    ResolveInspectionModelDirectory,
                    RepositionBeforeBatchStepAsync);

                _workflowViewModel.AnchorScoreMin = Math.Max(1, _appConfig.Analyze.AnchorScoreMin);
                _sharedHeatmapGuardLogged = false;

                _workflowViewModel.PropertyChanged += WorkflowViewModelOnPropertyChanged;
                _workflowViewModel.OverlayVisibilityChanged += WorkflowViewModelOnOverlayVisibilityChanged;
                _workflowViewModel.BatchStarted += WorkflowViewModelOnBatchStarted;
                _workflowViewModel.BatchEnded += WorkflowViewModelOnBatchEnded;
                _workflowViewModel.IsImageLoaded = _hasLoadedImage;
                _workflowViewModel.HeatmapOpacity = _heatmapOverlayOpacity;

                if (WorkflowHost != null)
                {
                    WorkflowHost.LoadModelRequested -= WorkflowHostOnLoadModelRequested;
                    WorkflowHost.LoadModelRequested += WorkflowHostOnLoadModelRequested;
                    WorkflowHost.ToggleEditRequested -= WorkflowHostOnToggleEditRequested;
                    WorkflowHost.ToggleEditRequested += WorkflowHostOnToggleEditRequested;
                    WorkflowHost.DataContext = _workflowViewModel;
                }

                EnsureInspectionDatasetStructure();
                _workflowViewModel.SetMasterLayout(_layout);
                _workflowViewModel.SetInspectionRoisCollection(_layout?.InspectionRois);

                SyncDrawToolFromViewModel();
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[workflow] init error: {ex.Message}");
            }
        }

        private void InitUI()
        {
            ComboFeature.SelectedIndex = 0;
            ComboMasterRoiRole.ItemsSource = new[] { "ROI Master 1", "ROI Inspección Master 1" };
            ComboMasterRoiRole.SelectedIndex = 0;


            ComboMasterRoiShape.Items.Clear();
            ComboMasterRoiShape.Items.Add("Rectángulo");
            ComboMasterRoiShape.Items.Add("Círculo");
            ComboMasterRoiShape.Items.Add("Annulus");
            ComboMasterRoiShape.SelectedIndex = 0;


            ComboM2Shape.SelectedIndex = 0;


            ComboM2Role.ItemsSource = new[] { "ROI Master 2", "ROI Inspección Master 2" };
            ComboM2Role.SelectedIndex = 0;

            InitRoiVisibilityControls();

            UpdateWizardState();
            ApplyPresetToUI(_preset);
        }

        private string GetInspectionShapeFromModel()
        {
            if (ViewModel?.SelectedInspectionRoi != null)
            {
                return ViewModel.SelectedInspectionRoi.Shape.ToString().ToLowerInvariant();
            }

            return _currentDrawTool.ToString().ToLowerInvariant();
        }

        private InspectionRoiConfig? GetInspectionConfigByIndex(int index)
        {
            if (ViewModel?.InspectionRois == null)
            {
                return null;
            }

            return ViewModel.InspectionRois.FirstOrDefault(r => r.Index == index);
        }

        private bool GetShowMaster1Pattern() => ViewModel?.ShowMaster1Pattern ?? true;
        private bool GetShowMaster1Inspection() => ViewModel?.ShowMaster1Inspection ?? true;
        private bool GetShowMaster2Pattern() => ViewModel?.ShowMaster2Pattern ?? true;
        private bool GetShowMaster2Inspection() => ViewModel?.ShowMaster2Inspection ?? true;
        private bool GetShowInspectionRoi() => ViewModel?.ShowInspectionRoi ?? true;

        private bool GetRoiVisibility(RoiRole role) => role switch
        {
            RoiRole.Master1Pattern => GetShowMaster1Pattern(),
            RoiRole.Master1Search => GetShowMaster1Inspection(),
            RoiRole.Master2Pattern => GetShowMaster2Pattern(),
            RoiRole.Master2Search => GetShowMaster2Inspection(),
            RoiRole.Inspection => GetShowInspectionRoi(),
            _ => true
        };

        private void SetRoiVisibility(RoiRole role, bool visible)
        {
            if (ViewModel == null)
            {
                return;
            }

            switch (role)
            {
                case RoiRole.Master1Pattern:
                    if (ViewModel.ShowMaster1Pattern != visible)
                    {
                        ViewModel.ShowMaster1Pattern = visible;
                    }
                    break;
                case RoiRole.Master1Search:
                    if (ViewModel.ShowMaster1Inspection != visible)
                    {
                        ViewModel.ShowMaster1Inspection = visible;
                    }
                    break;
                case RoiRole.Master2Pattern:
                    if (ViewModel.ShowMaster2Pattern != visible)
                    {
                        ViewModel.ShowMaster2Pattern = visible;
                    }
                    break;
                case RoiRole.Master2Search:
                    if (ViewModel.ShowMaster2Inspection != visible)
                    {
                        ViewModel.ShowMaster2Inspection = visible;
                    }
                    break;
                case RoiRole.Inspection:
                    if (ViewModel.ShowInspectionRoi != visible)
                    {
                        ViewModel.ShowInspectionRoi = visible;
                    }
                    break;
            }
        }

        private void EnablePresetsTab(bool enable)
        {
            if (TabSetupInspect != null && !TabSetupInspect.IsEnabled)
            {
                TabSetupInspect.IsEnabled = true;
            }

            var roiGroup = FindName("InspectionRoisGroup") as System.Windows.Controls.GroupBox;
            if (roiGroup != null)
            {
                roiGroup.IsEnabled = enable;
            }
        }

        private void InitRoiVisibilityControls()
        {
            _roiCheckboxHasRoi.Clear();

            UpdateRoiVisibilityControls();
        }

        private void UpdateRoiVisibilityControls()
        {
            UpdateRoiVisibilityState(RoiRole.Master1Pattern, _layout.Master1Pattern);
            UpdateRoiVisibilityState(RoiRole.Master1Search, _layout.Master1Search);
            UpdateRoiVisibilityState(RoiRole.Master2Pattern, _layout.Master2Pattern);
            UpdateRoiVisibilityState(RoiRole.Master2Search, _layout.Master2Search);
            UpdateRoiVisibilityState(RoiRole.Inspection, GetFirstInspectionRoi());

            RequestRoiVisibilityRefresh();
            UpdateRoiHud();
        }

        private void UpdateRoiVisibilityState(RoiRole role, RoiModel? model)
        {
            bool hasRoi = model != null;
            bool prevHasRoi = _roiCheckboxHasRoi.TryGetValue(role, out var prev) && prev;

            if (!hasRoi)
            {
                SetRoiVisibility(role, false);
            }
            else if (!prevHasRoi && !GetRoiVisibility(role))
            {
                SetRoiVisibility(role, true);
            }

            _roiCheckboxHasRoi[role] = hasRoi;
        }

        private void RoiVisibilityCheckChanged(object sender, RoutedEventArgs e)
        {
            RequestRoiVisibilityRefresh();
            UpdateRoiHud();
        }

        private void RequestRoiVisibilityRefresh()
        {
            if (_roiVisibilityRefreshPending)
                return;

            _roiVisibilityRefreshPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _roiVisibilityRefreshPending = false;
                ApplyRoiVisibilityFromViewModel();
            }), DispatcherPriority.Render);
        }

        private void ApplyRoiVisibilityFromViewModel()
        {
            if (CanvasROI == null)
                return;

            foreach (var shape in CanvasROI.Children.OfType<Shape>())
            {
                if (shape.Tag is not RoiModel roi)
                    continue;

                bool visible = IsRoiRoleVisible(roi.Role);
                if (visible)
                {
                    shape.Visibility = Visibility.Visible;

                    if (_roiLabels.TryGetValue(shape, out var label) && label != null)
                    {
                        if (!CanvasROI.Children.Contains(label))
                        {
                            CanvasROI.Children.Add(label);
                        }
                        label.Visibility = Visibility.Visible;
                    }

                    bool allowEditing = !roi.IsFrozen && ShouldEnableRoiEditing(roi.Role, roi);
                    if (allowEditing)
                    {
                        AttachRoiAdorner(shape);
                    }
                    else
                    {
                        RemoveRoiAdorners(shape);
                    }
                }
                else
                {
                    shape.Visibility = Visibility.Collapsed;

                    if (shape.Tag is RoiModel hiddenRoi)
                    {
                        var roiId = hiddenRoi.Id ?? hiddenRoi.Role.ToString();
                        OverlayCleanup.RemoveForRoi(CanvasROI, shape, roiId);
                        _roiLabels.Remove(shape);
                    }
                    else if (_roiLabels.TryGetValue(shape, out var label) && label != null)
                    {
                        label.Visibility = Visibility.Collapsed;
                        if (CanvasROI.Children.Contains(label))
                        {
                            CanvasROI.Children.Remove(label);
                        }
                        _roiLabels.Remove(shape);
                        RemoveRoiAdorners(shape);
                    }
                }
            }
        }

        private bool IsRoiRoleVisible(RoiRole role)
        {
            return GetRoiVisibility(role);
        }

        private void UpdateWizardState()
        {
            bool m1Ready = _layout.Master1Pattern != null && _layout.Master1Search != null;
            bool m2Ready = _layout.Master2Pattern != null && _layout.Master2Search != null;
            bool mastersReady = m1Ready && m2Ready;

            TxtMasterHints.Text = _state switch
            {
                MasterState.DrawM1_Pattern => "1) Dibuja el ROI del patrón Master 1. 2) Cambia a 'ROI Inspección Master 1' para delimitar la zona de búsqueda. Usa rectángulo, círculo o annulus.",
                MasterState.DrawM1_Search => "Dibuja la zona de búsqueda para Master 1 y pulsa Guardar.",
                MasterState.DrawM2_Pattern => "Dibuja el ROI del patrón Master 2.",
                MasterState.DrawM2_Search => "Dibuja la zona de búsqueda para Master 2 y pulsa Guardar.",
                MasterState.DrawInspection => "Dibuja el ROI de Inspección (rect/círc/annulus) y pulsa Guardar.",
                MasterState.Ready => "Pulsa 'Analizar Master' para localizar centros y reubicar el ROI de Inspección.",
                _ => ""
            };

            // Habilitación de tabs por etapas
            TabSetupInspect.IsEnabled = true;
            EnablePresetsTab(mastersReady || _hasLoadedImage);     // permite la pestaña de inspección tras cargar imagen o completar masters

            // Selección de tab acorde a estado
            MainTabs.SelectedItem = TabSetupInspect;

            if (_analysisViewActive && _state != MasterState.Ready)
            {
                ResetAnalysisMarks();
            }

            // Botón "Analizar Master" disponible en cuanto M1+M2 estén definidos
            BtnAnalyzeMaster.IsEnabled = mastersReady;

            UpdateRoiVisibilityControls();
        }

        private RoiModel? GetCurrentStatePersistedRoi()
        {
            return _state switch
            {
                MasterState.DrawM1_Pattern => _layout.Master1Pattern,
                MasterState.DrawM1_Search => _layout.Master1Search,
                MasterState.DrawM2_Pattern => _layout.Master2Pattern,
                MasterState.DrawM2_Search => _layout.Master2Search,
                MasterState.DrawInspection or MasterState.Ready => _layout.Inspection,
                _ => null
            };
        }

        private RoiRole? GetCurrentStateRole()
        {
            if (_editingM1 && _activeMaster1Role.HasValue)
            {
                return _activeMaster1Role.Value;
            }

            if (_editingM2 && _activeMaster2Role.HasValue)
            {
                return _activeMaster2Role.Value;
            }

            return _state switch
            {
                MasterState.DrawM1_Pattern => RoiRole.Master1Pattern,
                MasterState.DrawM1_Search => RoiRole.Master1Search,
                MasterState.DrawM2_Pattern => RoiRole.Master2Pattern,
                MasterState.DrawM2_Search => RoiRole.Master2Search,
                MasterState.DrawInspection or MasterState.Ready => RoiRole.Inspection,
                _ => null
            };
        }

        private bool ShouldEnableRoiEditing(RoiRole role, RoiModel? roi = null)
        {
            if (_editingM1 && (role == RoiRole.Master1Pattern || role == RoiRole.Master1Search))
            {
                // Only the active Master 1 role should be editable.
                return _activeMaster1Role.HasValue && _activeMaster1Role.Value == role;
            }

            if (_editingM2 && (role == RoiRole.Master2Pattern || role == RoiRole.Master2Search))
            {
                // Only the active Master 2 role should be editable.
                return _activeMaster2Role.HasValue && _activeMaster2Role.Value == role;
            }

            if (role == RoiRole.Inspection)
            {
                if (_state != MasterState.DrawInspection && _state != MasterState.Ready)
                {
                    return false;
                }

                if (!_globalUnlocked || string.IsNullOrWhiteSpace(_activeEditableRoiId) || roi == null)
                {
                    return false;
                }

                var cfgIndex = TryParseInspectionIndex(roi);
                var cfg = cfgIndex.HasValue ? GetInspectionConfigByIndex(cfgIndex.Value) : null;
                if (cfg != null && !cfg.Enabled)
                {
                    return false;
                }

                return string.Equals(roi.Id, _activeEditableRoiId, StringComparison.OrdinalIgnoreCase);
            }

            var currentRole = GetCurrentStateRole();
            return currentRole.HasValue && currentRole.Value == role;
        }

        private void CaptureCurrentUiShapeIntoSlot(string? roiId)
        {
            if (CanvasROI == null || string.IsNullOrWhiteSpace(roiId))
            {
                return;
            }

            Shape? shape = CanvasROI.Children
                .OfType<Shape>()
                .FirstOrDefault(s => s.Tag is RoiModel roiTag
                    && !string.IsNullOrWhiteSpace(roiTag.Id)
                    && string.Equals(roiTag.Id, roiId, StringComparison.OrdinalIgnoreCase));

            if (shape?.Tag is not RoiModel canvasRoi)
            {
                return;
            }

            try
            {
                SyncModelFromShape(shape);
            }
            catch
            {
                // best-effort; keep going with existing tag geometry
            }

            var pixelRoi = CanvasToImage(canvasRoi);

            if (_imgW > 0 && _imgH > 0)
            {
                pixelRoi.BaseImgW ??= _imgW;
                pixelRoi.BaseImgH ??= _imgH;
            }

            int? slotIndex = TryParseInspectionIndex(pixelRoi);
            if (!slotIndex.HasValue)
            {
                slotIndex = TryParseInspectionIndex(canvasRoi);
            }

            if (!slotIndex.HasValue)
            {
                slotIndex = ResolveActiveInspectionIndex();
            }

            if (!slotIndex.HasValue)
            {
                return;
            }

            NormalizeInspectionRoi(pixelRoi, slotIndex.Value);
            pixelRoi.IsFrozen = false;

            bool updateActive = slotIndex.Value == _activeInspectionIndex;
            SetInspectionSlotModel(slotIndex.Value, pixelRoi, updateActive);
            RefreshInspectionRoiSlots();
        }

        private void ExitEditMode(string reason = "edit-exit", bool skipCapture = false)
        {
            string? exitingId = _activeEditableRoiId;

            if (!skipCapture && !string.IsNullOrWhiteSpace(exitingId))
            {
                CaptureCurrentUiShapeIntoSlot(exitingId);
            }

            _editModeActive = false;
            ResetInspectionEditingFlags();
            ResetEditState();
            RemoveAllRoiAdorners();
            ApplyInspectionInteractionPolicy(reason);
            RedrawOverlaySafe();
            UpdateRoiHud();
            UpdateInspectionEditButtons();
        }

        private void ResetEditState()
        {
            _activeEditableRoiId = null;
            _globalUnlocked = false;
            _activeMaster1Role = null;
            _activeMaster2Role = null;
            UpdateEditableConfigState();
        }

        private void ResetInspectionEditingFlags()
        {
            _editingInspection1 = false;
            _editingInspection2 = false;
            _editingInspection3 = false;
            _editingInspection4 = false;
            _editingInspectionSlot = null;
        }

        private void UpdateInspectionEditButtons()
        {
            RaiseInspectionCommandStates();
        }

        private void RaiseInspectionCommandStates()
        {
            if (ViewModel == null)
            {
                return;
            }

            if (ViewModel.AddRoiToOkCommand is AsyncCommand addOk)
            {
                addOk.RaiseCanExecuteChanged();
            }

            if (ViewModel.AddRoiToNgCommand is AsyncCommand addNg)
            {
                addNg.RaiseCanExecuteChanged();
            }

            ViewModel.AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            ViewModel.AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
        }

        private void SetInspectionEditingFlag(int index, bool editing)
        {
            switch (index)
            {
                case 1:
                    _editingInspection1 = editing;
                    break;
                case 2:
                    _editingInspection2 = editing;
                    break;
                case 3:
                    _editingInspection3 = editing;
                    break;
                case 4:
                    _editingInspection4 = editing;
                    break;
            }
        }

        private void ToggleEditByRoiId(string roiId, int inspectionIndex)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return;
            }

            var slotRoi = GetInspectionSlotModel(inspectionIndex);
            if (slotRoi == null || string.IsNullOrWhiteSpace(slotRoi.Id) ||
                !string.Equals(slotRoi.Id, roiId, StringComparison.OrdinalIgnoreCase))
            {
                Snack($"Inspection {inspectionIndex} no tiene un ROI para editar.");
                return;
            }

            if (_editingInspectionSlot == inspectionIndex)
            {
                ExitInspectionEditMode(saveChanges: true);
                return;
            }

            EnterInspectionEditMode(inspectionIndex, roiId);
        }

        private void EnterInspectionEditMode(int inspectionIndex, string roiId)
        {
            GuiLog.Info(
                $"[workflow-edit] EnterInspectionEditMode START roi='{roiId}' slot={inspectionIndex} " +
                $"editingSlot={_editingInspectionSlot?.ToString() ?? "null"} " +
                $"globalUnlocked={_globalUnlocked} activeEditableRoiId='{_activeEditableRoiId}'");

            if (_state != MasterState.DrawInspection && _state != MasterState.Ready)
            {
                _state = MasterState.DrawInspection;
                UpdateWizardState();
            }

            if (_editingInspectionSlot.HasValue && _editingInspectionSlot.Value != inspectionIndex)
            {
                ExitInspectionEditMode(saveChanges: true);
            }

            ResetInspectionEditingFlags();
            _activeEditableRoiId = roiId;
            _globalUnlocked = true;
            _editModeActive = true;
            _editingInspectionSlot = inspectionIndex;
            SetInspectionEditingFlag(inspectionIndex, true);
            UpdateEditableConfigState();
            UpdateInspectionEditButtons();

            GoToInspectionTab();
            SetActiveInspectionIndex(inspectionIndex);

            RemoveAllRoiAdorners();
            ApplyInspectionInteractionPolicy($"toggle-on:{roiId}");
            RedrawOverlaySafe();
            UpdateRoiHud();

            var activeRoi = FindInspectionRoiModel(roiId) ?? GetInspectionSlotModel(inspectionIndex);
            Dispatcher.InvokeAsync(() =>
            {
                AttachAdornerForRoi(activeRoi);
                ApplyInspectionInteractionPolicy($"toggle-on:post-dispatch:{roiId}");
            }, DispatcherPriority.Background);

            GuiLog.Info(
                $"[workflow-edit] EnterInspectionEditMode END roi='{roiId}' slot={inspectionIndex} " +
                $"editingSlot={_editingInspectionSlot?.ToString() ?? "null"} " +
                $"globalUnlocked={_globalUnlocked} activeEditableRoiId='{_activeEditableRoiId}'");
        }

        private void ExitInspectionEditMode(bool saveChanges)
        {
            if (!_editingInspectionSlot.HasValue)
            {
                return;
            }

            var slot = _editingInspectionSlot.Value;

            GuiLog.Info(
                $"[workflow-edit] ExitInspectionEditMode START saveChanges={saveChanges} " +
                $"slot={slot} editingSlot={_editingInspectionSlot?.ToString() ?? "null"} " +
                $"globalUnlocked={_globalUnlocked} activeEditableRoiId='{_activeEditableRoiId}'");

            if (saveChanges)
            {
                SaveCurrentInspectionToSlot(slot);
            }

            ResetInspectionEditingFlags();
            ResetEditState();
            _editModeActive = _editingM1 || _editingM2;
            UpdateInspectionEditButtons();

            RemoveAllRoiAdorners();
            ApplyInspectionInteractionPolicy("inspection-edit-exit");
            RedrawOverlaySafe();
            UpdateRoiHud();

            GuiLog.Info(
                $"[workflow-edit] ExitInspectionEditMode END saveChanges={saveChanges} " +
                $"editingSlot={_editingInspectionSlot?.ToString() ?? "null"} " +
                $"globalUnlocked={_globalUnlocked} activeEditableRoiId='{_activeEditableRoiId}'");
        }

        private void UpdateEditableConfigState()
        {
            if (ViewModel?.InspectionRois == null)
            {
                return;
            }

            bool hasActive = _globalUnlocked && !string.IsNullOrWhiteSpace(_activeEditableRoiId);

            foreach (var cfg in ViewModel.InspectionRois)
            {
                bool editable = hasActive && !string.IsNullOrWhiteSpace(cfg.Id)
                    && string.Equals(cfg.Id, _activeEditableRoiId, StringComparison.OrdinalIgnoreCase);
                cfg.IsEditable = editable;
            }
        }

        private bool TryClearCurrentStatePersistedRoi(out RoiRole? clearedRole)
        {
            return TryClearPersistedRoiForState(_state, out clearedRole);
        }

        private bool TryClearPersistedRoiForState(MasterState state, out RoiRole? clearedRole)
        {
            clearedRole = state switch
            {
                MasterState.DrawM1_Pattern => RoiRole.Master1Pattern,
                MasterState.DrawM1_Search => RoiRole.Master1Search,
                MasterState.DrawM2_Pattern => RoiRole.Master2Pattern,
                MasterState.DrawM2_Search => RoiRole.Master2Search,
                MasterState.DrawInspection => RoiRole.Inspection,
                MasterState.Ready => RoiRole.Inspection,
                _ => GetCurrentStateRole()
            };

            switch (state)
            {
                case MasterState.DrawM1_Pattern:
                    if (_layout.Master1Pattern != null)
                    {
                        _layout.Master1Pattern = null;
                        _layout.Master1PatternImagePath = null;
                        return true;
                    }
                    break;

                case MasterState.DrawM1_Search:
                    if (_layout.Master1Search != null)
                    {
                        _layout.Master1Search = null;
                        return true;
                    }
                    break;

                case MasterState.DrawM2_Pattern:
                    if (_layout.Master2Pattern != null)
                    {
                        _layout.Master2Pattern = null;
                        _layout.Master2PatternImagePath = null;
                        return true;
                    }
                    break;

                case MasterState.DrawM2_Search:
                    if (_layout.Master2Search != null)
                    {
                        _layout.Master2Search = null;
                        return true;
                    }
                    break;

                case MasterState.DrawInspection:
                    if (GetInspectionSlotModel(_activeInspectionIndex) != null)
                    {
                        SetInspectionSlotModel(_activeInspectionIndex, null);
                        _layout.Inspection = null;
                        SetInspectionBaseline(null);
                        RefreshInspectionRoiSlots();
                        return true;
                    }
                    break;

                case MasterState.Ready:
                    if (GetInspectionSlotModel(_activeInspectionIndex) != null)
                    {
                        SetInspectionSlotModel(_activeInspectionIndex, null);
                        _layout.Inspection = null;
                        SetInspectionBaseline(null);
                        RefreshInspectionRoiSlots();
                        _state = MasterState.DrawInspection;
                        return true;
                    }

                    _state = MasterState.DrawInspection;
                    break;
            }

            return false;
        }


        // ====== Imagen ======
        private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog() != true) return;

            LoadImage(dlg.FileName);
        }

        private void LoadImage(string path)
        {
            _currentImagePathWin = path;
            _currentImagePathBackend = path;
            _currentImagePath = _currentImagePathWin;

            ViewModel?.BeginManualInspection(path);

            _imgSourceBI = new BitmapImage();
            _imgSourceBI.BeginInit();
            _imgSourceBI.CacheOption = BitmapCacheOption.OnLoad;
            _imgSourceBI.UriSource = new Uri(_currentImagePathWin);
            _imgSourceBI.EndInit();

            ImgMain.Source = _imgSourceBI;

            // [AUTO] reset master/image seeds for this new image
            _inspectionBaselineSeededForImage = false;   // force reseed of inspection baseline for this image
            _imageKeyForMasters = string.Empty;          // so masters won't be considered already seeded
            _mastersSeededForImage = false;              // seed again during auto Analyze Master
            _hasInspectionAnalysisTransform = false;
            _isFirstImageForCurrentKey = true;
            _inspectionBaselineRestorePending = false;
            RemoveAllRoiAdorners();
            LogDebug($"[roi-diag] load image → seeded=false, mastersSeeded={_mastersSeededForImage}, imageKey='{_imageKeyForMasters}'");
            OnImageLoaded_SetCurrentSource(_imgSourceBI);
            _currentImageHash = ComputeImageSeedKey();
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // RoiOverlay.InvalidateOverlay();
            _imgW = _imgSourceBI.PixelWidth;
            _imgH = _imgSourceBI.PixelHeight;

            LogImgLoadAndRois("imgload:pre-rescale");
            RescaleInspectionRoisToNewImageSize(_imgW, _imgH);
            LogImgLoadAndRois("imgload:post-rescale");

            try
            {
                var newFrame = Cv2.ImRead(path, ImreadModes.Color);
                if (newFrame == null || newFrame.Empty())
                {
                    newFrame?.Dispose();
                    bgrFrame?.Dispose();
                    bgrFrame = null;
                    MessageBox.Show("No se pudo leer la imagen para análisis.");
                }
                else
                {
                    bgrFrame?.Dispose();
                    bgrFrame = newFrame;
                }
            }
            catch (Exception ex)
            {
                bgrFrame?.Dispose();
                bgrFrame = null;
                MessageBox.Show($"Error al leer la imagen: {ex.Message}");
            }

            // 🔧 clave: forzar reprogramación aunque el scheduler se hubiera quedado “true”

            if (_isFirstImageLoaded)
            {
                ClearViewerForNewImage();
            }
            else
            {
                _isFirstImageLoaded = true;
            }

            RedrawOverlay();

            ScheduleSyncOverlay(force: true, reason: "ImageLoaded");

            AppendLog($"Imagen cargada: {_imgW}x{_imgH}  (Canvas: {CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0})");
            RedrawOverlaySafe();
            DumpUiShapesMap("imgload:post-redraw");
            ClearHeatmapOverlay();
            RestoreInspectionBaselineForCurrentImage();

            Dispatcher.InvokeAsync(async () =>
            {
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                SyncOverlayToImage(scheduleResync: true);
                await Dispatcher.Yield(DispatcherPriority.Render);
                await Dispatcher.Yield(DispatcherPriority.Background);

                if (HasAllMastersAndInspectionsDefined())
                {
                    try
                    {
                        LogDebug("[auto-analyze] ImageLoaded → AnalyzeMaster()");
                        await AnalyzeMastersAsync();
                        ScheduleSyncOverlay(force: true, reason: "AutoAnalyzeAfterImageLoad");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[auto-analyze] failed: {ex.Message}");
                    }
                }
            }, DispatcherPriority.Loaded);

            UpdateRoiHud();

            {
                var seedKey = ComputeImageSeedKey();
                _currentImageHash = seedKey;
                // New image => reset and seed
                if (!string.Equals(seedKey, _lastImageSeedKey, System.StringComparison.Ordinal))
                {
                    _inspectionBaselineFixed = null;
                    _inspectionBaselineSeededForImage = false;
                    InspLog($"[Seed] New image detected, oldKey='{_lastImageSeedKey}' newKey='{seedKey}' -> reset baseline.");
                    try
                    {
                        // Prefer persisted inspection ROI if available; else current on-screen inspection
                        SeedInspectionBaselineOnce(_layout?.InspectionBaseline ?? _layout?.Inspection, seedKey);
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    // Same image => do NOTHING (no re-seed)
                    InspLog($"[Seed] Same image key='{seedKey}', no re-seed.");
                }
            }

            {
                string key = ComputeImageSeedKey();
                _currentImageHash = key;
                var m1p = _layout?.Master1Pattern;
                var m2p = _layout?.Master2Pattern;

                if (!string.Equals(key, _imageKeyForMasters, System.StringComparison.Ordinal))
                {
                    _imageKeyForMasters = key;
                    _mastersSeededForImage = false;
                    _lastAccM1X = _lastAccM1Y = _lastAccM2X = _lastAccM2Y = double.NaN;
                    _lastDetectionAccepted = false;
                    InspLog("[Analyze] Reset last-accepted M1/M2 for new image.");
                }
                else
                {
                    InspLog($"[Seed-M] Same image key='{key}', keep current masters baseline.");
                }

                // Seed masters BASE pivots once per image using true geometric centers (shape-aware)
                if (!_mastersSeededForImage && _layout?.Master1Pattern != null && _layout?.Master2Pattern != null)
                {
                    var (m1cx, m1cy) = GetCenterShapeAware(_layout.Master1Pattern);
                    var (m2cx, m2cy) = GetCenterShapeAware(_layout.Master2Pattern);

                    _m1BaseX = m1cx; _m1BaseY = m1cy;
                    _m2BaseX = m2cx; _m2BaseY = m2cy;
                    _mastersSeededForImage = true;

                    Dbg($"[Seed-M] New image: M1_base=({m1cx:F3},{m1cy:F3}) M2_base=({m2cx:F3},{m2cy:F3})");
                }

                if (!_mastersSeededForImage)
                {
                    InspLog("[Seed-M] WARNING: Cannot seed masters baseline (missing Master1Pattern/Master2Pattern).");
                }
            }

            // === ROI DIAG: after image load & overlay sync ===
            try
            {
                RoiDiagDumpTransform("imgload:after-sync");

                // Dump all known ROIs from layout
                if (_layout != null)
                {
                    RoiDiagDumpRoi("imgload", "Master1Pattern", _layout.Master1Pattern);
                    RoiDiagDumpRoi("imgload", "Master1Search ", _layout.Master1Search);
                    RoiDiagDumpRoi("imgload", "Master2Pattern", _layout.Master2Pattern);
                    RoiDiagDumpRoi("imgload", "Master2Search ", _layout.Master2Search);
                    RoiDiagDumpRoi("imgload", "Inspection   ", _layout.Inspection);
                }
                // Heatmap ROI if available
                if (_lastHeatmapRoi != null)
                    RoiDiagDumpRoi("imgload", "HeatmapROI   ", _lastHeatmapRoi);

                // Snapshot of canvas children after layout
                Dispatcher.InvokeAsync(() => RoiDiagDumpCanvasChildren("imgload:UI-snapshot"),
                                       System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (System.Exception ex)
            {
                RoiDiagLog("imgload: diagnostics EX: " + ex.Message);
            }

            _hasLoadedImage = true;
            if (_workflowViewModel != null)
            {
                _workflowViewModel.IsImageLoaded = true;
            }
            EnablePresetsTab(true);
        }

        private bool IsOverlayAligned()
        {
            var disp = GetImageDisplayRect();
            // CODEX: si no hay imagen (0x0), consideramos alineado para no bloquear interacción.
            if (disp.Width <= 0 || disp.Height <= 0)
            {
                return true; // CODEX: no existe display que sincronizar todavía
            }

            double dw = Math.Abs(CanvasROI.ActualWidth - disp.Width);
            double dh = Math.Abs(CanvasROI.ActualHeight - disp.Height);
            return dw <= 0.5 && dh <= 0.5; // tolerancia sub-px
        }

        private void RedrawOverlaySafe()
        {
            if (_suspendManualOverlayInvalidations)
            {
                AppendLog("[batch] skip redraw (batch running)");
                return;
            }

            bool aligned = IsOverlayAligned();
            AppendLog($"[guard] RedrawOverlaySafe aligned={aligned} " +
                      $"canvasActual=({CanvasROI?.ActualWidth:0.##}x{CanvasROI?.ActualHeight:0.##}) " +
                      $"imgActual=({ImgMain?.ActualWidth:0.##}x{ImgMain?.ActualHeight:0.##})");

            if (aligned)
            {
                RedrawOverlay();
                _overlayNeedsRedraw = false;
                ApplyInspectionInteractionPolicy("redraw");
            }
            else
            {
                _overlayNeedsRedraw = true;
                ScheduleSyncOverlay(force: true, reason: "RedrawOverlaySafe");
                // CODEX: string interpolation compatibility.
                AppendLog($"[guard] Redraw pospuesto (overlay aún no alineado)");
                ApplyInspectionInteractionPolicy("redraw-deferred");
            }
        }





        private void RemoveRoiShape(Shape shape)
        {
            if (CanvasROI == null)
                return;

            if (shape.Tag is RoiModel roiModel)
            {
                var roiId = roiModel.Id ?? roiModel.Role.ToString();
                OverlayCleanup.RemoveForRoi(CanvasROI, shape, roiId);
                _roiLabels.Remove(shape);
            }
            else
            {
                RemoveRoiLabel(shape);
                RemoveRoiAdorners(shape);
            }

            CanvasROI.Children.Remove(shape);
        }

        private void ClearCanvasShapesAndLabels()
        {
            try
            {
                var shapes = CanvasROI.Children.OfType<System.Windows.Shapes.Shape>().ToList();
                foreach (var s in shapes) CanvasROI.Children.Remove(s);
                var labels = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .Where(fe => fe.Name != null && fe.Name.StartsWith("roiLabel_"))
                    .ToList();
                foreach (var l in labels) CanvasROI.Children.Remove(l);
            }
            catch { /* ignore */ }
        }

        private void ClearCanvasInternalMaps()
        {
            try
            {
                _roiShapesById?.Clear();
                _roiLabels?.Clear();
            }
            catch { /* ignore */ }
        }

        private void ClearViewerForNewImage()
        {
            try { ClearCanvasShapesAndLabels(); } catch { }
            try { ClearCanvasInternalMaps(); } catch { }
            try { DetachPreviewAndAdorner(); } catch { }
            try { RemoveAllRoiAdorners(); } catch { }
            try { ResetAnalysisMarks(); } catch { }

            _currentImageHash = string.Empty;

            if (RoiHudStack != null)
            {
                RoiHudStack.Children.Clear();
            }

            if (RoiHudOverlay != null)
            {
                RoiHudOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void DetachPreviewAndAdorner()
        {
            try
            {
                if (_previewShape != null)
                {
                    try
                    {
                        var al = AdornerLayer.GetAdornerLayer(_previewShape);
                        if (al != null)
                        {
                            var adorners = al.GetAdorners(_previewShape);
                            if (adorners != null)
                            {
                                foreach (var ad in adorners.OfType<RoiAdorner>())
                                    al.Remove(ad);
                            }
                        }
                    }
                    catch { /* ignore */ }

                    CanvasROI.Children.Remove(_previewShape);
                    _previewShape = null;
                }
            }
            catch { }
        }

        private void ClearPersistedRoisFromCanvas()
        {
            if (CanvasROI == null)
                return;

            var persisted = CanvasROI.Children
                .OfType<Shape>()
                .Where(shape => !ReferenceEquals(shape, _previewShape) && shape.Tag is RoiModel)
                .ToList();

            foreach (var shape in persisted)
            {
                RemoveRoiShape(shape);
            }

            _roiShapesById.Clear();
            _roiLabels.Clear();
        }

        private void RedrawOverlay()
        {
            if (_suspendManualOverlayInvalidations)
            {
                AppendLog("[batch] skip overlay redraw (batch running)");
                return;
            }

            AppendResizeLog($"[redraw] start: CanvasROI={CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}");
            if (CanvasROI == null || _imgW <= 0 || _imgH <= 0)
                return;

            // Remove orphan labels whose ROI no longer exists
            try
            {
                var validKeys = new HashSet<string>(SavedRois.Select(rm =>
                {
                    string lbl = (!string.IsNullOrWhiteSpace(rm.Label)) ? rm.Label : ResolveRoiLabelText(rm);
                    lbl = string.IsNullOrWhiteSpace(lbl) ? "ROI" : lbl;
                    return "roiLabel_" + lbl.Replace(" ", "_");
                }));

                var toRemove = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .Where(fe => fe.Name.StartsWith("roiLabel_") && !validKeys.Contains(fe.Name))
                    .ToList();
                foreach (var tb in toRemove) CanvasROI.Children.Remove(tb);
            }
            catch { /* ignore */ }

            var activeRois = SavedRois.Where(roi => roi != null).ToList();
            var activeIds = new HashSet<string>(activeRois.Select(roi => roi.Id));

            foreach (var kv in _roiShapesById.ToList())
            {
                if (!activeIds.Contains(kv.Key))
                {
                    RemoveRoiShape(kv.Value);
                    _roiShapesById.Remove(kv.Key);
                }
            }

            var transform = GetImageToCanvasTransform();
            double sx = transform.sx;
            double sy = transform.sy;
            double ox = transform.offX;
            double oy = transform.offY;
            if (sx <= 0.0 || sy <= 0.0)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[overlay] skipped redraw (transform invalid)");
                return;
            }

            double k = Math.Min(sx, sy);

            foreach (var roi in activeRois)
            {
                if (!_roiShapesById.TryGetValue(roi.Id, out var shape) || shape == null)
                {
                    shape = CreateLayoutShape(roi);
                    if (shape == null)
                    {
                        AppendLog($"[overlay] build failed for {roi.Role} ({roi.Label})");
                        continue;
                    }

                    CanvasROI.Children.Add(shape);
                    _roiShapesById[roi.Id] = shape;
                }

                // Keep saved ROI visible (do not hide stroke/fill)
                try
                {
                    var style = GetRoiStyle(roi.Role);
                    shape.Stroke = style.stroke;
                    shape.Fill = style.fill;
                    shape.StrokeThickness = Math.Max(1.0, shape.StrokeThickness);
                    if (style.dash != null)
                        shape.StrokeDashArray = style.dash;
                    else
                        shape.StrokeDashArray = null;
                    bool allowEditing = !roi.IsFrozen && ShouldEnableRoiEditing(roi.Role, roi);
                    shape.IsHitTestVisible = allowEditing;
                    if (!allowEditing)
                    {
                        RemoveRoiAdorners(shape);
                    }
                    Panel.SetZIndex(shape, style.zIndex);
                }
                catch
                {
                    // Fallback if style not available
                    shape.Stroke = Brushes.White;
                    shape.Fill = Brushes.Transparent;
                    shape.StrokeThickness = 1.0;
                    shape.StrokeDashArray = null;
                    bool allowEditing = !roi.IsFrozen && ShouldEnableRoiEditing(roi.Role, roi);
                    shape.IsHitTestVisible = allowEditing;
                    if (!allowEditing)
                    {
                        RemoveRoiAdorners(shape);
                    }
                    Panel.SetZIndex(shape, 5);
                }

                if (shape.Tag is not RoiModel canvasRoi)
                {
                    canvasRoi = roi.Clone();
                    shape.Tag = canvasRoi;
                }

                // Ensure the saved ROI label is synced to the canvas clone
                if (canvasRoi is RoiModel)
                {
                    canvasRoi.Label = roi.Label;
                }

                canvasRoi.Role = roi.Role;
                canvasRoi.AngleDeg = roi.AngleDeg;
                canvasRoi.Shape = roi.Shape;

                bool isMasterRole = roi.Role == RoiRole.Master1Pattern ||
                                    roi.Role == RoiRole.Master1Search ||
                                    roi.Role == RoiRole.Master2Pattern ||
                                    roi.Role == RoiRole.Master2Search;

                SWRect expectedCanvasRect = default;
                bool expectedRectValid = false;

                switch (roi.Shape)
                {
                    case RoiShape.Rectangle:
                        {
                            double left;
                            double top;
                            double width;
                            double height;
                            double centerX;
                            double centerY;

                            if (isMasterRole)
                            {
                                var canvasRect = MapImageRectToCanvas(new SWRect(roi.Left, roi.Top, roi.Width, roi.Height));
                                left = canvasRect.X;
                                top = canvasRect.Y;
                                width = Math.Max(1.0, canvasRect.Width);
                                height = Math.Max(1.0, canvasRect.Height);
                            }
                            else
                            {
                                left = ox + roi.Left * sx;
                                top = oy + roi.Top * sy;
                                width = Math.Max(1.0, roi.Width * sx);
                                height = Math.Max(1.0, roi.Height * sy);
                            }

                            centerX = left + width / 2.0;
                            centerY = top + height / 2.0;

                            Canvas.SetLeft(shape, left);
                            Canvas.SetTop(shape, top);
                            shape.Width = width;
                            shape.Height = height;

                            canvasRoi.Width = width;
                            canvasRoi.Height = height;
                            canvasRoi.Left = left;
                            canvasRoi.Top = top;
                            canvasRoi.X = centerX;
                            canvasRoi.Y = centerY;
                            canvasRoi.CX = centerX;
                            canvasRoi.CY = centerY;
                            canvasRoi.R = Math.Max(width, height) / 2.0;
                            canvasRoi.RInner = 0;
                            expectedCanvasRect = MapImageRectToCanvas(new SWRect(roi.Left, roi.Top, roi.Width, roi.Height));
                            expectedRectValid = true;
                            break;
                        }
                    case RoiShape.Circle:
                        {
                            double cx;
                            double cy;
                            double d;

                            if (isMasterRole)
                            {
                                var circleMapped1 = MapImageCircleToCanvas(roi.CX, roi.CY, roi.R, 0);
                                cx = circleMapped1.c.X;
                                cy = circleMapped1.c.Y;
                                d = Math.Max(1.0, circleMapped1.rOuter * 2.0);
                            }
                            else
                            {
                                double cxImg = roi.CX;
                                double cyImg = roi.CY;
                                double dImg = roi.R * 2.0;

                                cx = ox + cxImg * sx;
                                cy = oy + cyImg * sy;
                                d = Math.Max(1.0, dImg * k);
                            }

                            Canvas.SetLeft(shape, cx - d / 2.0);
                            Canvas.SetTop(shape, cy - d / 2.0);
                            shape.Width = d;
                            shape.Height = d;

                            canvasRoi.Width = d;
                            canvasRoi.Height = d;
                            canvasRoi.Left = cx - d / 2.0;
                            canvasRoi.Top = cy - d / 2.0;
                            canvasRoi.CX = cx;
                            canvasRoi.CY = cy;
                            canvasRoi.X = cx;
                            canvasRoi.Y = cy;
                            canvasRoi.R = d / 2.0;
                            canvasRoi.RInner = 0;
                            var circleMapped2 = MapImageCircleToCanvas(roi.CX, roi.CY, roi.R, 0);
                            double mappedDiameter = Math.Max(1.0, circleMapped2.rOuter * 2.0);
                            expectedCanvasRect = new SWRect(circleMapped2.c.X - mappedDiameter / 2.0, circleMapped2.c.Y - mappedDiameter / 2.0, mappedDiameter, mappedDiameter);
                            expectedRectValid = true;
                            break;
                        }
                    case RoiShape.Annulus:
                        {
                            double cx;
                            double cy;
                            double d;
                            double innerCanvas;

                            if (isMasterRole)
                            {
                                var annulusMapped1 = MapImageCircleToCanvas(roi.CX, roi.CY, roi.R, roi.RInner);
                                cx = annulusMapped1.c.X;
                                cy = annulusMapped1.c.Y;
                                double outerRadius = Math.Max(0.0, annulusMapped1.rOuter);
                                d = Math.Max(1.0, outerRadius * 2.0);
                                innerCanvas = Math.Max(0.0, Math.Min(annulusMapped1.rInner, d / 2.0));
                            }
                            else
                            {
                                double cxImg = roi.CX;
                                double cyImg = roi.CY;
                                double dImg = roi.R * 2.0;

                                cx = ox + cxImg * sx;
                                cy = oy + cyImg * sy;
                                d = Math.Max(1.0, dImg * k);
                                innerCanvas = Math.Max(0.0, Math.Min(roi.RInner * k, d / 2.0));
                            }

                            Canvas.SetLeft(shape, cx - d / 2.0);
                            Canvas.SetTop(shape, cy - d / 2.0);
                            shape.Width = d;
                            shape.Height = d;

                            if (shape is AnnulusShape ann)
                            {
                                ann.InnerRadius = innerCanvas;
                                canvasRoi.RInner = innerCanvas;
                            }

                            canvasRoi.Width = d;
                            canvasRoi.Height = d;
                            canvasRoi.Left = cx - d / 2.0;
                            canvasRoi.Top = cy - d / 2.0;
                            canvasRoi.CX = cx;
                            canvasRoi.CY = cy;
                            canvasRoi.X = cx;
                            canvasRoi.Y = cy;
                            canvasRoi.R = d / 2.0;
                            var annulusMapped2 = MapImageCircleToCanvas(roi.CX, roi.CY, roi.R, roi.RInner);
                            double mappedDiameter = Math.Max(1.0, annulusMapped2.rOuter * 2.0);
                            expectedCanvasRect = new SWRect(annulusMapped2.c.X - mappedDiameter / 2.0, annulusMapped2.c.Y - mappedDiameter / 2.0, mappedDiameter, mappedDiameter);
                            expectedRectValid = true;
                            break;
                        }
                }

                // Ensure shape.Tag references the ROI clone for downstream logic
                shape.Tag = canvasRoi;

                string _lbl;
                if (roi is RoiModel mObj)
                {
                    string resolved = null;
                    try { resolved = ResolveRoiLabelText(mObj); } catch { /* ignore */ }

                    _lbl = !string.IsNullOrWhiteSpace(resolved) ? resolved
                         : (!string.IsNullOrWhiteSpace(mObj.Label) ? mObj.Label : "ROI");

                    // Persist resolved label so future redraws reuse same text/key
                    if (string.IsNullOrWhiteSpace(mObj.Label) && !string.IsNullOrWhiteSpace(resolved))
                        mObj.Label = resolved;
                }
                else
                {
                    _lbl = "ROI";
                }

                if (canvasRoi is RoiModel)
                {
                    canvasRoi.Label = _lbl;
                }

                // Use unique TextBlock name derived from label text
                string _labelName = "roiLabel_" + (_lbl ?? string.Empty).Replace(" ", "_");
                var _existing = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(fe => fe.Name == _labelName);
                FrameworkElement _label;
                string finalText = string.IsNullOrWhiteSpace(_lbl) ? "ROI" : _lbl;

                if (_existing == null)
                {
                    _label = CreateStyledLabel(finalText);
                    _label.Name = _labelName;
                    _label.IsHitTestVisible = false;
                    CanvasROI.Children.Add(_label);
                    Panel.SetZIndex(_label, int.MaxValue);
                }
                else
                {
                    _label = _existing;
                    _label.IsHitTestVisible = false;
                    if (_label is System.Windows.Controls.Border border && border.Child is System.Windows.Controls.TextBlock tb)
                    {
                        tb.Text = finalText;
                    }
                }

                // Place label next to the ROI shape (may defer via Dispatcher if geometry not ready)
                UpdateRoiLabelPosition(shape);

                if (shape != null)
                {
                    double l = Canvas.GetLeft(shape), t = Canvas.GetTop(shape);
                    AppendResizeLog($"[roi] {roi.Label ?? "ROI"}: L={l:0} T={t:0} W={shape.Width:0} H={shape.Height:0}");
                }

                if (roi.Role == RoiRole.Inspection && expectedRectValid && !string.IsNullOrWhiteSpace(roi.Id))
                {
                    LogDeltaAgainstShape($"redraw:{roi.Id}", roi.Id, expectedCanvasRect);
                }

                ApplyRoiRotationToShape(shape, roi.AngleDeg);
            }

            if (_layout.Inspection != null)
            {
                SyncCurrentRoiFromInspection(_layout.Inspection);
            }

            DumpUiShapesMap("redraw:end");
        }

        private Shape? CreateLayoutShape(RoiModel roi)
        {
            var canvasRoi = roi.Clone();

            Shape shape = roi.Shape switch
            {
                RoiShape.Rectangle => new WRectShape(),
                RoiShape.Annulus => new AnnulusShape(),
                _ => new WEllipse()
            };

            var style = GetRoiStyle(roi.Role);

            shape.Stroke = style.stroke;
            shape.Fill = style.fill;
            shape.StrokeThickness = style.thickness;
            if (style.dash != null)
                shape.StrokeDashArray = style.dash;
            shape.SnapsToDevicePixels = true;
            shape.IsHitTestVisible = !roi.IsFrozen && ShouldEnableRoiEditing(roi.Role, roi);
            Panel.SetZIndex(shape, style.zIndex);

            // Persist canvas ROI info on Tag; geometry will be updated during RedrawOverlay().
            shape.Tag = canvasRoi;

            return shape;
        }

        private (WBrush stroke, WBrush fill, double thickness, DoubleCollection? dash, int zIndex) GetRoiStyle(RoiRole role)
        {
            var transparent = WBrushes.Transparent;
            switch (role)
            {
                case RoiRole.Master1Pattern:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(30, 0, 255, 255));
                        fill.Freeze();
                        return (WBrushes.Cyan, fill, 2.0, null, 5);
                    }
                case RoiRole.Master1Search:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(18, 255, 215, 0));
                        fill.Freeze();
                        var dash = new DoubleCollection { 4, 3 };
                        dash.Freeze();
                        return (WBrushes.Gold, fill, 1.5, dash, 4);
                    }
                case RoiRole.Master2Pattern:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(30, 255, 165, 0));
                        fill.Freeze();
                        return (WBrushes.Orange, fill, 2.0, null, 6);
                    }
                case RoiRole.Master2Search:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(18, 205, 92, 92));
                        fill.Freeze();
                        var dash = new DoubleCollection { 4, 3 };
                        dash.Freeze();
                        return (WBrushes.IndianRed, fill, 1.5, dash, 4);
                    }
                case RoiRole.Inspection:
                    {
                        var fill = new SolidColorBrush(WColor.FromArgb(45, 50, 205, 50));
                        fill.Freeze();
                        return (WBrushes.Lime, fill, 2.5, null, 7);
                    }
                default:
                    return (WBrushes.White, transparent, 2.0, null, 5);
            }
        }

        private (WBrush stroke, WBrush fill, double thickness, DoubleCollection? dash, int zIndex) GetRoiStyle(ROI roi)
        {
            return GetRoiStyle(RoiRole.Inspection);
        }

        private void AttachRoiAdorner(Shape shape)
        {
            if (shape.Tag is not RoiModel roiInfo)
            {
                return;
            }

            if (!ShouldEnableRoiEditing(roiInfo.Role, roiInfo))
            {
                if (roiInfo.Role == RoiRole.Inspection)
                {
                    var guardLayer = AdornerLayer.GetAdornerLayer(shape);
                    if (guardLayer != null)
                    {
                        var adorners = guardLayer.GetAdorners(shape);
                        if (adorners != null)
                        {
                            foreach (var adorner in adorners.OfType<RoiAdorner>())
                            {
                                guardLayer.Remove(adorner);
                            }
                        }
                    }
                }

                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
                return;

            var existing = layer.GetAdorners(shape);
            if (existing != null)
            {
                foreach (var adorner in existing.OfType<RoiAdorner>())
                    layer.Remove(adorner);
            }

            if (RoiOverlay == null)
                return;

            var newAdorner = new RoiAdorner(shape, RoiOverlay, (changeKind, updatedModel) =>
            {
                var pixelModel = CanvasToImage(updatedModel);
                if (_layout != null)
                {
                    UpdateLayoutFromPixel(pixelModel);
                }
                // Evitamos redibujar en DragStarted (click en adorner sin mover)
                HandleAdornerChange(changeKind, updatedModel, pixelModel, "[adorner]");
                UpdateRoiLabelPosition(shape);
            }, AppendLog);

            layer.Add(newAdorner);
        }

        private void RemoveRoiAdorners(Shape shape)
        {
            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
                return;

            var adorners = layer.GetAdorners(shape);
            if (adorners == null)
                return;

            foreach (var adorner in adorners.OfType<RoiAdorner>())
                layer.Remove(adorner);
        }

        private void RemoveAllRoiAdorners()
        {
            if (CanvasROI == null)
            {
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(CanvasROI);
            if (layer == null)
            {
                return;
            }

            foreach (var child in CanvasROI.Children.OfType<Shape>())
            {
                var adorners = layer.GetAdorners(child);
                if (adorners == null)
                {
                    continue;
                }

                foreach (var adorner in adorners.OfType<RoiAdorner>())
                {
                    layer.Remove(adorner);
                }
            }
        }


        private void UpdateHeatmapOverlayLayoutAndClip()
        {
            LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: BEGIN ----");

            if (HeatmapOverlay == null)
            {
                LogHeatmap("HeatmapOverlay control is null.");
                LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: END ----");
                return;
            }

            if (_lastHeatmapBmp == null || _lastHeatmapRoi == null)
            {
                LogHeatmap("No heatmap or ROI to overlay.");
                HeatmapOverlay.Source = null;
                HeatmapOverlay.Visibility = Visibility.Collapsed;
                HeatmapOverlay.Clip = null;
                HeatmapOverlay.RenderTransform = Transform.Identity;
                LogHeatmap("Clip = null");
                LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: END ----");
                return;
            }

            // 1) Rectángulo de la imagen en pantalla (letterboxing)
            var disp = GetImageDisplayRect();
            LogHeatmap($"DisplayRect = (X={disp.X:F2},Y={disp.Y:F2},W={disp.Width:F2},H={disp.Height:F2})");

            // 2) Transformación imagen→canvas actualmente en uso
            var heatmapTransform = GetImageToCanvasTransform();
            LogHeatmap($"Transform Img→Canvas: sx={heatmapTransform.sx:F6}, sy={heatmapTransform.sy:F6}, offX={heatmapTransform.offX:F4}, offY={heatmapTransform.offY:F4}]");

            // 3) ROI en espacio de imagen (si tienes RoiDebug)
            try { LogHeatmap("ROI (image space): " + RoiDebug(_lastHeatmapRoi)); } catch {}

            // 4) ROI en espacio de CANVAS
            var rc = ImageToCanvas(_lastHeatmapRoi);
            LogHeatmap($"ROI canvas rect rc = (L={rc.Left:F2},T={rc.Top:F2},W={rc.Width:F2},H={rc.Height:F2})");

            // 5) Margen del Canvas de las ROI (CanvasROI) y su offset visual real
            if (CanvasROI != null)
            {
                var cm = CanvasROI.Margin;
                LogHeatmap($"CanvasROI.Margin = (L={cm.Left:F0},T={cm.Top:F0})");
                var cofs = System.Windows.Media.VisualTreeHelper.GetOffset(CanvasROI);
                LogHeatmap($"CanvasROI.VisualOffset = (X={cofs.X:F4},Y={cofs.Y:F4})");
            }

            // 6) Tipo de padre del heatmap y ruta de posicionamiento
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(HeatmapOverlay);
            bool parentIsCanvas = parent is System.Windows.Controls.Canvas;
            LogHeatmap($"HeatmapOverlay.Parent = {parent?.GetType().Name ?? "<null>"} ; ParentIsCanvas={parentIsCanvas}");

            // 2) Anchor overlay to ROI rect (canvas absolute via Canvas coords when available)
            // Size to ROI rect
            HeatmapOverlay.Source = _lastHeatmapBmp;
            HeatmapOverlay.Width  = rc.Width;
            HeatmapOverlay.Height = rc.Height;

            // Prefer Canvas positioning to avoid Margin rounding drift. Fallback to Margin
            // only if parent is not a Canvas.
            if (parentIsCanvas)
            {
                // Clear Margin so Canvas.Left/Top are not compounded
                HeatmapOverlay.Margin = new System.Windows.Thickness(0);
                System.Windows.Controls.Canvas.SetLeft(HeatmapOverlay, rc.Left);
                System.Windows.Controls.Canvas.SetTop(HeatmapOverlay,  rc.Top);
            }
            else
            {
                // SUMAR el margen del Canvas de las ROI para alinear origen,
                // y redondear a enteros para evitar subpíxeles (misma política que CanvasROI).
                double leftRounded = System.Math.Round((CanvasROI?.Margin.Left ?? 0) + rc.Left);
                double topRounded  = System.Math.Round((CanvasROI?.Margin.Top  ?? 0) + rc.Top);
                HeatmapOverlay.Margin = new System.Windows.Thickness(leftRounded, topRounded, 0, 0);
            }

            LogHeatmap($"Heatmap by Margin with CanvasROI.Margin sum: finalMargin=({HeatmapOverlay.Margin.Left},{HeatmapOverlay.Margin.Top})");

            ApplyHeatmapOverlayRotation(rc.Width, rc.Height);

            HeatmapOverlay.Visibility = System.Windows.Visibility.Visible;

            // 3) Build Clip in OVERLAY-LOCAL coordinates (0..Width, 0..Height)
            //    Determine shape ratios from ROI model
            System.Windows.Media.Geometry clipGeo = null;

            // Optional: role-based mismatch detection (keep existing skipClip logic if you already added it)
            bool skipClip = false; // keep your previous mismatch logic if present to possibly set this true
            string heatmapShape = InferShapeName(_lastHeatmapRoi);
            string modelShape = null;
            RoiModel modelRoi = null;

            // Choose the expected model ROI by role
            try
            {
                // If the heatmap is for Master 1 inspection, compare against Master1Pattern
                if (_lastHeatmapRoi.Role == RoiRole.Master1Search)
                    modelRoi = _layout?.Master1Pattern;
                // If for Master 2 inspection, compare against Master2Pattern
                else if (_lastHeatmapRoi.Role == RoiRole.Master2Search)
                    modelRoi = _layout?.Master2Pattern;
                // For final Inspection, we may not have a single model to compare; leave null
            }
            catch { /* ignore */ }

            if (modelRoi != null)
            {
                modelShape = InferShapeName(modelRoi);
                if (!string.Equals(modelShape, heatmapShape, StringComparison.OrdinalIgnoreCase))
                {
                    skipClip = true;
                    LogHeatmap($"[WARN] ROI shape mismatch — model={modelShape}, heatmap={heatmapShape}. " +
                               "Skipping clip to show full heatmap.");
                }
                else
                {
                    LogHeatmap($"ROI shape match — {heatmapShape}.");
                }
            }
            else
            {
                LogHeatmap($"No model ROI available for mismatch check (heatmap shape={heatmapShape}).");
            }
            // --- end mismatch detection block ---

            // Compute outer ellipse based on the overlay bounds:
            // Note: overlay is exactly the ROI bounding box. "Outer radius" is half of the max side.
            double ow = HeatmapOverlay.Width;
            double oh = HeatmapOverlay.Height;
            double outerR = System.Math.Max(ow, oh) * 0.5;
            var center = new System.Windows.Point(ow * 0.5, oh * 0.5);

            // If ROI has both R and RInner > 0 => Annulus
            if (!skipClip && _lastHeatmapRoi.R > 0 && _lastHeatmapRoi.RInner > 0)
            {
                // Inner radius is proportional to outer radius by model ratio
                double innerR = outerR * (_lastHeatmapRoi.RInner / _lastHeatmapRoi.R);

                var outer = new System.Windows.Media.EllipseGeometry(center, outerR, outerR);
                var inner = new System.Windows.Media.EllipseGeometry(center, innerR, innerR);
                clipGeo = new System.Windows.Media.CombinedGeometry(System.Windows.Media.GeometryCombineMode.Exclude, outer, inner);
            }
            // If ROI has only R > 0 => Circle
            else if (!skipClip && _lastHeatmapRoi.R > 0)
            {
                clipGeo = new System.Windows.Media.EllipseGeometry(center, outerR, outerR);
            }
            // Otherwise treat as rectangle ROI (no need to clip; the overlay bounds already match)
            else
            {
                // leave clipGeo = null
                // clipGeo = new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, ow, oh));
            }

            // 4) Apply Clip (or disable if mismatch logic set skipClip)
            HeatmapOverlay.Clip = skipClip ? null : clipGeo;

            var hOfs = System.Windows.Media.VisualTreeHelper.GetOffset(HeatmapOverlay);
            LogHeatmap($"HeatmapOverlay.VisualOffset = (X={hOfs.X:F4},Y={hOfs.Y:F4})");

            if (HeatmapOverlay?.Clip != null)
            {
                var b = HeatmapOverlay.Clip.Bounds;
                LogHeatmap($"Clip.Bounds = (X={b.X:F2},Y={b.Y:F2},W={b.Width:F2},H={b.Height:F2})");
            }
            else
            {
                LogHeatmap("Clip = null");
            }

            // 5) (Optional) Log overlay rect in canvas space & local clip bounds
            LogHeatmap($"Overlay anchored to ROI: Left={rc.Left:F2}, Top={rc.Top:F2}, W={rc.Width:F2}, H={rc.Height:F2}");
            if (HeatmapOverlay?.Clip != null)
            {
                var b = HeatmapOverlay.Clip.Bounds;
                LogHeatmap($"Clip.Bounds = (X={b.X:F2},Y={b.Y:F2},W={b.Width:F2},H={b.Height:F2})");
            }
            else
            {
                LogHeatmap("Clip = null");
            }

            LogHeatmap("---- UpdateHeatmapOverlayLayoutAndClip: END ----");
        }

        private void ApplyHeatmapOverlayRotation(double width, double height)
        {
            if (HeatmapOverlay == null)
            {
                return;
            }

            if (_lastHeatmapRoi == null || width <= 0 || height <= 0)
            {
                HeatmapOverlay.RenderTransform = Transform.Identity;
                LogHeatmap("Heatmap rotation reset (no ROI or empty size).");
                return;
            }

            double angle = _lastHeatmapRoi.AngleDeg;
            if (double.IsNaN(angle))
            {
                angle = 0.0;
            }

            var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(_lastHeatmapRoi, width, height);

            if (HeatmapOverlay.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = angle;
                rotate.CenterX = pivotLocal.X;
                rotate.CenterY = pivotLocal.Y;
            }
            else
            {
                rotate = new RotateTransform(angle, pivotLocal.X, pivotLocal.Y);
                HeatmapOverlay.RenderTransform = rotate;
            }

            LogHeatmap($"Heatmap rotation applied: angle={angle:F2}, pivot=({pivotLocal.X:F2},{pivotLocal.Y:F2}).");
        }

        private async Task EnsureOverlayAlignedForHeatmapAsync()
        {
            if (ImgMain == null || CanvasROI == null)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                SyncOverlayToImage(scheduleResync: false);
                ScheduleSyncOverlay(force: true, reason: "EnsureOverlayAlignedForHeatmap");
                await Dispatcher.Yield(DispatcherPriority.Render);
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SyncOverlayToImage(scheduleResync: false);
                    ScheduleSyncOverlay(force: true, reason: "EnsureOverlayAlignedForHeatmap");
                }, DispatcherPriority.Loaded);

                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }

        private void UpdateLastInferenceClassification()
        {
            double? score = _workflowViewModel?.InferenceScore;
            if (!score.HasValue)
            {
                _lastIsNg = false;
                return;
            }

            double? threshold = null;
            if (_workflowViewModel?.SelectedInspectionRoi is InspectionRoiConfig selected)
            {
                threshold = selected.CalibratedThreshold ?? selected.ThresholdDefault;
            }

            if (_workflowViewModel != null)
            {
                double local = _workflowViewModel.LocalThreshold;
                if (local > 0)
                {
                    threshold = local;
                }
                else if (_workflowViewModel.InferenceThreshold.HasValue)
                {
                    threshold = _workflowViewModel.InferenceThreshold.Value;
                }
            }

            _lastIsNg = threshold.HasValue && score.Value > threshold.Value;
        }

        private async Task ShowHeatmapAsync(Workflow.RoiExportResult export, byte[] heatmapBytes, double opacity)
        {
            try
            {
                await ShowHeatmapOverlayAsync(export, heatmapBytes, opacity).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() => RequestBatchHeatmapPlacement("[ui] ShowHeatmapAsync", ViewModel), DispatcherPriority.Render); // CODEX: reuse debounced placement to keep ROI index consistent after heatmap refresh.
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[ui] ShowHeatmapAsync failed", ex); // CODEX: string interpolation compatibility.
            }
        }

        private static Task<BitmapSource?> LoadBitmapSourceFromBytesAsync(byte[] imageBytes, CancellationToken ct)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return Task.FromResult<BitmapSource?>(null);
            }

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                using var ms = new MemoryStream(imageBytes, writable: false);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.CanFreeze && !frame.IsFrozen)
                {
                    frame.Freeze();
                }

                return (BitmapSource)frame;
            }, ct);
        }

        private async Task<(SWPoint? c1, int s1, SWPoint? c2, int s2)> DetectMastersForImageAsync(byte[] imageBytes, string imagePath, CancellationToken ct)
        {
            if (_layout == null || _preset == null)
            {
                return (null, 0, null, 0);
            }

            if (_layout.Master1Pattern == null || _layout.Master1Search == null ||
                _layout.Master2Pattern == null || _layout.Master2Search == null)
            {
                AppendLog("[batch] master detection skipped: layout incomplete");
                return (null, 0, null, 0);
            }

            BitmapSource? probe = null;
            try
            {
                probe = await LoadBitmapSourceFromBytesAsync(imageBytes, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"[batch] image decode failed: {ex.Message}");
                return (null, 0, null, 0);
            }

            if (probe == null)
            {
                AppendLog("[batch] image decode returned null");
                return (null, 0, null, 0);
            }

            AppendLog($"[batch] master detection input size: {probe.PixelWidth}x{probe.PixelHeight}");

            bool useLocalMatcher;
            if (Dispatcher.CheckAccess())
            {
                useLocalMatcher = ChkUseLocalMatcher?.IsChecked == true;
            }
            else
            {
                var dispatcherOp = Dispatcher.InvokeAsync(() => ChkUseLocalMatcher?.IsChecked == true);
                useLocalMatcher = await dispatcherOp.Task.ConfigureAwait(false);
            }

            SWPoint? c1 = null;
            SWPoint? c2 = null;
            int s1 = 0;
            int s2 = 0;

            if (useLocalMatcher)
            {
                try
                {
                    AppendLog("[batch] master detection: local matcher");
                    using var img = Cv2.ImDecode(imageBytes, ImreadModes.Unchanged);
                    if (img.Empty())
                    {
                        AppendLog("[batch] local matcher decode produced empty image");
                    }
                    else
                    {
                        Mat? m1Override = null;
                        Mat? m2Override = null;
                        try
                        {
                            if (_layout.Master1Pattern != null)
                            {
                                m1Override = TryLoadMasterPatternOverride(_layout.Master1PatternImagePath, "M1");
                            }

                            if (_layout.Master2Pattern != null)
                            {
                                m2Override = TryLoadMasterPatternOverride(_layout.Master2PatternImagePath, "M2");
                            }

                        var res1 = LocalMatcher.MatchInSearchROI(
                            img,
                            _layout.Master1Pattern,
                            _layout.Master1Search,
                            _preset.Feature,
                            _preset.MatchThr,
                            _preset.RotRange,
                            _preset.ScaleMin,
                            _preset.ScaleMax,
                            m1Override,
                            LogToFileAndUI);

                        if (res1.center.HasValue)
                        {
                            c1 = new SWPoint(res1.center.Value.X, res1.center.Value.Y);
                            s1 = res1.score;
                        }
                        else
                        {
                            AppendLog("[batch] local matcher: Master1 not found");
                        }

                            var res2 = LocalMatcher.MatchInSearchROI(
                                img,
                                _layout.Master2Pattern,
                                _layout.Master2Search,
                                _preset.Feature,
                                _preset.MatchThr,
                            _preset.RotRange,
                            _preset.ScaleMin,
                            _preset.ScaleMax,
                            m2Override,
                            LogToFileAndUI);

                        if (res2.center.HasValue)
                        {
                            c2 = new SWPoint(res2.center.Value.X, res2.center.Value.Y);
                            s2 = res2.score;
                        }
                        else
                        {
                            AppendLog("[batch] local matcher: Master2 not found");
                        }
                        }
                        finally
                        {
                            m1Override?.Dispose();
                            m2Override?.Dispose();
                        }
                    }
                }
                catch (DllNotFoundException ex)
                {
                    AppendLog($"[batch] local matcher missing dependency: {ex.Message}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[batch] local matcher error: {ex.Message}");
                }
            }

            ct.ThrowIfCancellationRequested();

            if (c1 is null || c2 is null)
            {
                AppendLog("[batch] master detection: backend fallback");

                if (c1 is null)
                {
                    ct.ThrowIfCancellationRequested();
                    var inferM1 = await BackendAPI
                        .InferAsync(imagePath, _layout.Master1Pattern!, _preset, AppendLog)
                        .ConfigureAwait(false);

                    if (inferM1.ok && inferM1.result != null)
                    {
                        var result = inferM1.result;
                        var (cx, cy) = _layout.Master1Pattern!.GetCenter();
                        c1 = new SWPoint(cx, cy);
                        s1 = 100;
                        string thrText = result.threshold.HasValue
                            ? result.threshold.Value.ToString("0.###", CultureInfo.InvariantCulture)
                            : "n/a";
                        bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
                        AppendLog($"[batch] backend M1 score={result.score:0.###} thr={thrText} status={(pass ? "OK" : "NG")}");
                    }
                    else
                    {
                        AppendLog($"[batch] backend M1 failed: {inferM1.error ?? "unknown"}");
                    }
                }

                if (c2 is null)
                {
                    ct.ThrowIfCancellationRequested();
                    var inferM2 = await BackendAPI
                        .InferAsync(imagePath, _layout.Master2Pattern!, _preset, AppendLog)
                        .ConfigureAwait(false);

                    if (inferM2.ok && inferM2.result != null)
                    {
                        var result = inferM2.result;
                        var (cx, cy) = _layout.Master2Pattern!.GetCenter();
                        c2 = new SWPoint(cx, cy);
                        s2 = 100;
                        string thrText = result.threshold.HasValue
                            ? result.threshold.Value.ToString("0.###", CultureInfo.InvariantCulture)
                            : "n/a";
                        bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
                        AppendLog($"[batch] backend M2 score={result.score:0.###} thr={thrText} status={(pass ? "OK" : "NG")}");
                    }
                    else
                    {
                        AppendLog($"[batch] backend M2 failed: {inferM2.error ?? "unknown"}");
                    }
                }
            }

            return (c1, s1, c2, s2);
        }

        public async Task RepositionBeforeBatchStepAsync(string imagePath, long stepId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || _layout == null)
            {
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"[batch-repos] read failed: {ex.Message} path='{imagePath}'");
                return;
            }

            SWPoint? detectedM1 = null;
            SWPoint? detectedM2 = null;
            int scoreM1 = 0;
            int scoreM2 = 0;
            try
            {
                (detectedM1, scoreM1, detectedM2, scoreM2) = await DetectMastersForImageAsync(bytes, imagePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"[batch-repos] detect masters error: {ex.Message}");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (ViewModel?.BatchStepId != stepId)
                {
                    AppendLog($"[batch-repos] skip stale step: was={stepId} now={ViewModel?.BatchStepId}");
                    return;
                }

                if (detectedM1 is SWPoint m1 && detectedM2 is SWPoint m2 && _layout?.Master1Pattern != null && _layout.Master2Pattern != null)
                {
                    var (cx1, cy1) = _layout.Master1Pattern.GetCenter();
                    var (cx2, cy2) = _layout.Master2Pattern.GetCenter();
                    ViewModel?.RegisterBatchAnchors(new SWPoint(cx1, cy1), new SWPoint(cx2, cy2), m1, m2, scoreM1, scoreM2);
                    GuiLog.Info($"[analyze-master] file='{Path.GetFileName(ViewModel?.CurrentImagePath ?? ViewModel?.CurrentManualImagePath ?? string.Empty)}' found M1=({m1.X:0.0},{m1.Y:0.0}) M2=({m2.X:0.0},{m2.Y:0.0}) score=({scoreM1},{scoreM2})");
                    AppendLog($"[batch-repos] anchors registered step={stepId} M1=({m1.X:F1},{m1.Y:F1}) M2=({m2.X:F1},{m2.Y:F1})");
                    return;
                }

                AppendLog("[batch-repos] anchors unavailable -> skip transform update");
            }, DispatcherPriority.Background);
        }

        private bool TryRestoreSnapshotForImage(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return false;
            }

            if (_layout?.InspectionBaselinesByImage == null || _layout.InspectionBaselinesByImage.Count == 0)
            {
                return false;
            }

            try
            {
                var imageKey = HashSHA256(imageBytes);
                if (string.IsNullOrWhiteSpace(imageKey))
                {
                    return false;
                }

                if (_layout.InspectionBaselinesByImage.TryGetValue(imageKey, out var snapshot) && snapshot != null && snapshot.Count > 0)
                {
                    var clones = snapshot
                        .Where(r => r != null)
                        .Select(r => r!.Clone())
                        .ToList();

                    if (clones.Count > 0)
                    {
                        using (SuppressAutoSaves())
                        {
                            _layout.Inspection = clones[0].Clone();
                            RefreshInspectionRoiSlots(clones);
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[batch-repos] snapshot restore failed: {ex.Message}");
            }

            return false;
        }

        private async Task ShowHeatmapOverlayAsync(Workflow.RoiExportResult export, byte[] heatmapBytes, double opacity)
        {
            await EnsureOverlayAlignedForHeatmapAsync().ConfigureAwait(false);

            if (export == null || heatmapBytes == null || heatmapBytes.Length == 0)
                return;

            try
            {
                BitmapSource heatmapSource;
                using (var ms = new MemoryStream(heatmapBytes))
                {
                    var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    heatmapSource = decoder.Frames[0];
                    heatmapSource.Freeze();
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    ClearHeatmapOverlay();
                    UpdateLastInferenceClassification();
                    _lastHeatmapRoi = HeatmapRoiModel.From(export.RoiImage.Clone());
                    _heatmapOverlayOpacity = Math.Clamp(opacity, 0.0, 1.0);
                    EnterAnalysisView();
                    WireExistingHeatmapControls();

                    if (_chkHeatmap != null)
                    {
                        _chkHeatmap.IsChecked = true;
                    }

                    CacheHeatmapGrayFromBitmapSource(heatmapSource);
                    RebuildHeatmapOverlayFromCache();
                    SyncHeatmapBitmapFromOverlay();

                    if (_lastHeatmapBmp != null)
                    {
                        LogHeatmap($"Heatmap Source: {_lastHeatmapBmp.PixelWidth}x{_lastHeatmapBmp.PixelHeight}, Fmt={_lastHeatmapBmp.Format}");
                    }
                    else
                    {
                        LogHeatmap("Heatmap Source: <null>");
                    }

                    // OPTIONAL: bump overlay opacity a bit (visual only)
                    if (HeatmapOverlay != null)
                    {
                        HeatmapOverlay.Visibility = Visibility.Visible;
                        HeatmapOverlay.Opacity = _heatmapOverlayOpacity;
                    }

                    UpdateHeatmapOverlayLayoutAndClip();

                    _heatmapOverlayOpacity = HeatmapOverlay?.Opacity ?? _heatmapOverlayOpacity;
                    LogDebug("[Eval] Heatmap overlay rebuilt and shown.");
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[heatmap] error: {ex.Message}");
            }
        }

        public void UpdateGlobalBadge(bool? ok)
        {
            if (DiskStatusHUD == null || DiskStatusText == null)
            {
                return;
            }

            if (ok == null)
            {
                DiskStatusHUD.Visibility = Visibility.Collapsed;
                return;
            }

            DiskStatusHUD.Visibility = Visibility.Visible;
            if (ok.Value)
            {
                DiskStatusText.Text = "✅  DISK OK";
                if (DiskStatusPanel != null)
                {
                    DiskStatusPanel.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#39FF14");
                }
            }
            else
            {
                DiskStatusText.Text = "❌  DISK NOK";
                if (DiskStatusPanel != null)
                {
                    DiskStatusPanel.BorderBrush = Brushes.Red;
                }
            }
        }

        private void ClearHeatmapOverlay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearHeatmapOverlay);
                return;
            }

            _lastHeatmapBmp = null;
            _lastHeatmapRoi = null;
            _lastHeatmapGray = null;
            _lastHeatmapW = 0;
            _lastHeatmapH = 0;
            _lastIsNg = false;
            LogHeatmap("Heatmap Source: <null>");
            UpdateHeatmapOverlayLayoutAndClip();
        }

        private void RefreshHeatmapOverlay()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshHeatmapOverlay);
                return;
            }
            UpdateHeatmapOverlayLayoutAndClip();
        }

        private static BitmapSource? ApplyHeatmapLut(BitmapSource src, double gain, double gamma)
        {
            if (src == null)
            {
                return null;
            }

            gain = Math.Clamp(gain, 0.25, 4.0);
            gamma = Math.Clamp(gamma, 0.25, 4.0);

            BitmapSource baseSrc = src;
            if (baseSrc.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(baseSrc, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                baseSrc = converted;
            }

            var wb = new WriteableBitmap(baseSrc);
            int width = wb.PixelWidth;
            int height = wb.PixelHeight;
            int stride = wb.BackBufferStride;
            int bpp = (wb.Format.BitsPerPixel + 7) / 8;
            byte[] pixels = new byte[stride * height];
            wb.CopyPixels(pixels, stride, 0);

            const double eps = 1e-6;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x * bpp;
                    double b = pixels[idx];
                    double g = pixels[idx + 1];
                    double r = pixels[idx + 2];
                    double lum = (0.0722 * b + 0.7152 * g + 0.2126 * r) / 255.0;
                    double safeLum = Math.Max(lum, eps);
                    double adjusted = Math.Pow(safeLum, gamma);
                    adjusted = Math.Clamp(adjusted * gain, 0.0, 1.0);
                    double scale = adjusted / safeLum;
                    pixels[idx] = (byte)Math.Round(Math.Clamp(b * scale, 0.0, 255.0));
                    pixels[idx + 1] = (byte)Math.Round(Math.Clamp(g * scale, 0.0, 255.0));
                    pixels[idx + 2] = (byte)Math.Round(Math.Clamp(r * scale, 0.0, 255.0));
                }
            }

            wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            wb.Freeze();
            return wb;
        }

        private void RebuildHeatmapOverlayFromCache()
        {
            if (HeatmapOverlay == null) return;
            if (_lastHeatmapGray == null || _lastHeatmapW <= 0 || _lastHeatmapH <= 0) return;

            // Build Turbo colormap LUT (once per rebuild; fast enough at this size)
            byte[] turboR = new byte[256], turboG = new byte[256], turboB = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                double rr = 0.13572138 + 4.61539260*t - 42.66032258*t*t + 132.13108234*t*t*t - 152.94239396*t*t*t*t + 59.28637943*t*t*t*t*t;
                double gg = 0.09140261 + 2.19418839*t + 4.84296658*t*t - 14.18503333*t*t*t + 14.13815831*t*t*t*t - 4.21519726*t*t*t*t*t;
                double bb = 0.10667330 + 12.64194608*t - 60.58204836*t*t + 139.27510080*t*t*t - 150.21747690*t*t*t*t + 59.17006120*t*t*t*t*t;
                turboR[i] = (byte)Math.Round(255.0 * Math.Clamp(rr, 0.0, 1.0));
                turboG[i] = (byte)Math.Round(255.0 * Math.Clamp(gg, 0.0, 1.0));
                turboB[i] = (byte)Math.Round(255.0 * Math.Clamp(bb, 0.0, 1.0));
            }

            // Normalize with global _heatmapNormMax (1.0=identity). If <1 -> brighter; if >1 -> darker.
            double denom = Math.Max(0.0001, _heatmapNormMax) * 255.0;

            byte[] bgra = new byte[_lastHeatmapW * _lastHeatmapH * 4];
            int idx = 0;
            for (int i = 0; i < _lastHeatmapGray.Length; i++)
            {
                // Map gray -> 0..255 index using the global normalization
                double v = _lastHeatmapGray[i];                 // 0..255
                int lut = (int)Math.Round(255.0 * Math.Clamp(v / denom, 0.0, 1.0));
                bgra[idx++] = turboB[lut];
                bgra[idx++] = turboG[lut];
                bgra[idx++] = turboR[lut];
                bgra[idx++] = 255; // opaque
            }

            // Create bitmap and assign
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(
                _lastHeatmapW, _lastHeatmapH, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, _lastHeatmapW, _lastHeatmapH), bgra, _lastHeatmapW * 4, 0);
            double baseGain = _heatmapGain;
            double baseGamma = _heatmapGamma;
            double effectiveGain = _lastIsNg ? Math.Max(baseGain, 1.35) : Math.Min(baseGain, 0.85);
            double effectiveGamma = _lastIsNg ? Math.Min(baseGamma, 0.85) : Math.Max(baseGamma, 1.15);
            var adjusted = ApplyHeatmapLut(wb, effectiveGain, effectiveGamma);

            if (adjusted != null)
            {
                HeatmapOverlay.Source = adjusted;
            }
            else
            {
                wb.Freeze();
                HeatmapOverlay.Source = wb;
            }
        }

        private void CacheHeatmapGrayFromBitmapSource(BitmapSource src)
        {
            if (src == null)
            {
                _lastHeatmapGray = null;
                _lastHeatmapW = 0;
                _lastHeatmapH = 0;
                return;
            }

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            byte[] gray;

            if (src.Format == PixelFormats.Gray8 || src.Format == PixelFormats.Indexed8)
            {
                gray = CopyGrayBytes(src);
            }
            else if (src.Format == PixelFormats.Gray16)
            {
                int stride = ((src.Format.BitsPerPixel * w) + 7) / 8;
                byte[] raw = new byte[h * stride];
                src.CopyPixels(raw, stride, 0);
                gray = new byte[w * h];
                for (int i = 0, j = 0; i < gray.Length; i++, j += 2)
                {
                    ushort val = (ushort)(raw[j] | (raw[j + 1] << 8));
                    gray[i] = (byte)Math.Round((val / 65535.0) * 255.0);
                }
            }
            else
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                conv.Freeze();
                gray = CopyGrayBytes(conv);
            }

            _lastHeatmapGray = gray;
            _lastHeatmapW = w;
            _lastHeatmapH = h;
        }

        private static byte[] CopyGrayBytes(BitmapSource src)
        {
            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = ((src.Format.BitsPerPixel * w) + 7) / 8;
            byte[] raw = new byte[h * stride];
            src.CopyPixels(raw, stride, 0);
            if (stride == w)
                return raw;

            byte[] trimmed = new byte[w * h];
            for (int row = 0; row < h; row++)
            {
                Buffer.BlockCopy(raw, row * stride, trimmed, row * w, w);
            }
            return trimmed;
        }

        private void SyncHeatmapBitmapFromOverlay()
        {
            if (HeatmapOverlay?.Source is BitmapSource bmp)
            {
                if (bmp.CanFreeze && !bmp.IsFrozen)
                {
                    try { bmp.Freeze(); } catch { }
                }
                _lastHeatmapBmp = bmp;
            }
        }

        private void HeatmapScaleSlider_ValueChangedSync(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SyncHeatmapBitmapFromOverlay();
        }

        // Map a [0,1] value to Turbo colormap (approx), returning (B,G,R) tuple
        private static (byte B, byte G, byte R) TurboLUT(double t)
        {
            if (double.IsNaN(t)) t = 0;
            if (t < 0) t = 0; if (t > 1) t = 1;
            // Polynomial approximation of Turbo (McIlroy 2019), clamped
            double r = 0.13572138 + 4.61539260*t - 42.66032258*t*t + 132.13108234*t*t*t - 152.94239396*t*t*t*t + 59.28637943*t*t*t*t*t;
            double g = 0.09140261 + 2.19418839*t + 4.84296658*t*t - 14.18503333*t*t*t + 14.13815831*t*t*t*t - 4.21519726*t*t*t*t*t;
            double b = 0.10667330 + 12.64194608*t - 60.58204836*t*t + 139.27510080*t*t*t - 150.21747690*t*t*t*t + 59.17006120*t*t*t*t*t;
            r = System.Math.Clamp(r, 0.0, 1.0);
            g = System.Math.Clamp(g, 0.0, 1.0);
            b = System.Math.Clamp(b, 0.0, 1.0);
            return ((byte)System.Math.Round(255*b), (byte)System.Math.Round(255*g), (byte)System.Math.Round(255*r));
        }

        // Build a visible BGRA32 heatmap with robust min/max normalization and optional colorization
        private static System.Windows.Media.Imaging.WriteableBitmap BuildVisibleHeatmap(
            System.Windows.Media.Imaging.BitmapSource src,
            bool useTurbo = true,   // set true for vivid colors
            double gamma = 0.9      // slight gamma to lift mid-tones
        )
        {
            if (src == null) return null;

            var fmt = src.Format;
            int w = src.PixelWidth, h = src.PixelHeight;

            // Extract raw buffer according to source format
            if (fmt == System.Windows.Media.PixelFormats.Gray8)
            {
                int stride = w;
                byte[] g8 = new byte[h * stride];
                src.CopyPixels(g8, stride, 0);

                // Compute min/max ignoring zeros
                int minv = 255, maxv = 0, countNZ = 0;
                for (int i = 0; i < g8.Length; i++)
                {
                    int v = g8[i];
                    if (v <= 0) continue;
                    if (v < minv) minv = v;
                    if (v > maxv) maxv = v;
                    countNZ++;
                }
                if (countNZ == 0) { minv = 0; maxv = 0; }

                double inv = (maxv > minv) ? 1.0 / (maxv - minv) : 0.0;

                byte[] bgra = new byte[w*h*4];
                for (int p = 0, q = 0; p < g8.Length; p++, q += 4)
                {
                    double t = (inv == 0.0) ? 0.0 : (g8[p] - minv) * inv;
                    if (gamma != 1.0) t = System.Math.Pow(t, 1.0 / System.Math.Max(1e-6, gamma));
                    if (useTurbo)
                    {
                        var (B,G,R) = TurboLUT(t);
                        bgra[q+0] = B; bgra[q+1] = G; bgra[q+2] = R; bgra[q+3] = 255;
                    }
                    else
                    {
                        byte u = (byte)System.Math.Round(255.0 * t);
                        bgra[q+0] = u; bgra[q+1] = u; bgra[q+2] = u; bgra[q+3] = 255;
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h, src.DpiX, src.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w*4, 0);
                wb.Freeze();
                return wb;
            }
            else if (fmt == System.Windows.Media.PixelFormats.Gray16)
            {
                int stride = w * 2;
                byte[] raw = new byte[h * stride];
                src.CopyPixels(raw, stride, 0);

                // Convert bytes→ushort (Little Endian)
                int N = w*h;
                ushort[] g16 = new ushort[N];
                for (int i = 0, j = 0; i < N; i++, j += 2)
                    g16[i] = (ushort)(raw[j] | (raw[j+1] << 8));

                int minv = ushort.MaxValue, maxv = 0, countNZ = 0;
                for (int i = 0; i < N; i++)
                {
                    int v = g16[i];
                    if (v <= 0) continue;
                    if (v < minv) minv = v;
                    if (v > maxv) maxv = v;
                    countNZ++;
                }
                if (countNZ == 0) { minv = 0; maxv = 0; }

                double inv = (maxv > minv) ? 1.0 / (maxv - minv) : 0.0;

                byte[] bgra = new byte[N*4];
                for (int i = 0, q = 0; i < N; i++, q += 4)
                {
                    double t = (inv == 0.0) ? 0.0 : (g16[i] - minv) * inv;
                    if (gamma != 1.0) t = System.Math.Pow(t, 1.0 / System.Math.Max(1e-6, gamma));
                    if (useTurbo)
                    {
                        var (B,G,R) = TurboLUT(t);
                        bgra[q+0] = B; bgra[q+1] = G; bgra[q+2] = R; bgra[q+3] = 255;
                    }
                    else
                    {
                        byte u = (byte)System.Math.Round(255.0 * t);
                        bgra[q+0] = u; bgra[q+1] = u; bgra[q+2] = u; bgra[q+3] = 255;
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h, src.DpiX, src.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w*4, 0);
                wb.Freeze();
                return wb;
            }
            else
            {
                // Convert to BGRA32 and compute luminance min/max ignoring zeros
                var conv = (fmt != System.Windows.Media.PixelFormats.Bgra32)
                    ? new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0)
                    : src;

                int stride = w * 4;
                byte[] buf = new byte[h * stride];
                conv.CopyPixels(buf, stride, 0);

                int minv = 255, maxv = 0, countNZ = 0;
                for (int q = 0; q < buf.Length; q += 4)
                {
                    // premultiplied alpha is fine: we treat zeros as background
                    byte b = buf[q+0], g = buf[q+1], r = buf[q+2];
                    int lum = (int)System.Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b);
                    if (lum <= 0) continue;
                    if (lum < minv) minv = lum;
                    if (lum > maxv) maxv = lum;
                    countNZ++;
                }
                if (countNZ == 0) { minv = 0; maxv = 0; }

                double inv = (maxv > minv) ? 1.0 / (maxv - minv) : 0.0;

                byte[] bgra = new byte[h * stride];
                for (int q = 0; q < buf.Length; q += 4)
                {
                    byte b = buf[q+0], g = buf[q+1], r = buf[q+2];
                    int lum = (int)System.Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b);
                    double t = (inv == 0.0) ? 0.0 : (lum - minv) * inv;
                    if (t < 0) t = 0; if (t > 1) t = 1;
                    if (gamma != 1.0) t = System.Math.Pow(t, 1.0 / System.Math.Max(1e-6, gamma));
                    if (useTurbo)
                    {
                        var (B,G,R) = TurboLUT(t);
                        bgra[q+0] = B; bgra[q+1] = G; bgra[q+2] = R; bgra[q+3] = 255;
                    }
                    else
                    {
                        byte u = (byte)System.Math.Round(255.0 * t);
                        bgra[q+0] = u; bgra[q+1] = u; bgra[q+2] = u; bgra[q+3] = 255;
                    }
                }

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(w, h, conv.DpiX, conv.DpiY,
                    System.Windows.Media.PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w*4, 0);
                wb.Freeze();
                return wb;
            }
        }


        private void ResetAnalysisMarks(bool preserveLastCenters = false)
        {
            RemoveAnalysisMarks();

            bool hadCenters = _lastM1CenterPx.HasValue || _lastM2CenterPx.HasValue || _lastMidCenterPx.HasValue;

            if (!preserveLastCenters)
            {
                _lastM1CenterPx = null;
                _lastM2CenterPx = null;
                _lastMidCenterPx = null;
            }

            if (!preserveLastCenters || hadCenters)
            {
                RedrawAnalysisCrosses();
            }

            ClearHeatmapOverlay();
            RedrawOverlaySafe();

            if (!preserveLastCenters)
            {
                _analysisViewActive = false;
            }

            AppendLog(preserveLastCenters
                ? "[ANALYZE] Limpieza de análisis preservando cruces previas."
                : "[ANALYZE] Limpiadas marcas de análisis (cruces).");
        }

        private void RemoveAnalysisMarks()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RemoveAnalysisMarks);
                return;
            }

            if (CanvasROI == null)
                return;

            var toRemove = CanvasROI.Children
                .OfType<FrameworkElement>()
                .Where(el => el.Tag is string tag && tag == ANALYSIS_TAG)
                .ToList();

            foreach (var el in toRemove)
            {
                CanvasROI.Children.Remove(el);
            }
        }

        private void DrawCross(double x, double y, int size, Brush brush, double thickness)
        {
            if (CanvasROI == null)
                return;

            double half = size;

            var container = new Canvas
            {
                Width = half * 2,
                Height = half * 2,
                IsHitTestVisible = false,
                Tag = ANALYSIS_TAG
            };

            var lineH = new WLine
            {
                X1 = 0,
                Y1 = half,
                X2 = half * 2,
                Y2 = half,
                Stroke = brush,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            var lineV = new WLine
            {
                X1 = half,
                Y1 = 0,
                X2 = half,
                Y2 = half * 2,
                Stroke = brush,
                StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };

            container.Children.Add(lineH);
            container.Children.Add(lineV);

            Canvas.SetLeft(container, x - half);
            Canvas.SetTop(container, y - half);
            Panel.SetZIndex(container, 2000);

            CanvasROI.Children.Add(container);
        }

        private void DrawLabeledCross(double x, double y, string label, Brush crossColor, Brush labelBg, Brush labelFg, int crossSize, double thickness)
        {
            DrawCross(x, y, crossSize, crossColor, thickness);

            if (CanvasROI == null)
                return;

            var text = new TextBlock
            {
                Text = label,
                Foreground = labelFg,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2)
            };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);

            var border = new Border
            {
                Background = labelBg,
                CornerRadius = new CornerRadius(6),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(0.8),
                Child = text,
                Opacity = 0.92,
                Tag = ANALYSIS_TAG
            };

            Canvas.SetLeft(border, x + LabelOffsetX);
            Canvas.SetTop(border, y + LabelOffsetY);
            Panel.SetZIndex(border, 31);

            CanvasROI.Children.Add(border);
        }

        private void DrawMasterMatch(RoiModel roi, SWPoint matchImagePoint, string caption, Brush brush, bool withLabel)
        {
            var canvasPoint = ImagePxToCanvasPt(matchImagePoint.X, matchImagePoint.Y);
            var canvasRoi = ImageToCanvas(roi);
            double reference = Math.Max(canvasRoi.Width, canvasRoi.Height);
            if (reference <= 0)
            {
                reference = Math.Max(CanvasROI?.ActualWidth ?? 0, CanvasROI?.ActualHeight ?? 0) * 0.05;
            }

            int size = (int)Math.Round(Math.Clamp(reference * 0.2, 14, 60));
            double thickness = Math.Max(2.0, size / 8.0);

            if (withLabel)
            {
                DrawLabeledCross(canvasPoint.X, canvasPoint.Y, caption, brush, Brushes.Black, Brushes.White, size, thickness);
            }
            else
            {
                DrawCross(canvasPoint.X, canvasPoint.Y, size, brush, thickness);
            }

        }
        private void DrawMasterCenters(SWPoint c1Canvas, SWPoint c2Canvas, SWPoint midCanvas)
        {
            DrawCross(c1Canvas.X, c1Canvas.Y, 20, Brushes.LimeGreen, 2);
            DrawCross(c2Canvas.X, c2Canvas.Y, 20, Brushes.Orange, 2);
            DrawCross(midCanvas.X, midCanvas.Y, 24, Brushes.Red, 2);
        }

        private SWPoint ImageToCanvasPoint(SWPoint imagePoint)
            => ImagePxToCanvasPt(imagePoint.X, imagePoint.Y);

        private static SWPoint Mid(SWPoint a, SWPoint b)
            => new SWPoint((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);


        private void RedrawAnalysisCrosses()
        {
            if (CanvasROI == null)
                return;

            RemoveAnalysisMarks();

            SWPoint? m1Canvas = _lastM1CenterPx.HasValue ? ImagePxToCanvasPt(_lastM1CenterPx.Value) : (SWPoint?)null;
            SWPoint? m2Canvas = _lastM2CenterPx.HasValue ? ImagePxToCanvasPt(_lastM2CenterPx.Value) : (SWPoint?)null;

            if (m1Canvas.HasValue && m2Canvas.HasValue)
            {
                var midCanvas = _lastMidCenterPx.HasValue
                    ? ImagePxToCanvasPt(_lastMidCenterPx.Value)
                    : Mid(m1Canvas.Value, m2Canvas.Value);

                DrawMasterCenters(m1Canvas.Value, m2Canvas.Value, midCanvas);
            }
            else
            {
                if (m1Canvas.HasValue)
                {
                    DrawCross(m1Canvas.Value.X, m1Canvas.Value.Y, 20, Brushes.LimeGreen, 2);
                }

                if (m2Canvas.HasValue)
                {
                    DrawCross(m2Canvas.Value.X, m2Canvas.Value.Y, 20, Brushes.Orange, 2);
                }

                if (_lastMidCenterPx.HasValue)
                {
                    var midCanvas = ImagePxToCanvasPt(_lastMidCenterPx.Value);
                    DrawCross(midCanvas.X, midCanvas.Y, 24, Brushes.Red, 2);
                }
            }
        }

        private void EnterAnalysisView()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(EnterAnalysisView);
                return;
            }

            _analysisViewActive = true;
            if (MainTabs != null && TabSetupInspect != null)
            {
                MainTabs.SelectedItem = TabSetupInspect;
            }
        }

        private string? ResolveRoiLabelText(RoiModel roi)
        {
            if (roi == null)
                return null;

            if (!string.IsNullOrWhiteSpace(roi.Label)
                && !string.Equals(roi.Label, "Inspection", StringComparison.OrdinalIgnoreCase))
                return roi.Label;

            if (roi.Role == RoiRole.Inspection)
            {
                var index = TryParseInspectionIndex(roi) ?? _activeInspectionIndex;
                return GetInspectionDisplayName(index);
            }

            return roi.Role switch
            {
                RoiRole.Master1Pattern => "Master 1",
                RoiRole.Master1Search => "Master 1 search",
                RoiRole.Master2Pattern => "Master 2",
                RoiRole.Master2Search => "Master 2 search",
                _ => null
            };
        }

        private IReadOnlyList<RoiModel> CollectSavedInspectionRois()
        {
            var results = new List<RoiModel>();
            var seenRefs = new HashSet<RoiModel>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void add(RoiModel? roi, int? slotIndex = null)
            {
                if (roi == null || !IsRoiSaved(roi))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(roi.Id))
                {
                    if (!seenIds.Add(roi.Id))
                    {
                        return;
                    }
                }
                else if (!seenRefs.Add(roi))
                {
                    return;
                }

                int index = slotIndex ?? TryParseInspectionIndex(roi) ?? (results.Count + 1);
                NormalizeInspectionRoi(roi, Math.Max(1, Math.Min(4, index)));
                results.Add(roi);
            }

            for (int index = 1; index <= 4; index++)
            {
                var slot = GetInspectionSlotModel(index);
                add(slot, index);
            }

            add(_preset?.Inspection1, 1);
            add(_preset?.Inspection2, 2);

            if (_preset?.Rois != null)
            {
                foreach (var roi in _preset.Rois)
                {
                    if (roi?.Role != RoiRole.Inspection)
                    {
                        continue;
                    }

                    add(roi);
                }
            }

            return results;
        }

        private void UpdateRoiHud()
        {
            if (RoiHudStack == null || RoiHudOverlay == null || _layout == null)
                return;

            int count = 0;
            RoiHudStack.Children.Clear();

            // Masters (Patterns)
            if (_layout.Master1Pattern != null && IsRoiSaved(_layout.Master1Pattern))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 1 (Pattern)",
                    () => _showMaster1PatternOverlay,
                    v  => _showMaster1PatternOverlay = v));
                count++;
            }
            if (_layout.Master2Pattern != null && IsRoiSaved(_layout.Master2Pattern))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 2 (Pattern)",
                    () => _showMaster2PatternOverlay,
                    v  => _showMaster2PatternOverlay = v));
                count++;
            }

            // Master Searches
            if (_layout.Master1Search != null && IsRoiSaved(_layout.Master1Search))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 1 Search",
                    () => _showMaster1SearchOverlay,
                    v  => _showMaster1SearchOverlay = v));
                count++;
            }
            if (_layout.Master2Search != null && IsRoiSaved(_layout.Master2Search))
            {
                RoiHudStack.Children.Add(CreateHudItem("Master 2 Search",
                    () => _showMaster2SearchOverlay,
                    v  => _showMaster2SearchOverlay = v));
                count++;
            }

            // Inspection ROIs (saved only)
            var savedInspectionRois = CollectSavedInspectionRois();
            foreach (var roi in savedInspectionRois)
            {
                RoiHudStack.Children.Add(CreateRoiHudItem(roi));
                count++;
            }

            RoiHudOverlay.Visibility = (count > 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement CreateHudItem(string label, Func<bool> getVisible, Action<bool> setVisible)
        {
            bool isVisible = getVisible();

            var text = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var eye = new TextBlock
            {
                Text = isVisible ? "👁" : "🚫",
                Foreground = isVisible ? Brushes.Lime : Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(6, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(text);
            sp.Children.Add(eye);

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Black,
                BorderBrush = isVisible ? (Brush)new BrushConverter().ConvertFromString("#39FF14") : Brushes.DimGray,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = sp,
                Cursor = Cursors.Hand
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                bool now = !getVisible();
                setVisible(now);
                eye.Text = now ? "👁" : "🚫";
                border.BorderBrush = now ? (Brush)new BrushConverter().ConvertFromString("#39FF14") : Brushes.DimGray;
                try { RedrawAllRois(); } catch { }
                e.Handled = true;
            };

            return border;
        }

        private FrameworkElement CreateRoiHudItem(RoiModel roi)
        {
            var labelText = ResolveRoiLabelText(roi) ?? roi.Label ?? "Inspection";
            bool isVisible = IsRoiRoleVisible(roi.Role);

            var text = new TextBlock
            {
                Text = labelText,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var eye = new TextBlock
            {
                Text = isVisible ? "👁" : "🚫",
                Foreground = isVisible ? (Brush)RoiHudAccentBrush : Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(6, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(text);
            stack.Children.Add(eye);

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Black,
                BorderBrush = isVisible ? (Brush)RoiHudAccentBrush : Brushes.DimGray,
                BorderThickness = new Thickness(1.5),
                Margin = new Thickness(0, 0, 0, 6),
                Child = stack,
                Tag = roi,
                Cursor = Cursors.Hand
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                ToggleRoiVisibility(roi.Role);
                e.Handled = true;
            };

            return border;
        }

        private void ToggleRoiVisibility(RoiRole role)
        {
            if (!_roiCheckboxHasRoi.TryGetValue(role, out var hasRoi) || !hasRoi)
            {
                return;
            }

            bool newState = !GetRoiVisibility(role);
            SetRoiVisibility(role, newState);
            RedrawAllRois();
        }

        private void RefreshCreateButtonsEnabled()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshCreateButtonsEnabled);
                return;
            }

            if (BtnCreateInspection1 != null)
            {
                BtnCreateInspection1.IsEnabled = !HasInspectionRoi(1);
            }

            if (BtnCreateInspection2 != null)
            {
                BtnCreateInspection2.IsEnabled = !HasInspectionRoi(2);
            }

            if (BtnCreateInspection3 != null)
            {
                BtnCreateInspection3.IsEnabled = !HasInspectionRoi(3);
            }

            if (BtnCreateInspection4 != null)
            {
                BtnCreateInspection4.IsEnabled = !HasInspectionRoi(4);
            }
        }

        private void RefreshInspectionRoiSlots(IReadOnlyList<RoiModel>? rois = null)
        {
            var source = rois ?? CollectSavedInspectionRois();
            var slots = new RoiModel?[4];

            if (rois == null)
            {
                foreach (var roi in source)
                {
                    if (roi == null)
                    {
                        continue;
                    }

                    var slotIndex = TryParseInspectionIndex(roi);

                    if (!slotIndex.HasValue || slotIndex < 1 || slotIndex > 4 || slots[slotIndex.Value - 1] != null)
                    {
                        var nextFree = Array.FindIndex(slots, s => s == null);
                        slotIndex = nextFree >= 0 ? nextFree + 1 : null;
                    }

                    if (slotIndex.HasValue && slotIndex.Value >= 1 && slotIndex.Value <= 4 && slots[slotIndex.Value - 1] == null)
                    {
                        slots[slotIndex.Value - 1] = roi;
                    }
                }
            }
            else
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = source.Count > i ? source[i] : null;
                }
            }

            for (int i = 0; i < slots.Length; i++)
            {
                var model = slots[i];
                RoiDiag(string.Format(
                    CultureInfo.InvariantCulture,
                    "[slot-refresh:inspection] slot={0} srcId={1} shape={2} base=({3}x{4}) Img(L={5:0.###},T={6:0.###},W={7:0.###},H={8:0.###})",
                    i + 1,
                    model?.Id ?? "<null>",
                    model?.Shape.ToString() ?? "<none>",
                    model?.BaseImgW?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-",
                    model?.BaseImgH?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-",
                    model?.Left ?? 0,
                    model?.Top ?? 0,
                    model?.Width ?? 0,
                    model?.Height ?? 0));
                SetInspectionSlotModel(i + 1, model, updateActive: false);
            }

            Inspection1 = GetInspectionSlotModel(1);
            Inspection2 = GetInspectionSlotModel(2);
            Inspection3 = GetInspectionSlotModel(3);
            Inspection4 = GetInspectionSlotModel(4);

            RoiDiag($"[slot-refresh] layout slot1={(Inspection1?.Id ?? "<null>")} slot2={(Inspection2?.Id ?? "<null>")} slot3={(Inspection3?.Id ?? "<null>")} slot4={(Inspection4?.Id ?? "<null>")}");

            _workflowViewModel?.SetInspectionRoiModels(Inspection1, Inspection2, Inspection3, Inspection4);
            SetActiveInspectionIndex(_activeInspectionIndex);
            RefreshCreateButtonsEnabled();
            UpdateEditableConfigState();
            ApplyInspectionInteractionPolicy("slot-refresh");
        }

        private void SaveCurrentInspectionToSlot(int index)
        {
            if (_layout == null)
            {
                MessageBox.Show("No hay layout cargado.", $"Inspection {index}", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var layoutIo = ViewModel?.BeginLayoutIo($"save-slot:{index}");

            var slotSnapshot = GetInspectionSlotModelClone(index);
            LogSaveSlot("pre-slot", index, slotSnapshot);

            var roiModel = BuildCurrentRoiModel();
            LogSaveSlot("pre-current", index, roiModel);

            if (!HasDrawableGeometry(roiModel))
            {
                AppendLog($"[save-slot] idx={index} ABORT: current ROI has no geometry");
                MessageBox.Show($"Dibuja un ROI antes de guardar el slot {index}.",
                                $"Inspection {index}",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            if (slotSnapshot != null)
            {
                roiModel.Id = slotSnapshot.Id;
                if (string.IsNullOrWhiteSpace(roiModel.Label))
                {
                    roiModel.Label = slotSnapshot.Label;
                }

                if (!roiModel.BaseImgW.HasValue && slotSnapshot.BaseImgW.HasValue)
                {
                    roiModel.BaseImgW = slotSnapshot.BaseImgW;
                }

                if (!roiModel.BaseImgH.HasValue && slotSnapshot.BaseImgH.HasValue)
                {
                    roiModel.BaseImgH = slotSnapshot.BaseImgH;
                }
            }

            roiModel.IsFrozen = false;
            NormalizeInspectionRoi(roiModel, index);

            if (_imgW > 0 && _imgH > 0)
            {
                roiModel.BaseImgW = _imgW;
                roiModel.BaseImgH = _imgH;
            }

            RoiDiag($"[save-slot] idx={index} source=slot roiId={roiModel.Id} role={roiModel.Role} shape={roiModel.Shape} img=({_imgW}x{_imgH}) base=({roiModel.BaseImgW}x{roiModel.BaseImgH}) rect=({roiModel.Left:F1},{roiModel.Top:F1},{roiModel.Width:F1},{roiModel.Height:F1})");

            var config = GetInspectionConfigByIndex(index);
            if (config != null)
            {
                config.Id = $"Inspection_{index}";
                config.Shape = roiModel.Shape;
                if (!string.IsNullOrWhiteSpace(roiModel.Label)
                    && (string.IsNullOrWhiteSpace(config.Name)
                        || config.Name.StartsWith("Inspection", StringComparison.OrdinalIgnoreCase)))
                {
                    config.Name = roiModel.Label;
                }
                config.BaseImgW = roiModel.BaseImgW ?? _imgW;
                config.BaseImgH = roiModel.BaseImgH ?? _imgH;
                RoiDiag($"[save-slot] idx={index} configId={config.Id} shape={config.Shape} base=({config.BaseImgW}x{config.BaseImgH}) name='{config.Name}'");
            }

            GuiLog.Info($"[inspection] save slot={index} roi={roiModel.Label ?? roiModel.Id} shape={roiModel.Shape}");

            RoiDiag($"[save-slot] idx={index} persist base=({roiModel.BaseImgW}x{roiModel.BaseImgH})");

            var savedClone = roiModel.Clone();
            SetInspectionSlotModel(index, savedClone);

            var persistedSlot = GetInspectionSlotModel(index);
            if (persistedSlot != null)
            {
                LogRoi("[save-slot] persisted", index, persistedSlot);
                PresetManager.SaveInspection(_preset, index, persistedSlot, line => RoiDiag(line));
            }
            else
            {
                AppendLog($"[save-slot] idx={index} WARN: persisted slot missing after update");
            }
            try
            {
                PresetManager.Save(_preset);
            }
            catch (Exception ex)
            {
                RoiDiag($"[save-slot] preset-save-error idx={index} ex={ex.Message}");
            }

            RefreshInspectionRoiSlots();
            SetActiveInspectionIndex(index);
            EnsureInspectionDatasetStructure();
            TryPersistLayout();

            DumpUiShapesMap($"save-slot:{index}");

            LogSaveSlot("post", index, GetInspectionSlotModelClone(index));
            AppendLog($"[inspection] saved slot={index} ok");
            ExitEditMode("save");
            MessageBox.Show($"Inspection {index} guardado.",
                            $"Inspection {index}",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
        }

        private static bool HasDrawableGeometry(RoiModel roi)
        {
            if (roi == null)
            {
                return false;
            }

            static bool IsPositive(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 1.0;

            return roi.Shape switch
            {
                RoiShape.Rectangle => IsPositive(roi.Width) && IsPositive(roi.Height),
                RoiShape.Circle or RoiShape.Annulus =>
                    IsPositive(roi.R) || IsPositive(Math.Max(roi.Width, roi.Height) / 2.0),
                _ => false,
            };
        }

        private void ToggleInspectionEdit(int index)
        {
            var config = GetInspectionConfigByIndex(index);
            if (config == null)
            {
                GuiLog.Warn($"[workflow-edit] ToggleInspectionEdit ignored: missing config for index={index}");
                Snack($"Inspection {index} no tiene un ROI para editar.");
                return;
            }

            ToggleInspectionEdit(config);
        }

        private void ApplyInspectionToggleEdit(string? roiId, int index, string source = "unknown")
        {
            Dispatcher.Invoke(() =>
            {
                var vm = ViewModel;
                if (vm?.InspectionRois == null || vm.InspectionRois.Count == 0)
                {
                    GuiLog.Warn($"[workflow-edit] ToggleEdit ignored: InspectionRois empty roi='{roiId}' index={index} source={source}");
                    return;
                }

                var cfg = vm.InspectionRois.FirstOrDefault(r => string.Equals(r.Id, roiId, StringComparison.OrdinalIgnoreCase))
                          ?? vm.InspectionRois.FirstOrDefault(r => r.Index == index);

                if (cfg == null)
                {
                    GuiLog.Warn($"[workflow-edit] ToggleEdit unknown ROI roi='{roiId}' index={index} source={source}");
                    return;
                }

                GuiLog.Info($"[workflow-edit] ApplyInspectionToggleEdit source={source} roi='{cfg.Id}' index={cfg.Index} enabled={cfg.Enabled} isEditable={cfg.IsEditable} editingSlot={_editingInspectionSlot?.ToString() ?? "null"} activeEditableRoiId='{_activeEditableRoiId}'");

                ToggleInspectionEdit(cfg);
            });
        }

        private void ToggleInspectionEdit(InspectionRoiConfig config)
        {
            var index = config.Index;
            var roiId = config.Id;

            GuiLog.Info($"[workflow-edit] toggle request roi='{roiId}' index={index} enabled={config.Enabled} isEditable={config.IsEditable} editingSlot={_editingInspectionSlot?.ToString() ?? "null"} activeEditableRoiId='{_activeEditableRoiId}'");

            if (!config.Enabled)
            {
                GuiLog.Warn($"[workflow-edit] ToggleInspectionEdit aborted: roi='{roiId}' index={index} disabled");
                Snack($"Inspection {index} está deshabilitado.");
                return;
            }

            var roiModel = FindInspectionRoiModel(roiId) ?? GetInspectionSlotModel(index);
            if (roiModel == null)
            {
                GuiLog.Warn($"[workflow-edit] ToggleInspectionEdit: no RoiModel for roi='{roiId}' index={index}");
                Snack($"Inspection {index} no tiene un ROI para editar.");
                return;
            }

            if (string.IsNullOrWhiteSpace(roiId))
            {
                roiId = roiModel.Id;
            }

            if (string.IsNullOrWhiteSpace(roiId))
            {
                GuiLog.Warn($"[workflow-edit] ToggleInspectionEdit: missing roiId for index={index}");
                Snack($"Inspection {index} no tiene un ROI para editar.");
                return;
            }

            bool switchingRoi = _editingInspectionSlot.HasValue && _editingInspectionSlot.Value != index;
            if (switchingRoi)
            {
                GuiLog.Info($"[workflow-edit] Switching edit mode from ROI='{_activeEditableRoiId}' slot={_editingInspectionSlot?.ToString() ?? "null"} to ROI='{roiId}' slot={index}");
                ExitInspectionEditMode(saveChanges: true);
            }

            // Make sure the shape exists so the adorner can attach immediately on the first click.
            EnsureShapeForRoi(roiModel);

            if (ViewModel != null)
            {
                ViewModel.SelectedInspectionRoi = config;
            }

            SetActiveInspectionIndex(index);

            // Determine if THIS slot is currently the active inspection being edited,
            // using the global state (_editingInspectionSlot), not only config.IsEditable.
            bool isActiveEditing =
                _editingInspectionSlot.HasValue &&
                _editingInspectionSlot.Value == index;

            // If this ROI is NOT currently active → we want to ENTER edit mode.
            // If it IS the active one → we want to EXIT edit mode.
            bool makeEditable = !isActiveEditing;

            GuiLog.Info(
                $"[workflow-edit] decision roi='{roiId}' index={index} " +
                $"isActiveEditing={isActiveEditing} makeEditable={makeEditable} " +
                $"config.IsEditable(before)={config.IsEditable} " +
                $"editingSlot={_editingInspectionSlot?.ToString() ?? "null"} " +
                $"globalUnlocked={_globalUnlocked} activeEditableRoiId='{_activeEditableRoiId}'");

            if (makeEditable)
            {
                // Enter edit mode for this ROI.
                // EnterInspectionEditMode already handles closing another ROI in edit mode if needed.
                EnterInspectionEditMode(index, roiId);
                roiModel.IsFrozen = false;
            }
            else
            {
                // Exit edit mode of the current ROI, saving changes.
                ExitInspectionEditMode(saveChanges: true);
                roiModel.IsFrozen = true;
            }

            // Keep the config flag in sync with the new state.
            config.IsEditable = makeEditable;

            GuiLog.Info(
                $"[workflow-edit] applied roi='{roiId}' index={index} " +
                $"isEditable={config.IsEditable} frozen={roiModel.IsFrozen} " +
                $"editingSlot={_editingInspectionSlot?.ToString() ?? "null"} " +
                $"globalUnlocked={_globalUnlocked} activeEditableRoiId='{_activeEditableRoiId}'");
        }

        private async void BtnToggleEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is InspectionRoiConfig cfg)
            {
                GuiLog.Info($"[workflow-edit] BtnToggleEdit_Click(main) roi='{cfg.Id}' index={cfg.Index} enabled={cfg.Enabled} isEditable={cfg.IsEditable}");

                bool requiresTabSwitch = ViewModel?.SelectedInspectionRoi?.Index != cfg.Index;
                if (requiresTabSwitch)
                {
                    var previous = ViewModel?.SelectedInspectionRoi?.Index;
                    GuiLog.Info($"[workflow-edit] side-click: switching inspection tab from {previous?.ToString() ?? "null"} to {cfg.Index}");

                    // Activate the corresponding inspection tab before toggling edit mode so the adorner can attach.
                    SetActiveInspectionIndex(cfg.Index);

                    // Allow the UI to render the newly selected tab before applying the toggle logic.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }

                ApplyInspectionToggleEdit(cfg.Id, cfg.Index, source: "side-panel");
            }
            else
            {
                GuiLog.Warn("[workflow-edit] BtnToggleEdit_Click(main) ignored: invalid sender/DataContext");
            }
        }

        private void BtnCreateInspection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag ||
                !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return;
            }

            if (_imgW <= 0 || _imgH <= 0)
            {
                Snack($"Carga primero una imagen antes de crear un ROI de inspección."); // CODEX: string interpolation compatibility.
                return;
            }

            if (HasInspectionRoi(index))
            {
                Snack($"Inspection {index} ya existe.");
                return;
            }

            var shape = ReadInspectionShapeForIndex(index);
            var roi = CreateDefaultInspectionRoi(index, shape);

            AppendLog($"[inspection] create slot={index} shape={shape} id={roi.Id}");

            SaveInspectionRoiToLayout(index, roi);
            RefreshInspectionRoiSlots();

            var stored = GetInspectionSlotModel(index);
            if (stored != null)
            {
                SyncCurrentRoiFromInspection(stored);
            }

            SetActiveInspectionIndex(index);

            var config = GetInspectionConfigByIndex(index);
            if (config != null)
            {
                ToggleInspectionEdit(config);
            }
            else
            {
                GuiLog.Warn($"[workflow-edit] Toggle after create failed: missing config for index={index}");
            }
            UpdateRoiHud();
        }

        private RoiModel CreateDefaultInspectionRoi(int index, RoiShape shape)
        {
            double imgW = Math.Max(1, _imgW);
            double imgH = Math.Max(1, _imgH);
            double cx = imgW / 2.0;
            double cy = imgH / 2.0;
            double minSide = Math.Min(imgW, imgH);

            var roi = new RoiModel
            {
                Id = $"Inspection_{index}",
                Role = RoiRole.Inspection,
                Shape = shape,
                BaseImgW = imgW,
                BaseImgH = imgH,
                AngleDeg = 0,
                IsFrozen = false
            };

            switch (shape)
            {
                case RoiShape.Rectangle:
                    {
                        double width = Math.Max(100, imgW * 0.25);
                        double height = Math.Max(100, imgH * 0.25);
                        width = Math.Min(width, imgW - 20);
                        height = Math.Min(height, imgH - 20);
                        width = Math.Max(40, width);
                        height = Math.Max(40, height);

                        roi.Width = width;
                        roi.Height = height;
                        roi.X = cx;
                        roi.Y = cy;
                        roi.CX = cx;
                        roi.CY = cy;
                        roi.R = Math.Min(width, height) / 2.0;
                        break;
                    }
                case RoiShape.Circle:
                    {
                        double radius = Math.Max(60, minSide * 0.12);
                        radius = Math.Min(radius, (minSide / 2.0) - 10);
                        if (radius <= 0)
                        {
                            radius = Math.Max(30, minSide / 3.0);
                        }

                        roi.R = radius;
                        roi.CX = cx;
                        roi.CY = cy;
                        roi.Width = radius * 2.0;
                        roi.Height = radius * 2.0;
                        roi.X = cx;
                        roi.Y = cy;
                        break;
                    }
                case RoiShape.Annulus:
                    {
                        double outer = Math.Max(60, minSide * 0.16);
                        outer = Math.Min(outer, (minSide / 2.0) - 10);
                        if (outer <= 0)
                        {
                            outer = Math.Max(40, minSide / 3.0);
                        }

                        double inner = Math.Max(30, outer * 0.5);
                        inner = Math.Min(inner, outer - 15);
                        inner = Math.Max(10, inner);

                        roi.R = outer;
                        roi.RInner = inner;
                        roi.CX = cx;
                        roi.CY = cy;
                        roi.Width = outer * 2.0;
                        roi.Height = outer * 2.0;
                        roi.X = cx;
                        roi.Y = cy;
                        break;
                    }
            }

            return roi;
        }

        private void SaveInspectionRoiToLayout(int index, RoiModel roi)
        {
            if (_layout == null)
            {
                return;
            }

            roi.Role = RoiRole.Inspection;
            roi.Id = $"Inspection_{index}";
            roi.IsFrozen = false;

            if (_imgW > 0 && _imgH > 0)
            {
                roi.BaseImgW = _imgW;
                roi.BaseImgH = _imgH;
            }

            SetInspectionSlotModel(index, roi, updateActive: false);
        }

        private void BtnRemoveInspection1_Click(object sender, RoutedEventArgs e) => RemoveInspectionRoi(1);

        private void BtnRemoveInspection2_Click(object sender, RoutedEventArgs e) => RemoveInspectionRoi(2);

        private void BtnRemoveInspection3_Click(object sender, RoutedEventArgs e) => RemoveInspectionRoi(3);

        private void BtnRemoveInspection4_Click(object sender, RoutedEventArgs e) => RemoveInspectionRoi(4);

        private void RemoveInspectionRoi(int index)
        {
            if (_layout == null)
                return;

            var roiModel = GetInspectionSlotModel(index);
            if (roiModel == null)
                return;

            bool wasActive = !string.IsNullOrWhiteSpace(_activeEditableRoiId)
                && !string.IsNullOrWhiteSpace(roiModel.Id)
                && string.Equals(_activeEditableRoiId, roiModel.Id, StringComparison.OrdinalIgnoreCase);

            if (wasActive)
            {
                ExitEditMode("remove-active", skipCapture: true);
            }

            SetInspectionSlotModel(index, null);

            string slotId = $"Inspection_{index}";
            if (index == 1)
            {
                _preset.Inspection1 = null;
            }
            else if (index == 2)
            {
                _preset.Inspection2 = null;
            }

            if (_preset.Rois != null && _preset.Rois.Count > 0)
            {
                _preset.Rois.RemoveAll(r => r != null &&
                    !string.IsNullOrWhiteSpace(r.Id) &&
                    string.Equals(r.Id, slotId, StringComparison.OrdinalIgnoreCase));
            }

            try
            {
                PresetManager.Save(_preset);
                RoiDiag($"[remove-slot] idx={index} preset-cleared id={slotId}");
            }
            catch (Exception ex)
            {
                RoiDiag($"[remove-slot] idx={index} preset-clear-error ex={ex.Message}");
            }

            if (index == 1 || _activeInspectionIndex == index)
            {
                _layout.Inspection = null;
                SetInspectionBaseline(null);

                if (_state == MasterState.Ready && _activeInspectionIndex == index)
                    _state = MasterState.DrawInspection;
            }

            RefreshInspectionRoiSlots();
            RedrawAllRois();

            if (!wasActive)
            {
                ApplyInspectionInteractionPolicy("remove-inactive");
                UpdateRoiHud();
            }
        }

        private void SetRoiAdornersVisible(string? roiId, bool visible)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return;
            }

            var shape = FindRoiShapeById(roiId);
            if (shape == null)
            {
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
            {
                return;
            }

            if (visible)
            {
                if (shape.Tag is RoiModel roi && ShouldEnableRoiEditing(roi.Role, roi) && !roi.IsFrozen)
                {
                    AttachRoiAdorner(shape);
                }
            }
            else
            {
                RemoveRoiAdorners(shape);
            }
        }

        private void SetRoiInteractive(string? roiId, bool interactive)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return;
            }

            var shape = FindRoiShapeById(roiId);
            if (shape == null)
            {
                return;
            }

            shape.IsHitTestVisible = interactive;
        }

        private Shape? FindRoiShapeById(string roiId)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return null;
            }

            return _roiShapesById.TryGetValue(roiId, out var shape) ? shape : null;
        }

        private Shape? EnsureShapeForRoi(RoiModel? roi)
        {
            if (roi == null || string.IsNullOrWhiteSpace(roi.Id) || CanvasROI == null)
            {
                return null;
            }

            if (_roiShapesById.TryGetValue(roi.Id, out var existing) && existing != null)
            {
                return existing;
            }

            RedrawOverlaySafe();

            if (_roiShapesById.TryGetValue(roi.Id, out existing) && existing != null)
            {
                return existing;
            }

            var shape = CreateLayoutShape(roi);
            if (shape != null)
            {
                CanvasROI.Children.Add(shape);
                _roiShapesById[roi.Id] = shape;
            }

            return shape;
        }

        private void AttachAdornerForRoi(RoiModel? roi)
        {
            var shape = EnsureShapeForRoi(roi);
            if (shape != null)
            {
                AttachRoiAdorner(shape);
            }
        }

        private void RemoveAdornersForRoi(RoiModel? roi)
        {
            if (roi == null || string.IsNullOrWhiteSpace(roi.Id))
            {
                return;
            }

            var shape = FindRoiShapeById(roi.Id);
            if (shape != null)
            {
                RemoveRoiAdorners(shape);
            }
        }

        private void CancelMasterEditing(bool redraw = true)
        {
            bool changed = false;

            if (_editingM1)
            {
                _editingM1 = false;
                _activeMaster1Role = null;
                if (BtnEditM1 != null)
                {
                    BtnEditM1.Content = "Edit Master 1";
                }
                SetMasterFrozen(1, true);
                RemoveAdornersForMaster(1);
                changed = true;
            }

            if (_editingM2)
            {
                _editingM2 = false;
                _activeMaster2Role = null;
                if (BtnEditM2 != null)
                {
                    BtnEditM2.Content = "Edit Master 2";
                }
                SetMasterFrozen(2, true);
                RemoveAdornersForMaster(2);
                changed = true;
            }

            if (changed && redraw)
            {
                RedrawOverlaySafe();
            }

            if (changed)
            {
                _editModeActive = false;
            }
        }

        private int CountRoiAdornersForShape(Shape shape)
        {
            if (shape == null)
            {
                return 0;
            }

            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null)
            {
                return 0;
            }

            var adorners = layer.GetAdorners(shape);
            if (adorners == null)
            {
                return 0;
            }

            return adorners.OfType<RoiAdorner>().Count();
        }

        private int CountActiveInspectionAdorners()
        {
            if (CanvasROI == null)
            {
                return 0;
            }

            int total = 0;
            foreach (var shape in CanvasROI.Children.OfType<Shape>())
            {
                if (shape?.Tag is RoiModel roi && roi.Role == RoiRole.Inspection)
                {
                    total += CountRoiAdornersForShape(shape);
                }
            }

            return total;
        }

        private void ApplyInspectionInteractionPolicy(string reason)
        {
            try
            {
                int activeIndex = Math.Max(1, Math.Min(4, _activeInspectionIndex));
                int removed = 0;
                bool attached = false;
                string? activeId = _globalUnlocked ? _activeEditableRoiId : null;

                for (int index = 1; index <= 4; index++)
                {
                    var roi = GetInspectionSlotModel(index);
                    if (roi == null)
                    {
                        continue;
                    }

                    bool matchesId = !string.IsNullOrWhiteSpace(activeId)
                        && !string.IsNullOrWhiteSpace(roi.Id)
                        && string.Equals(roi.Id, activeId, StringComparison.OrdinalIgnoreCase);
                    bool interactive = matchesId && index == activeIndex;

                    roi.IsFrozen = !(_globalUnlocked && interactive);

                    if (string.IsNullOrWhiteSpace(roi.Id))
                    {
                        continue;
                    }

                    var shape = FindRoiShapeById(roi.Id);
                    if (shape == null)
                    {
                        continue;
                    }

                    shape.IsHitTestVisible = interactive;

                    if (interactive)
                    {
                        AttachRoiAdorner(shape);
                        attached = true;
                    }
                    else
                    {
                        removed += CountRoiAdornersForShape(shape);
                        RemoveRoiAdorners(shape);
                    }
                }

                int adornerCount = CountActiveInspectionAdorners();
                string suffix = attached ? " attached" : string.Empty;
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    AppendLog($"[adorner] policy reason={reason} active={activeIndex} unlocked={_globalUnlocked} id={(activeId ?? "<none>")} count={adornerCount} removed={removed}{suffix}");
                }
                else
                {
                    AppendLog($"[adorner] policy active={activeIndex} unlocked={_globalUnlocked} id={(activeId ?? "<none>")} count={adornerCount} removed={removed}{suffix}");
                }

                if (adornerCount > 1)
                {
                    AppendLog($"[adorner] WARN multiple inspection adorners active -> {adornerCount}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[adorner] policy error: {ex.Message}");
            }
        }

        private void WorkflowViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.IsImageLoaded), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsImageLoaded));
            }
            else if (string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.SelectedInspectionRoi), StringComparison.Ordinal)
                     || string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.SelectedInspectionShape), StringComparison.Ordinal))
            {
                Dispatcher.Invoke(() =>
                {
                    if (!_updatingActiveInspection && ViewModel?.SelectedInspectionRoi != null)
                    {
                        SetActiveInspectionIndex(ViewModel.SelectedInspectionRoi.Index);
                    }

                    SyncDrawToolFromViewModel();
                });
            }
            else if (string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.BatchHeatmapSource), StringComparison.Ordinal)
                     || string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.BatchImageSource), StringComparison.Ordinal)
                     || string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.ActiveInspectionRoiImageRectPx), StringComparison.Ordinal)
                     || string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.CurrentRowIndex), StringComparison.Ordinal)
                     || string.Equals(e.PropertyName, nameof(Workflow.WorkflowViewModel.BatchRowOk), StringComparison.Ordinal))
            {
                RequestBatchHeatmapPlacement($"vm-global:{e.PropertyName}", ViewModel); // CODEX: ensure global VM changes still capture the ROI index at schedule time.
            }
        }

        private void WorkflowViewModelOnBatchStarted(object? sender, EventArgs e)
        {
            _suspendManualOverlayInvalidations = true;
            _sharedHeatmapGuardLogged = false;
        }

        private void WorkflowViewModelOnBatchEnded(object? sender, EventArgs e)
        {
            _suspendManualOverlayInvalidations = false;
        }

        private void WorkflowViewModelOnOverlayVisibilityChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                RequestRoiVisibilityRefresh();
                UpdateRoiHud();
            });
        }

        private void OnPresetLoaded()
        {
            TryCollapseMasterEditors();
            GoToInspectionTab();
        }

        private void OnLayoutLoaded()
        {
            TryCollapseMasterEditors();
            GoToInspectionTab();
        }

        private void TryCollapseMasterEditors()
        {
            if (Master1EditorGroup != null)
            {
                Master1EditorGroup.Visibility = Visibility.Collapsed;
            }

        }

        private void GoToInspectionTab()
        {
            if (TabSetupInspect != null)
            {
                TabSetupInspect.IsEnabled = true;
            }

            if (MainTabs != null && TabSetupInspect != null)
            {
                MainTabs.SelectedItem = TabSetupInspect;
            }
        }

        private void RedrawAllRois()
        {
            try
            {
                RedrawOverlaySafe();
                RequestRoiVisibilityRefresh();
                UpdateRoiHud();
                RedrawAnalysisCrosses();
            }
            catch
            {
                // no-op
            }
        }
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            GuiLog.Info($"[BOOT] MainWindow_Loaded ENTER"); // CODEX: string interpolation compatibility.

            if (_loadedOnce)
            {
                GuiLog.Info($"[BOOT] MainWindow_Loaded: ya estaba cargada (_loadedOnce == true) → RETURN"); // CODEX: string interpolation compatibility.
                return;
            }

            _loadedOnce = true;
            GuiLog.Info($"[BOOT] MainWindow_Loaded: _loadedOnce = true"); // CODEX: string interpolation compatibility.

            try
            {
                GuiLog.Info($"[BOOT] MainWindow_Loaded → AttachBatchUiHandlersOnce()"); // CODEX: string interpolation compatibility.
                AttachBatchUiHandlersOnce();
                GuiLog.Info($"[BOOT] MainWindow_Loaded → AttachBatchUiHandlersOnce() OK"); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[BOOT] MainWindow_Loaded: AttachBatchUiHandlersOnce FAILED", ex); // CODEX: string interpolation compatibility.
            }

            var tray = FindName("TopLeftTray") as ToolBarTray ?? FindVisualChildByName<ToolBarTray>(this, "TopLeftTray");
            if (tray != null)
            {
                GuiLog.Info($"[BOOT] MainWindow_Loaded → TopLeftTray encontrado, ajustando ZIndex y posición"); // CODEX: string interpolation compatibility.
                Panel.SetZIndex(tray, 1000);
                tray.Visibility = Visibility.Visible;

                if (VisualTreeHelper.GetParent(tray) is Canvas)
                {
                    Canvas.SetLeft(tray, 8);
                    Canvas.SetTop(tray, 8);
                }
            }
            else
            {
                GuiLog.Info($"[BOOT] MainWindow_Loaded → TopLeftTray NO encontrado"); // CODEX: string interpolation compatibility.
            }

            GuiLog.Info($"[BOOT] MainWindow_Loaded → ScheduleSyncOverlay(Loaded)"); // CODEX: string interpolation compatibility.
            ScheduleSyncOverlay(force: true, reason: "Loaded");
            GuiLog.Info($"[BOOT] MainWindow_Loaded → ScheduleSyncOverlay(Loaded) DONE"); // CODEX: string interpolation compatibility.

            UpdateHeatmapOverlayLayoutAndClip();
            GuiLog.Info($"[BOOT] MainWindow_Loaded → UpdateHeatmapOverlayLayoutAndClip() DONE"); // CODEX: string interpolation compatibility.

            RedrawAnalysisCrosses();
            GuiLog.Info($"[BOOT] MainWindow_Loaded → RedrawAnalysisCrosses() DONE"); // CODEX: string interpolation compatibility.

            WireExistingHeatmapControls();
            SyncDrawToolFromViewModel();

            try
            {
                if (_workflowViewModel != null)
                {
                    GuiLog.Info($"[BOOT] MainWindow_Loaded → Workflow.InitializeAsync BEGIN"); // CODEX: string interpolation compatibility.
                    await _workflowViewModel.InitializeAsync();
                    GuiLog.Info($"[BOOT] MainWindow_Loaded → Workflow.InitializeAsync END"); // CODEX: string interpolation compatibility.
                }
                else
                {
                    GuiLog.Info($"[BOOT] MainWindow_Loaded → _workflowViewModel es null (no se inicializa workflow)"); // CODEX: string interpolation compatibility.
                }
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[BOOT] MainWindow_Loaded: Workflow.InitializeAsync FAILED", ex); // CODEX: string interpolation compatibility.
            }

            try
            {
                GuiLog.Info($"[BOOT] MainWindow_Loaded → RefreshDatasetCommand"); // CODEX: string interpolation compatibility.
                _workflowViewModel?.RefreshDatasetCommand.Execute(null);
                GuiLog.Info($"[BOOT] MainWindow_Loaded → RefreshDatasetCommand DONE"); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[BOOT] MainWindow_Loaded: RefreshDatasetCommand FAILED", ex); // CODEX: string interpolation compatibility.
            }

            try
            {
                GuiLog.Info($"[BOOT] MainWindow_Loaded → RefreshHealthCommand"); // CODEX: string interpolation compatibility.
                _workflowViewModel?.RefreshHealthCommand.Execute(null);
                GuiLog.Info($"[BOOT] MainWindow_Loaded → RefreshHealthCommand DONE"); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[BOOT] MainWindow_Loaded: RefreshHealthCommand FAILED", ex); // CODEX: string interpolation compatibility.
            }

            InitComms();

            RefreshCreateButtonsEnabled();
            GuiLog.Info($"[BOOT] MainWindow_Loaded EXIT"); // CODEX: string interpolation compatibility.
        }

        private void BatchFile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is BatchRow row && File.Exists(row.FullPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    GuiLog.Warn($"[batch-ui] Failed to open '{row.FullPath}': {ex.Message}");
                }
            }
        }

        // === BATCH HEATMAP OVERLAY (Batch Inspection tab) ===
        #region BatchHeatmapOverlay
        private void BatchGrid_Loaded(object sender, RoutedEventArgs e)
        {
            AttachBatchUiHandlersOnce();
            UpdateBatchViewMetricsAndPlace();
        }

        private void BatchGrid_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _pendingBatchPlacementOp?.Abort(); // CODEX: reset pending placement when DataContext switches.
            _pendingBatchPlacementOp = null;
            _batchVmCached = null;
            _batchUiHandlersAttached = false;
            AttachBatchUiHandlersOnce();
            UpdateBatchViewMetricsAndPlace();
        }

        private void AttachBatchUiHandlersOnce()
        {
            if (_batchUiHandlersAttached)
            {
                return;
            }

            _batchVmCached = BatchGrid?.DataContext as WorkflowViewModel;
            var attachedVm = _batchVmCached;
            if (attachedVm == null)
            {
                GuiLog.Warn($"[batch-ui] AttachBatchUiHandlersOnce skipped: VM null"); // CODEX: guard wiring until VM exists. string interpolation compatibility.
                return;
            }

            attachedVm.OverlayCanvas ??= Overlay;
            attachedVm.OverlayBatchCaption ??= OverlayBatchCaption;

            void RequestPlaceForVm(string reason)
                => RequestBatchHeatmapPlacement(reason, attachedVm); // CODEX: capture VM + ROI index once per request.
            void RequestPlaceLive(string reason)
                => RequestBatchHeatmapPlacement(reason); // CODEX: reuse latest VM for UI-only events (size changes).

            if (attachedVm is INotifyPropertyChanged inpc)
            {
                inpc.PropertyChanged += (s, e) =>
                {
                    try
                    {
                        if (e.PropertyName == nameof(WorkflowViewModel.BatchHeatmapSource)
                            || e.PropertyName == nameof(WorkflowViewModel.BatchHeatmapRoiIndex)
                            || e.PropertyName == nameof(WorkflowViewModel.BatchImageSource)
                            || e.PropertyName == nameof(WorkflowViewModel.HeatmapCutoffPercent)
                            || e.PropertyName == nameof(WorkflowViewModel.BaseImageActualWidth)
                            || e.PropertyName == nameof(WorkflowViewModel.BaseImageActualHeight)
                            || e.PropertyName == nameof(WorkflowViewModel.CanvasRoiActualWidth)
                            || e.PropertyName == nameof(WorkflowViewModel.CanvasRoiActualHeight)
                            || e.PropertyName == nameof(WorkflowViewModel.BaseImagePixelWidth)
                            || e.PropertyName == nameof(WorkflowViewModel.BaseImagePixelHeight)
                            || e.PropertyName == nameof(WorkflowViewModel.UseCanvasPlacementForBatchHeatmap)
                            || e.PropertyName == nameof(WorkflowViewModel.CurrentRowIndex)
                            || e.PropertyName == nameof(WorkflowViewModel.BatchRowOk))
                        {
                            RequestPlaceForVm($"VM.{e.PropertyName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error($"[batch-ui] PropertyChanged handler failed", ex); // CODEX: string interpolation compatibility.
                    }
                };
            }

            if (!_batchSizeHandlersHooked)
            {
                if (BaseImage != null)
                {
                    BaseImage.SizeChanged += (_, __) => RequestPlaceLive("BaseImage.SizeChanged"); // CODEX: reschedule placement on resize.
                }

                if (Overlay != null)
                {
                    Overlay.SizeChanged += (_, __) => RequestPlaceLive("RoiCanvas.SizeChanged"); // CODEX: keep ROI canvas metrics in sync.
                }

                _batchSizeHandlersHooked = true;
            }

            _batchUiHandlersAttached = true;
            GuiLog.Info($"[batch-ui] AttachBatchUiHandlersOnce: handlers attached"); // CODEX: string interpolation compatibility.
            UpdateBatchViewMetricsAndPlace();
        }

        private void UpdateBatchViewMetricsAndPlace()
        {
            var vm = _batchVmCached ?? (BatchGrid?.DataContext as WorkflowViewModel);
            if (vm == null)
            {
                return;
            }

            if (BaseImage != null && Overlay != null && (BaseImage.ActualWidth <= 0 || BaseImage.ActualHeight <= 0 || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0))
            {
                _ = vm.WaitUntilMeasuredAsync(BaseImage, Overlay, CancellationToken.None);
            }

            UpdateBatchMetricsFromControls(vm);
            RequestBatchHeatmapPlacement("[ui] metrics-updated", vm); // CODEX: align metrics update with scheduled placement.
        }

        private void EnsureBatchInfoOverlay()
        {
            if (_batchInfoOverlay != null || Overlay == null)
            {
                return;
            }

            _batchInfoOverlay = new TextBlock
            {
                Name = "BatchInfoOverlay",
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(6, 2, 6, 2),
                Visibility = Visibility.Collapsed,
                Text = string.Empty
            };

            Overlay.Children.Add(_batchInfoOverlay);
            Panel.SetZIndex(_batchInfoOverlay, int.MaxValue);
            Canvas.SetLeft(_batchInfoOverlay, 8);
            Canvas.SetTop(_batchInfoOverlay, 8);
        }

        private void UpdateBatchInfoOverlay(WorkflowViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            if (Overlay == null)
            {
                return;
            }

            EnsureBatchInfoOverlay();
            if (_batchInfoOverlay == null)
            {
                return;
            }

            int rowIndex = vm.CurrentRowIndex;
            string name = Path.GetFileName(vm.CurrentImagePath ?? string.Empty) ?? string.Empty;
            bool hasContent = rowIndex > 0 || !string.IsNullOrEmpty(name);

            if (!hasContent)
            {
                _batchInfoOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            string status = vm.BatchRowOk.HasValue ? (vm.BatchRowOk.Value ? "OK" : "NG") : "…";
            string text = FormattableString.Invariant($"#{Math.Max(0, rowIndex):000}  {name}  — {status}");

            _batchInfoOverlay.Visibility = Visibility.Visible;
            if (!string.Equals(_batchInfoOverlay.Text, text, StringComparison.Ordinal))
            {
                _batchInfoOverlay.Text = text;
                AppendLog(FormattableString.Invariant($"[batch-ui:info] overlay='{text}' step={vm.BatchStepId}"));
            }
        }

        private void UpdateBatchMetricsFromControls(WorkflowViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            if (BaseImage?.Source is BitmapSource baseBitmap)
            {
                vm.BaseImagePixelWidth = baseBitmap.PixelWidth;
                vm.BaseImagePixelHeight = baseBitmap.PixelHeight;
            }
            else
            {
                vm.BaseImagePixelWidth = 0;
                vm.BaseImagePixelHeight = 0;
            }

            vm.BaseImageActualWidth = BaseImage?.ActualWidth ?? 0.0;
            vm.BaseImageActualHeight = BaseImage?.ActualHeight ?? 0.0;
            vm.CanvasRoiActualWidth = Overlay?.ActualWidth ?? 0.0;
            vm.CanvasRoiActualHeight = Overlay?.ActualHeight ?? 0.0;
        }

        private void TryPlaceBatchHeatmap(string reason, int roiIndex, WorkflowViewModel? vmOverride = null)
        {
            _pendingBatchPlacementOp = null; // CODEX: release pending op when the placement executes.
            try
            {
                var vm = vmOverride ?? _batchVmCached ?? (BatchGrid?.DataContext as WorkflowViewModel) ?? ViewModel;
                if (vm == null)
                {
                    GuiLog.Warn($"[batch-ui] TryPlaceBatchHeatmap: VM not found (reason={reason})");
                    return;
                }

                var fileName = System.IO.Path.GetFileName(vm.CurrentImagePath ?? string.Empty) ?? string.Empty;

                if (!vm.ShouldPlaceBatchPlacement(reason))
                {
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:skip-placed", roiIndex, null);
                    return;
                }

                UpdateBatchInfoOverlay(vm);

                if (vm.IsLayoutIo)
                {
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:io-busy", roiIndex, null);
                    ScheduleBatchHeatmapPlacement(vm, $"{reason}:retry-after-io");
                    return;
                }

                if (!vm.IsBatchAnchorReady && reason != null && reason.Contains("BatchHeatmapSource", StringComparison.Ordinal))
                {
                    GuiLog.Warn($"[batch-ui] skip placement (anchors not ready) step={vm.BatchStepId} file='{Path.GetFileName(vm.CurrentImagePath ?? string.Empty)}' reason={reason}");
                    _ = vm.CaptureCanvasIfNeededAsync(Path.GetFileName(vm.CurrentImagePath ?? string.Empty), "anchorNotReady");
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:anchor-wait", roiIndex, null);
                    return;
                }

                UpdateBatchMetricsFromControls(vm);

                var baseImage = BaseImage;
                var heatmap = HeatmapImage;
                var roiCanvas = Overlay; // CODEX: Overlay acts as the ROI canvas in the Batch tab.
                if (baseImage == null || heatmap == null || roiCanvas == null)
                {
                    GuiLog.Warn($"[batch-ui] TryPlaceBatchHeatmap: missing BaseImage/HeatmapImage/RoiCanvas"); // CODEX: string interpolation compatibility.
                    return;
                }

                if (roiIndex <= 0)
                {
                    heatmap.Source = null;
                    heatmap.Visibility = Visibility.Collapsed;
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:idx0", roiIndex, null);
                    return;
                }

                var roiConfig = vm.GetInspectionRoiConfig(roiIndex);
                if (roiConfig == null || !roiConfig.Enabled)
                {
                    heatmap.Source = null;
                    heatmap.Visibility = Visibility.Collapsed;
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:disabled", roiIndex, null);
                    return;
                }

                var src = vm.BatchHeatmapRoiIndex == roiIndex ? vm.BatchHeatmapSource : null;
                if (src == null)
                {
                    heatmap.Source = null;
                    heatmap.Visibility = Visibility.Collapsed;
                    if (vm.BatchHeatmapSource != null && vm.BatchHeatmapRoiIndex != roiIndex && roiIndex == 2 && !_sharedHeatmapGuardLogged)
                    {
                        GuiLog.Warn("[guard] idx=2 was sharing source with idx=1 (FIXED)");
                        _sharedHeatmapGuardLogged = true;
                    }

                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:no-src", roiIndex, null);
                    return; // CODEX: no heatmap available yet; skip scheduling await-size retries.
                }

                if (baseImage.ActualWidth <= 0 || baseImage.ActualHeight <= 0 || roiCanvas.ActualWidth <= 0 || roiCanvas.ActualHeight <= 0)
                {
                    ScheduleBatchHeatmapPlacement(vm, $"{reason}:await-size"); // CODEX: wait until layout system reports non-zero sizes, but only when a source exists.
                    return;
                }

                WRect? canvasRect = vm.GetInspectionRoiCanvasRect(roiIndex); // CODEX: capture explicit WPF rect type to avoid ambiguity.
                if (canvasRect == null)
                {
                    heatmap.Source = null;
                    heatmap.Visibility = Visibility.Collapsed;
                    GuiLog.Warn($"[batch-ui] TryPlaceBatchHeatmap: null canvasRect for idx={roiIndex}");
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:null-rect", roiIndex, null);
                    return;
                }

                heatmap.Source = src;
                heatmap.Width = Math.Max(1.0, canvasRect.Value.Width);
                heatmap.Height = Math.Max(1.0, canvasRect.Value.Height);
                Canvas.SetLeft(heatmap, canvasRect.Value.Left);
                Canvas.SetTop(heatmap, canvasRect.Value.Top);
                heatmap.Visibility = Visibility.Visible;

                WInt32Rect? rectImgOpt = vm.GetInspectionRoiImageRectPx(roiIndex); // CODEX: explicit Int32Rect for conversion helpers.
                string rectImgText = rectImgOpt.HasValue
                    ? $"{rectImgOpt.Value.X},{rectImgOpt.Value.Y},{rectImgOpt.Value.Width},{rectImgOpt.Value.Height}"
                    : "null";
                CvRect cvCanvasRect = ToCvRect(canvasRect.Value); // CODEX: convert canvas rect to OpenCV coordinates.
                CvRect? cvImageRect = rectImgOpt.HasValue ? ToCvRect(rectImgOpt.Value) : (CvRect?)null; // CODEX: convert ROI image rect for OpenCV consumers.
                string rectImgCvText = cvImageRect.HasValue // CODEX: track printable OpenCV rect description.
                    ? $"{cvImageRect.Value.X},{cvImageRect.Value.Y},{cvImageRect.Value.Width},{cvImageRect.Value.Height}" // CODEX: describe converted ROI rect.
                    : "null"; // CODEX: flag missing ROI rects during logging.

                GuiLog.Info($"[batch-ui] place idx={roiIndex} RECTimg=({rectImgText}) RECTcanvas=({canvasRect.Value.Left:0.##},{canvasRect.Value.Top:0.##},{canvasRect.Value.Width:0.##},{canvasRect.Value.Height:0.##}) CVimg=({rectImgCvText}) CVcanvas=({cvCanvasRect.X},{cvCanvasRect.Y},{cvCanvasRect.Width},{cvCanvasRect.Height}) reason={reason}"); // CODEX: log both WPF and OpenCV-friendly rectangles for diagnostics.

                double scaleX = rectImgOpt.HasValue && rectImgOpt.Value.Width > 0
                    ? canvasRect.Value.Width / rectImgOpt.Value.Width
                    : double.NaN;
                double scaleY = rectImgOpt.HasValue && rectImgOpt.Value.Height > 0
                    ? canvasRect.Value.Height / rectImgOpt.Value.Height
                    : double.NaN;

                FormattableString roiCanvasMessage =
                    $"[batch-ui] roi-canvas step={vm.BatchStepId} file='{fileName}' idx={roiIndex} imgRect=({rectImgText}) canvasRect=({canvasRect.Value.Left:0.##},{canvasRect.Value.Top:0.##},{canvasRect.Value.Width:0.##},{canvasRect.Value.Height:0.##}) scaleX={scaleX:0.####} scaleY={scaleY:0.####}";
                GuiLog.Info(roiCanvasMessage);

                if (vm.BatchRowOk.HasValue && !string.IsNullOrWhiteSpace(vm.CurrentImagePath))
                {
                    OverlayBatchCaption(vm.CurrentImagePath!, vm.BatchRowOk.Value);
                }

                if (roiIndex == 2)
                {
                    WInt32Rect? roi1Rect = vm.GetInspectionRoiImageRectPx(1); // CODEX: ensure consistent Int32Rect typing for ROI1.
                    if (roi1Rect.HasValue && rectImgOpt.HasValue)
                    {
                        if (roi1Rect.Value.Equals(rectImgOpt.Value))
                        {
                            GuiLog.Warn($"[batch-ui] guard: idx=2 comparte RECTimg con idx=1"); // CODEX: detect ROI2 using ROI1 geometry. string interpolation compatibility.
                        }
                        else if (Math.Abs(roi1Rect.Value.Width - rectImgOpt.Value.Width) > 0.5)
                        {
                            WRect? roi1Canvas = vm.GetInspectionRoiCanvasRect(1); // CODEX: explicit WPF rect typing for ROI1 canvas rect.
                            if (roi1Canvas.HasValue && RectsClose(canvasRect.Value, roi1Canvas.Value))
                            {
                                GuiLog.Warn($"[batch-ui] guard: idx=2 canvasRect≈ROI1 while widths differ (w1={roi1Rect.Value.Width:0.##} w2={rectImgOpt.Value.Width:0.##})");
                            }
                        }
                    }
                }

                vm.TraceBatchHeatmapPlacement($"ui:{reason}", roiIndex, canvasRect);
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[batch-ui] TryPlaceBatchHeatmap failed", ex); // CODEX: string interpolation compatibility.
            }
        }

        private static bool RectsClose(WRect a, WRect b, double tolerance = 0.5) // CODEX: disambiguate Rect usage by forcing WPF alias.
        {
            // CODEX: helper to spot ROI mixing by comparing canvas rectangles with a tight tolerance.
            return Math.Abs(a.Left - b.Left) <= tolerance
                && Math.Abs(a.Top - b.Top) <= tolerance
                && Math.Abs(a.Width - b.Width) <= tolerance
                && Math.Abs(a.Height - b.Height) <= tolerance;
        }

        private static CvRect ToCvRect(WRect rect) // CODEX: convert double-based WPF rects into integer OpenCV rects.
            => new CvRect((int)Math.Round(rect.X), (int)Math.Round(rect.Y), Math.Max(0, (int)Math.Round(rect.Width)), Math.Max(0, (int)Math.Round(rect.Height))); // CODEX: round and clamp WPF coordinates to pixels.

        private static CvRect ToCvRect(WInt32Rect rect) // CODEX: map Int32Rect structures directly into OpenCV rects.
            => new CvRect(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height)); // CODEX: guard width/height from going negative.

        private static CvRect ClampToMat(CvRect rect, Mat mat) // CODEX: ensure OpenCV rectangles stay inside Mat dimensions.
        {
            int x = Math.Clamp(rect.X, 0, Math.Max(0, mat.Width - 1)); // CODEX: clamp left coordinate within texture bounds.
            int y = Math.Clamp(rect.Y, 0, Math.Max(0, mat.Height - 1)); // CODEX: clamp top coordinate within texture bounds.
            int w = Math.Clamp(rect.Width, 0, Math.Max(0, mat.Width - x)); // CODEX: adjust width so rectangle fits inside the Mat.
            int h = Math.Clamp(rect.Height, 0, Math.Max(0, mat.Height - y)); // CODEX: adjust height so rectangle fits inside the Mat.
            return new CvRect(x, y, w, h); // CODEX: return the bounded rectangle.
        }

        private void OverlayBatchCaption(string fileName, bool isOk)
        {
            var tb = _batchCaption ??= new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 3, Color = Colors.Black, Opacity = 0.8 }
            };

            tb.Text = $"{System.IO.Path.GetFileName(fileName)}  —  {(isOk ? "OK" : "NG")}";

            if (Overlay != null && !Overlay.Children.Contains(tb))
            {
                Overlay.Children.Add(tb);
            }

            Canvas.SetLeft(tb, 8);
            Canvas.SetTop(tb, 8);
        }

        private void RequestBatchHeatmapPlacement(string reason, WorkflowViewModel? vmOverride = null)
        {
            var vm = vmOverride ?? _batchVmCached ?? (BatchGrid?.DataContext as WorkflowViewModel) ?? ViewModel;
            if (vm == null)
            {
                return; // CODEX: nothing to schedule if VM is absent.
            }

            ScheduleBatchHeatmapPlacement(vm, reason);
        }

        private void ScheduleBatchHeatmapPlacement(WorkflowViewModel vm, string reason)
        {
            // CODEX: capture ROI index immediately to avoid late reads of SelectedInspectionRoi.
            int roiIndex = Math.Max(0, vm.BatchHeatmapRoiIndex);
            long stepId = vm.BatchStepId;

            try
            {
                _pendingBatchPlacementOp?.Abort();
            }
            catch (Exception ex)
            {
                GuiLog.Warn($"[batch-ui] pending placement abort failed: {ex.Message}");
            }

            _pendingBatchPlacementOp = Dispatcher.InvokeAsync(() =>
            {
                if (vm.BatchStepId != stepId)
                {
                    vm.TraceBatchHeatmapPlacement($"ui:{reason}:skip-stale stepWas={stepId} now={vm.BatchStepId}", roiIndex, null);
                    return;
                }

                TryPlaceBatchHeatmap(reason, roiIndex, vm);
            }, DispatcherPriority.Background);
        }
        #endregion

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AppendResizeLog($"[window] SizeChanged: window={ActualWidth:0}x{ActualHeight:0} ImgMain={ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0}");
            ScheduleSyncOverlay(force: false, reason: "Window.SizeChanged");
            RoiOverlay?.InvalidateVisual();
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();
        }

        private void ImgMain_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            AppendResizeLog($"[image] SizeChanged: ImgMain={ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0}");
            ScheduleSyncOverlay(force: true, reason: "ImgMain.SizeChanged");
            UpdateHeatmapOverlayLayoutAndClip();
            RedrawAnalysisCrosses();
        }


        private static RoiShape ReadShapeFromToggle(ToggleButton toggle)
        {
            var tag = toggle.Tag?.ToString();
            if (!string.IsNullOrWhiteSpace(tag)
                && Enum.TryParse(tag, true, out RoiShape parsed))
            {
                return parsed;
            }

            return RoiShape.Rectangle;
        }

        private void ApplyDrawToolSelection(RoiShape shape, bool updateViewModel)
        {
            _currentDrawTool = shape;

            _updatingDrawToolUi = true;
            try
            {
                var rectBtn = FindName("RectToolButton") as ToggleButton;
                var circBtn = FindName("CircleToolButton") as ToggleButton;
                var annBtn = FindName("AnnulusToolButton") as ToggleButton;

                if (rectBtn != null)
                {
                    rectBtn.IsChecked = shape == RoiShape.Rectangle;
                }

                if (circBtn != null)
                {
                    circBtn.IsChecked = shape == RoiShape.Circle;
                }

                if (annBtn != null)
                {
                    annBtn.IsChecked = shape == RoiShape.Annulus;
                }
            }
            finally
            {
                _updatingDrawToolUi = false;
            }

            if (updateViewModel && ViewModel?.SelectedInspectionRoi != null
                && ViewModel.SelectedInspectionRoi.Shape != shape)
            {
                ViewModel.SelectedInspectionRoi.Shape = shape;
            }
        }

        private void SyncDrawToolFromViewModel()
        {
            if (ViewModel?.SelectedInspectionRoi != null)
            {
                ApplyDrawToolSelection(ViewModel.SelectedInspectionRoi.Shape, updateViewModel: false);
            }
        }

        private void DrawToolToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingDrawToolUi)
            {
                return;
            }

            if (sender is ToggleButton toggle)
            {
                var shape = ReadShapeFromToggle(toggle);
                ApplyDrawToolSelection(shape, updateViewModel: true);
            }
        }

        private void DrawToolToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_updatingDrawToolUi)
            {
                return;
            }

            if (sender is ToggleButton toggle)
            {
                var shape = ReadShapeFromToggle(toggle);
                if (shape == _currentDrawTool)
                {
                    _updatingDrawToolUi = true;
                    try
                    {
                        toggle.IsChecked = true;
                    }
                    finally
                    {
                        _updatingDrawToolUi = false;
                    }
                }
            }
        }

        private void InspectionShape_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingDrawToolUi)
            {
                return;
            }

            if (sender is ComboBox combo && combo.SelectedItem is RoiShape shape)
            {
                InspectionRoiConfig? config = null;

                if (combo.Tag is string tag &&
                    int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    config = GetInspectionConfigByIndex(index);
                    if (config != null && config.Shape != shape)
                    {
                        config.Shape = shape;
                    }
                }

                if (ReferenceEquals(config, ViewModel?.SelectedInspectionRoi) || config == null)
                {
                    ApplyDrawToolSelection(shape, updateViewModel: false);
                }
            }
        }

        private void BtnClearRoi_Click(object sender, RoutedEventArgs e)
        {
            var previousState = _state;
            var cleared = TryClearCurrentStatePersistedRoi(out var clearedRole);

            if (cleared)
            {
                ClearPreview();
                RedrawOverlaySafe();
                UpdateWizardState();
                Snack($"ROI eliminado. Dibuja un ROI válido antes de guardar."); // CODEX: string interpolation compatibility.
                AppendLog($"[wizard] ROI cleared via toolbar (prevState={previousState}, role={clearedRole})");
            }
            else
            {
                Snack($"No hay ROI que eliminar."); // CODEX: string interpolation compatibility.
            }
        }

        private RoiShape ReadCurrentInspectionShape()
        {
            return _currentDrawTool;
        }

        private RoiShape ReadInspectionShapeForIndex(int index)
        {
            ComboBox? combo = index switch
            {
                1 => CmbShapeInspection1,
                2 => CmbShapeInspection2,
                3 => CmbShapeInspection3,
                4 => CmbShapeInspection4,
                _ => null
            };

            if (combo?.SelectedItem is RoiShape shape)
            {
                return shape;
            }

            var config = GetInspectionConfigByIndex(index);
            if (config != null)
            {
                return config.Shape;
            }

            return ReadCurrentInspectionShape();
        }

        // ====== Ratón & dibujo ======
        private RoiShape ReadShapeForCurrentStep()
        {
            string ToLower(object? x)
            {
                if (x is ComboBoxItem comboItem)
                {
                    if (comboItem.Tag is RoiShape taggedShape)
                    {
                        return taggedShape.ToString().ToLowerInvariant();
                    }

                    var content = comboItem.Content?.ToString();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content.ToLowerInvariant();
                    }
                }

                return (x?.ToString() ?? string.Empty).ToLowerInvariant();
            }

            if (_state == MasterState.DrawM1_Pattern || _state == MasterState.DrawM1_Search)
            {
                return ReadShapeFrom(ComboMasterRoiShape);
            }

            if (_state == MasterState.DrawM2_Pattern || _state == MasterState.DrawM2_Search)
            {
                return ReadShapeFrom(ComboM2Shape);
            }

            if (_state == MasterState.DrawInspection || _state == MasterState.Ready)
            {
                return ReadCurrentInspectionShape();
            }

            var shapeText = ToLower(GetInspectionShapeFromModel());
            if (shapeText.Contains("annulus")) return RoiShape.Annulus;
            if (shapeText.Contains("círculo") || shapeText.Contains("circulo") || shapeText.Contains("circle")) return RoiShape.Circle;
            return RoiShape.Rectangle;
        }

        private void BeginDraw(RoiShape shape, SWPoint p0)
        {
            // Si había un preview anterior, elimínalo para evitar capas huérfanas
            ClearPreview();

            _previewShape = shape switch
            {
                RoiShape.Rectangle => new WRectShape
                {
                    Stroke = WBrushes.Cyan,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(40, 0, 255, 255))
                },
                RoiShape.Circle => new WEllipse
                {
                    Stroke = WBrushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(30, 0, 255, 0))
                },
                RoiShape.Annulus => new AnnulusShape
                {
                    Stroke = WBrushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(WColor.FromArgb(30, 0, 255, 0))
                },
                _ => null
            };

            if (_previewShape != null)
            {
                Canvas.SetLeft(_previewShape, p0.X);
                Canvas.SetTop(_previewShape, p0.Y);
                _previewShape.Width = 0;
                _previewShape.Height = 0;
                Panel.SetZIndex(_previewShape, 20);
                CanvasROI.Children.Add(_previewShape);

                string lbl;
                RoiModel previewModel;
                try
                {
                    var role = GetCurrentStateRole() ?? RoiRole.Inspection;
                    previewModel = new RoiModel { Shape = shape, Role = role };
                    lbl = ResolveRoiLabelText(previewModel) ?? "ROI";
                    previewModel.Label = lbl;
                }
                catch
                {
                    lbl = "ROI";
                    previewModel = new RoiModel { Shape = shape, Role = GetCurrentStateRole() ?? RoiRole.Inspection, Label = lbl };
                }

                string labelName = "roiLabel_" + lbl.Replace(" ", "_");
                var existingLabel = CanvasROI.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(t => t.Name == labelName);
                FrameworkElement label;
                if (existingLabel == null)
                {
                    label = CreateStyledLabel(lbl);
                    label.Name = labelName;
                    label.IsHitTestVisible = false;
                    CanvasROI.Children.Add(label);
                    Panel.SetZIndex(label, int.MaxValue);
                }
                else
                {
                    label = existingLabel;
                    label.IsHitTestVisible = false;
                    if (label is System.Windows.Controls.Border border && border.Child is System.Windows.Controls.TextBlock tbChild)
                    {
                        tbChild.Text = lbl;
                    }
                }

                _previewShape.Tag = previewModel;
                UpdateRoiLabelPosition(_previewShape);
            }
        }

        private bool ShouldLogAnnulusValue(ref double lastValue, double newValue)
        {
            if (double.IsNaN(lastValue) || Math.Abs(lastValue - newValue) >= AnnulusLogThreshold)
            {
                lastValue = newValue;
                return true;
            }

            return false;
        }

        private bool ShouldLogAnnulusInner(double proposedInner, double finalInner)
        {
            bool shouldLog = false;

            if (double.IsNaN(_lastLoggedAnnulusInnerProposed) || Math.Abs(proposedInner - _lastLoggedAnnulusInnerProposed) >= AnnulusLogThreshold)
            {
                shouldLog = true;
            }

            if (double.IsNaN(_lastLoggedAnnulusInnerFinal) || Math.Abs(finalInner - _lastLoggedAnnulusInnerFinal) >= AnnulusLogThreshold)
            {
                shouldLog = true;
            }

            if (shouldLog)
            {
                _lastLoggedAnnulusInnerProposed = proposedInner;
                _lastLoggedAnnulusInnerFinal = finalInner;
            }

            return shouldLog;
        }

        private void UpdateDraw(RoiShape shape, System.Windows.Point p0, System.Windows.Point p1)
        {
            if (_previewShape == null) return;

            if (shape == RoiShape.Rectangle)
            {
                var x = Math.Min(p0.X, p1.X);
                var y = Math.Min(p0.Y, p1.Y);
                var w = Math.Abs(p1.X - p0.X);
                var h = Math.Abs(p1.Y - p0.Y);

                Canvas.SetLeft(_previewShape, x);
                Canvas.SetTop(_previewShape, y);
                _previewShape.Width = w;
                _previewShape.Height = h;
            }
            else
            {
                // === Círculo / Annulus ===
                // Mantén el mismo sistema de coordenadas que el modelo/adorners:
                // usa radio = max(|dx|, |dy|) (norma L∞), no la distancia euclídea.
                var dx = p1.X - p0.X;
                var dy = p1.Y - p0.Y;

                double radius = Math.Max(Math.Abs(dx), Math.Abs(dy));

                // Evita que el preview se "vaya" fuera del canvas mientras dibujas
                radius = ClampRadiusToCanvasBounds(p0, radius);

                var diameter = radius * 2.0;
                var left = p0.X - radius;
                var top = p0.Y - radius;

                Canvas.SetLeft(_previewShape, left);
                Canvas.SetTop(_previewShape, top);
                _previewShape.Width = diameter;
                _previewShape.Height = diameter;

                if (shape == RoiShape.Annulus && _previewShape is AnnulusShape annulus)
                {
                    // Outer radius = radius (canvas)
                    var outer = radius;
                    if (ShouldLogAnnulusValue(ref _lastLoggedAnnulusOuterRadius, outer))
                        AppendLog($"[annulus] outer radius preview={outer:0.##} px");

                    // Conserva proporción si el usuario ya la ha cambiado; si no, usa el default & clamp.
                    double proposedInner = annulus.InnerRadius;
                    double resolvedInner = AnnulusDefaults.ResolveInnerRadius(proposedInner, outer);
                    double finalInner = AnnulusDefaults.ClampInnerRadius(resolvedInner, outer);

                    if (ShouldLogAnnulusInner(proposedInner, finalInner))
                        AppendLog($"[annulus] outer={outer:0.##} px, proposed inner={proposedInner:0.##} px -> final inner={finalInner:0.##} px");

                    annulus.InnerRadius = finalInner;
                }
            }

            UpdateRoiLabelPosition(_previewShape);
        }

        private double ClampRadiusToCanvasBounds(System.Windows.Point center, double desiredRadius)
        {
            if (CanvasROI == null) return desiredRadius;

            double cw = CanvasROI.ActualWidth;
            double ch = CanvasROI.ActualHeight;
            if (cw <= 0 || ch <= 0) return desiredRadius;

            double maxLeft = center.X;
            double maxRight = cw - center.X;
            double maxUp = center.Y;
            double maxDown = ch - center.Y;

            double maxRadius = Math.Max(0.0, Math.Min(Math.Min(maxLeft, maxRight), Math.Min(maxUp, maxDown)));
            return Math.Min(desiredRadius, maxRadius);
        }

        private void HookCanvasInput()
        {
            // Escuchamos SIEMPRE, aunque otro control marque Handled=true
            CanvasROI.AddHandler(UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(Canvas_MouseLeftButtonDownEx), true);
            CanvasROI.AddHandler(UIElement.MouseLeftButtonUpEvent,
                new MouseButtonEventHandler(Canvas_MouseLeftButtonUpEx), true);
            CanvasROI.AddHandler(UIElement.MouseMoveEvent,
                new MouseEventHandler(Canvas_MouseMoveEx), true);
        }

        private void Canvas_MouseLeftButtonDownEx(object sender, MouseButtonEventArgs e)
        {
            if (CanvasROI == null)
            {
                return;
            }

            if (!_editModeActive)
            {
                e.Handled = true;
                return;
            }

            var pos = e.GetPosition(CanvasROI);
            var fe = e.OriginalSource as FrameworkElement;
            var tagType = fe?.Tag?.GetType().Name ?? "null";
            AppendLog($"[mouse-down] srcType={fe?.GetType().Name ?? "null"} tagType={tagType} handled={e.Handled} pos=({pos.X:F1},{pos.Y:F1})");

            var over = System.Windows.Input.Mouse.DirectlyOver;
            var handledBefore = e.Handled;
            AppendLog($"[canvas+] Down HB={handledBefore} src={e.OriginalSource?.GetType().Name}, over={over?.GetType().Name}");

            // ⛑️ No permitir interacción si el overlay no está alineado aún
            if (!IsOverlayAligned())
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[guard] overlay no alineado todavía → reprogramo sync y cancelo este click");
                ScheduleSyncOverlay(force: true, reason: "CanvasGuard");
                e.Handled = true;
                return;
            }

            // 1) Thumb → lo gestiona el adorner
            if (over is System.Windows.Controls.Primitives.Thumb)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[canvas+] Down ignorado (Thumb debajo) -> Adorner manejará");
                return;
            }

            // 2) Arrastre de ROI existente
            var roiElement = FindRoiElementFromSource(e.OriginalSource);
            if (roiElement is Shape hitShape)
            {
                StartRoiDrag(hitShape, pos, e);
                return;
            }

            // 3) Dibujo nuevo ROI en canvas vacío
            if (e.OriginalSource is Canvas)
            {
                if (_editingM1 || _editingM2)
                {
                    AppendLog("[canvas+] Down ignorado (modo edición master)");
                    e.Handled = true;
                    return;
                }

                if (_globalUnlocked && !string.IsNullOrWhiteSpace(_activeEditableRoiId))
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[canvas+] Down ignorado (modo edición activo)");
                    e.Handled = true;
                    return;
                }

                _isDrawing = true;
                _p0 = pos;
                _currentShape = ReadShapeForCurrentStep();
                BeginDraw(_currentShape, _p0);
                CanvasROI.CaptureMouse();
                AppendLog($"[mouse] Down @ {_p0.X:0},{_p0.Y:0} shape={_currentShape}");
                e.Handled = true;
                return;
            }

            AppendLog($"[canvas+] Down ignorado (src={e.OriginalSource?.GetType().Name})");
        }


        private void Canvas_MouseMoveEx(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_editModeActive)
            {
                return;
            }

            // ARRASTRE activo
            if (_dragShape != null && CanvasROI != null)
            {
                var p = e.GetPosition(CanvasROI);
                var dx = p.X - _dragStart.X;
                var dy = p.Y - _dragStart.Y;

                AppendLog($"[drag] move dx={dx:F1} dy={dy:F1} state={_state}");

                var nx = _dragOrigX + dx;
                var ny = _dragOrigY + dy;

                Canvas.SetLeft(_dragShape, nx);
                Canvas.SetTop(_dragShape, ny);
                UpdateRoiLabelPosition(_dragShape);

                // Sincroniza modelo y recoloca los thumbs del adorner
                SyncModelFromShape(_dragShape);
                InvalidateAdornerFor(_dragShape);

                AppendLog($"[drag] move dx={dx:0.##} dy={dy:0.##} -> pos=({nx:0.##},{ny:0.##})");
                return;
            }

            // DIBUJO activo
            if (_isDrawing)
            {
                var p1 = e.GetPosition(CanvasROI);
                UpdateDraw(_currentShape, _p0, p1);
            }
        }

        private void Canvas_MouseLeftButtonUpEx(object sender, MouseButtonEventArgs e)
        {
            if (!_editModeActive)
            {
                return;
            }

            var over = System.Windows.Input.Mouse.DirectlyOver;
            var handledBefore = e.Handled;
            AppendLog($"[canvas+] Up   HB={handledBefore} src={e.OriginalSource?.GetType().Name}, over={over?.GetType().Name}");

            // FIN ARRASTRE
            if (_dragShape != null && CanvasROI != null)
            {
                var left = Canvas.GetLeft(_dragShape);
                var top = Canvas.GetTop(_dragShape);
                AppendLog($"[drag] end finalPos=({left:F1},{top:F1}) state={_state}");
                CanvasROI.ReleaseMouseCapture();
                _dragShape = null;
                TryAutosaveLayout("drag end");
                e.Handled = true;
                return;
            }

            // FIN DIBUJO
            if (_isDrawing)
            {
                if (_editingM1 || _editingM2)
                {
                    _isDrawing = false;
                    CanvasROI.ReleaseMouseCapture();
                    AppendLog("[mouse] Up ignorado (modo edición master)");
                    e.Handled = true;
                    return;
                }

                _isDrawing = false;
                var p1 = e.GetPosition(CanvasROI);
                EndDraw(_currentShape, _p0, p1);
                CanvasROI.ReleaseMouseCapture();
                AppendLog($"[mouse] Up   @ {p1.X:0},{p1.Y:0}");
                TryAutosaveLayout("draw end");
                e.Handled = true;
                return;
            }

            AppendLog($"[mouse-up] no drag; isDrawing={_isDrawing} state={_state}");
        }

        private void StartRoiDrag(Shape roiElement, System.Windows.Point posCanvas, MouseButtonEventArgs e)
        {
            if (CanvasROI == null)
            {
                return;
            }

            if (roiElement.Tag is RoiModel frozenRoi && frozenRoi.IsFrozen)
            {
                e.Handled = true;
                return;
            }

            _dragShape = roiElement;
            _dragStart = posCanvas;
            _dragOrigX = Canvas.GetLeft(roiElement);
            _dragOrigY = Canvas.GetTop(roiElement);
            if (double.IsNaN(_dragOrigX)) _dragOrigX = 0;
            if (double.IsNaN(_dragOrigY)) _dragOrigY = 0;

            CanvasROI.CaptureMouse();
            AppendLog($"[drag] start elemType={roiElement.GetType().Name} pos=({_dragOrigX:F1},{_dragOrigY:F1}) state={_state}");
            e.Handled = true;
        }

        private FrameworkElement? FindRoiElementFromSource(object? original)
        {
            if (original is not DependencyObject dep)
            {
                return null;
            }

            while (dep != null)
            {
                if (dep is FrameworkElement fe)
                {
                    if (fe.Tag is RoiModel || fe.DataContext is RoiModel)
                    {
                        if (fe is Shape)
                        {
                            return fe;
                        }

                        // Busca un ancestro que sea Shape para moverlo.
                        var ancestor = VisualTreeHelper.GetParent(fe);
                        while (ancestor != null)
                        {
                            if (ancestor is Shape ancestorShape && (ancestorShape.Tag is RoiModel || ancestorShape.DataContext is RoiModel))
                            {
                                return ancestorShape;
                            }

                            ancestor = VisualTreeHelper.GetParent(ancestor);
                        }

                        return fe;
                    }
                }

                dep = VisualTreeHelper.GetParent(dep);
            }

            return null;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e != null && e.Key == Key.Escape && !string.IsNullOrWhiteSpace(_activeEditableRoiId))
            {
                ExitEditMode("esc");
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private void EndDraw(RoiShape shape, System.Windows.Point p0, System.Windows.Point p1)
        {
            if (_previewShape == null) return;

            string? previewLabel = null;
            if (_previewShape.Tag is RoiModel existingTag && !string.IsNullOrWhiteSpace(existingTag.Label))
            {
                previewLabel = existingTag.Label;
            }

            RoiModel canvasDraft;
            if (shape == RoiShape.Rectangle)
            {
                var x = Canvas.GetLeft(_previewShape);
                var y = Canvas.GetTop(_previewShape);
                var w = _previewShape.Width;
                var h = _previewShape.Height;
                canvasDraft = new RoiModel { Shape = RoiShape.Rectangle, Width = w, Height = h };
                canvasDraft.Left = x;
                canvasDraft.Top = y;
            }
            else
            {
                var x = Canvas.GetLeft(_previewShape);
                var y = Canvas.GetTop(_previewShape);
                var w = _previewShape.Width;
                var r = w / 2.0;
                var cx = x + r; var cy = y + r;
                double innerRadius = 0;
                if (shape == RoiShape.Annulus)
                {
                    if (_previewShape is AnnulusShape annulus)
                        innerRadius = AnnulusDefaults.ResolveInnerRadius(annulus.InnerRadius, r);
                    else
                        innerRadius = AnnulusDefaults.ResolveInnerRadius(innerRadius, r);
                }

                canvasDraft = new RoiModel
                {
                    Shape = shape,
                    CX = cx,
                    CY = cy,
                    R = r,
                    RInner = innerRadius,
                    Width = w,
                    Height = _previewShape.Height
                };
                canvasDraft.Left = x;
                canvasDraft.Top = y;
            }

            if (!string.IsNullOrWhiteSpace(previewLabel))
            {
                canvasDraft.Label = previewLabel;
            }

            var pixelDraft = CanvasToImage(canvasDraft);
            if (!string.IsNullOrWhiteSpace(previewLabel) && pixelDraft != null)
            {
                pixelDraft.Label = previewLabel;
            }
            var activeRole = GetCurrentStateRole();
            _tmpBuffer = pixelDraft;
            if (activeRole.HasValue)
            {
                canvasDraft.Role = activeRole.Value;
                if (pixelDraft != null)
                    pixelDraft.Role = activeRole.Value;
                if (_tmpBuffer != null)
                    _tmpBuffer.Role = activeRole.Value;
            }
            AppendLog($"[draw] ROI draft = {DescribeRoi(_tmpBuffer)}");

            _previewShape.Tag = canvasDraft;
            ApplyRoiRotationToShape(_previewShape, canvasDraft.AngleDeg);
            UpdateRoiLabelPosition(_previewShape);
            if (_state == MasterState.DrawInspection)
            {
                if (_tmpBuffer != null)
                {
                    SyncCurrentRoiFromInspection(_tmpBuffer);
                }
            }
            else if (pixelDraft != null)
            {
                UpdateOverlayFromPixelModel(pixelDraft);
            }
            _previewShape.IsHitTestVisible = true; // el adorner coge los clics
            _previewShape.StrokeDashArray = new DoubleCollection { 4, 4 };

            var al = AdornerLayer.GetAdornerLayer(_previewShape);
            if (al != null)
            {
                var prev = al.GetAdorners(_previewShape);
                if (prev != null)
                {
                    foreach (var ad in prev.OfType<RoiAdorner>())
                        al.Remove(ad);
                }

                if (RoiOverlay == null)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[adorner] overlay no disponible para preview");
                    return;
                }

                var adorner = new RoiAdorner(_previewShape, RoiOverlay, (changeKind, modelUpdated) =>
                {
                    var pixelModel = CanvasToImage(modelUpdated);
                    _tmpBuffer = pixelModel.Clone();
                    if (_state == MasterState.DrawInspection && _tmpBuffer != null)
                    {
                        modelUpdated.Role = RoiRole.Inspection;
                        _tmpBuffer.Role = RoiRole.Inspection;
                        SyncCurrentRoiFromInspection(_tmpBuffer);
                    }
                    if (_tmpBuffer != null)
                    {
                        HandleAdornerChange(changeKind, modelUpdated, pixelModel, "[preview]");
                        UpdateRoiLabelPosition(_previewShape);
                    }
                }, AppendLog); // ⬅️ pasa logger

                al.Add(adorner);
                // CODEX: string interpolation compatibility.
                AppendLog($"[adorner] preview OK layer attach");
            }
            else
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[adorner] preview layer NOT FOUND (falta AdornerDecorator)");
            }
        }

        private string DescribeRoi(RoiModel? r)
        {
            if (r == null) return "<null>";
            return r.Shape switch
            {
                RoiShape.Rectangle => $"Rect x={r.X:0},y={r.Y:0},w={r.Width:0},h={r.Height:0},ang={r.AngleDeg:0.0}",
                RoiShape.Circle => $"Circ cx={r.CX:0},cy={r.CY:0},r={r.R:0},ang={r.AngleDeg:0.0}",
                RoiShape.Annulus => $"Ann cx={r.CX:0},cy={r.CY:0},r={r.R:0},ri={r.RInner:0},ang={r.AngleDeg:0.0}",
                _ => "?"
            };
        }

        private void HandleAdornerChange(RoiAdornerChangeKind changeKind, RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            switch (changeKind)
            {
                case RoiAdornerChangeKind.DragStarted:
                    _adornerHadDelta = false;
                    HandleDragStarted(canvasModel, pixelModel, contextLabel);
                    return;

                case RoiAdornerChangeKind.Delta:
                    _adornerHadDelta = true;
                    HandleDragDelta(canvasModel, pixelModel, contextLabel);
                    // Redibujamos solo cuando hay delta real
                    UpdateOverlayFromPixelModel(pixelModel);
                    return;

                case RoiAdornerChangeKind.DragCompleted:
                    HandleDragCompleted(canvasModel, pixelModel, contextLabel);
                    // Si no hubo delta (click sin mover), NO redibujamos → evita “salto”
                    if (_adornerHadDelta)
                        UpdateOverlayFromPixelModel(pixelModel);
                    _adornerHadDelta = false;
                    return;
            }
        }

        private void HandleDragStarted(RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            var state = GetOrCreateDragState(canvasModel);
            state.Buffer.Clear();
            state.LastSnapshot = CaptureSnapshot(canvasModel);
            state.HasSnapshot = true;

            AppendLog($"{contextLabel} drag start => {DescribeRoi(pixelModel)}");
        }

        private void HandleDragDelta(RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            var state = GetOrCreateDragState(canvasModel);
            var snapshot = CaptureSnapshot(canvasModel);

            if (!state.HasSnapshot)
            {
                state.LastSnapshot = snapshot;
                state.HasSnapshot = true;
            }

            if (!ShouldLogDelta(state, snapshot))
                return;

            state.Buffer.AppendLine($"{contextLabel} drag delta => {DescribeRoi(pixelModel)}");
            state.LastSnapshot = snapshot;
        }

        private void HandleDragCompleted(RoiModel canvasModel, RoiModel pixelModel, string contextLabel)
        {
            if (_dragLogStates.TryGetValue(canvasModel, out var state))
            {
                FlushDragBuffer(state);
                _dragLogStates.Remove(canvasModel);
            }

            AppendLog($"{contextLabel} drag end => {DescribeRoi(pixelModel)}");
        }

        private DragLogState GetOrCreateDragState(RoiModel model)
        {
            if (!_dragLogStates.TryGetValue(model, out var state))
            {
                state = new DragLogState();
                _dragLogStates[model] = state;
            }

            return state;
        }

        private static RoiSnapshot CaptureSnapshot(RoiModel model)
        {
            double x;
            double y;
            double width;
            double height;

            switch (model.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    {
                        double radius = model.R;
                        if (radius > 0)
                        {
                            width = radius * 2.0;
                            height = width;
                        }
                        else
                        {
                            width = model.Width;
                            height = model.Height;
                            if (width <= 0 && height > 0) width = height;
                            if (height <= 0 && width > 0) height = width;
                        }

                        x = model.CX - width / 2.0;
                        y = model.CY - height / 2.0;
                        break;
                    }
                case RoiShape.Rectangle:
                default:
                    x = model.X;
                    y = model.Y;
                    width = model.Width;
                    height = model.Height;
                    break;
            }

            return new RoiSnapshot(x, y, width, height, model.AngleDeg);
        }

        private bool ShouldLogDelta(DragLogState state, RoiSnapshot current)
        {
            if (!state.HasSnapshot)
                return true;

            var last = state.LastSnapshot;

            if (Math.Abs(current.X - last.X) >= DragLogMovementThreshold)
                return true;
            if (Math.Abs(current.Y - last.Y) >= DragLogMovementThreshold)
                return true;
            if (Math.Abs(current.Width - last.Width) >= DragLogMovementThreshold)
                return true;
            if (Math.Abs(current.Height - last.Height) >= DragLogMovementThreshold)
                return true;

            var angleDelta = Math.Abs(NormalizeAngleDifference(current.Angle - last.Angle));
            return angleDelta >= DragLogAngleThreshold;
        }

        private void FlushDragBuffer(DragLogState state)
        {
            if (state.Buffer.Length == 0)
                return;

            var snapshot = state.Buffer.ToString();
            state.Buffer.Clear();

            using var reader = new StringReader(snapshot);
            string? line;
            var lines = new List<string>();
            while ((line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            if (lines.Count > 0)
                AppendLogBulk(lines);
        }

        private static double NormalizeAngleDifference(double angleDeg)
        {
            angleDeg %= 360.0;
            if (angleDeg <= -180.0)
                angleDeg += 360.0;
            else if (angleDeg > 180.0)
                angleDeg -= 360.0;
            return angleDeg;
        }

        private sealed class DragLogState
        {
            public RoiSnapshot LastSnapshot;
            public bool HasSnapshot;
            public StringBuilder Buffer { get; } = new();
        }

        private readonly struct RoiSnapshot
        {
            public RoiSnapshot(double x, double y, double width, double height, double angle)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
                Angle = angle;
            }

            public double X { get; }
            public double Y { get; }
            public double Width { get; }
            public double Height { get; }
            public double Angle { get; }
        }

        // ====== Guardar pasos del wizard ======
        // ====== Guardar pasos del wizard ======
        private void BtnSaveMaster_Click(object sender, RoutedEventArgs e)
        {
            SaveMasterWizardFlow();
        }

        private MasterState ResolveStateForSave()
        {
            if (_editingM1 && _activeMaster1Role.HasValue)
            {
                return _activeMaster1Role.Value == RoiRole.Master1Pattern
                    ? MasterState.DrawM1_Pattern
                    : MasterState.DrawM1_Search;
            }

            if (_editingM2 && _activeMaster2Role.HasValue)
            {
                return _activeMaster2Role.Value == RoiRole.Master2Pattern
                    ? MasterState.DrawM2_Pattern
                    : MasterState.DrawM2_Search;
            }

            return _state;
        }

        private void SaveMasterWizardFlow()
        {

            var layoutPath = MasterLayoutManager.GetDefaultPath(_preset);
            var stateForSave = ResolveStateForSave();
            var originalState = _state;
            bool restoreStateAfterSave = (_editingM1 || _editingM2) && stateForSave != _state;

            if (_tmpBuffer is null)
            {
                _tmpBuffer = stateForSave switch
                {
                    MasterState.DrawM1_Pattern => _layout?.Master1Pattern?.Clone(),
                    MasterState.DrawM1_Search => _layout?.Master1Search?.Clone(),
                    MasterState.DrawM2_Pattern => _layout?.Master2Pattern?.Clone(),
                    MasterState.DrawM2_Search => _layout?.Master2Search?.Clone(),
                    MasterState.DrawInspection or MasterState.Ready => _layout?.Inspection?.Clone(),
                    _ => _tmpBuffer
                };
            }

            if (_tmpBuffer is null)
            {
                var previousState = stateForSave;
                var cleared = TryClearPersistedRoiForState(stateForSave, out var clearedRole);

                if (cleared)
                    AppendLog($"[wizard] cleared ROI state={previousState} role={clearedRole}");
                else
                    AppendLog($"[wizard] no ROI to clear state={previousState} role={clearedRole}");

                ClearPreview();
                RedrawOverlaySafe();
                UpdateWizardState();

                if (!cleared)
                {
                    Snack($"No hay ROI que eliminar. Dibuja un ROI válido antes de guardar."); // CODEX: string interpolation compatibility.
                    return;
                }

                if (!TrySaveLayoutGuarded("[wizard]", $"clear => {layoutPath}", requireMasters: true, out var clearError))
                {
                    Snack($"Error guardando layout: {clearError ?? "desconocido"}"); // CODEX: string interpolation compatibility.
                    return;
                }

                var removalSummary = clearedRole?.ToString() ?? "ROI";
                Snack($"ROI eliminado ({removalSummary}). Dibuja un ROI válido antes de guardar.");
                return;
            }

            var bufferSource = "fresh";
            RoiModel? savedRoi = null;
            RoiRole? savedRole = null;

            switch (stateForSave)
            {
                case MasterState.DrawM1_Pattern:
                    savedRole = RoiRole.Master1Pattern;
                    AppendLog($"[wizard] save state={stateForSave} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    if (!IsAllowedMasterShape(_tmpBuffer.Shape)) { // CODEX: string interpolation compatibility.
                        Snack($"Master: usa rectángulo, círculo o annulus"); // CODEX: string interpolation compatibility.
                        return;
                    }
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master1Pattern = _tmpBuffer.Clone();
                    savedRoi = _layout.Master1Pattern;
                    // Preview (si hay imagen cargada)
                    {
                        var displayRect = GetImageDisplayRect();
                        var (pw, ph) = GetImagePixelSize();
                        double scale = displayRect.Width / System.Math.Max(1.0, pw);
                        AppendLog($"[save] scale={scale:0.####} dispRect=({displayRect.Left:0.##},{displayRect.Top:0.##})");
                        AppendLog($"[save] ROI image : {savedRoi}");
                    }
                    SaveRoiCropPreview(_layout.Master1Pattern, "M1_pattern");
                    _layout.Master1PatternImagePath = SaveMasterPatternCanonical(_layout.Master1Pattern, "master1_pattern");

                    _tmpBuffer = null;
                    if (!restoreStateAfterSave)
                        _state = MasterState.DrawM1_Search;

                    // Auto-cambiar el combo de rol a "Inspección Master 1"
                    try { ComboMasterRoiRole.SelectedIndex = 1; } catch { }
                    break;

                case MasterState.DrawM1_Search:
                    savedRole = RoiRole.Master1Search;
                    AppendLog($"[wizard] save state={stateForSave} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master1Search = _tmpBuffer.Clone();
                    savedRoi = _layout.Master1Search;

                    SaveRoiCropPreview(_layout.Master1Search, "M1_search");

                    _tmpBuffer = null;
                    if (!restoreStateAfterSave)
                        _state = MasterState.DrawM2_Pattern;
                    break;

                case MasterState.DrawM2_Pattern:
                    savedRole = RoiRole.Master2Pattern;
                    AppendLog($"[wizard] save state={stateForSave} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    if (!IsAllowedMasterShape(_tmpBuffer.Shape)) { // CODEX: string interpolation compatibility.
                        Snack($"Master: usa rectángulo, círculo o annulus"); // CODEX: string interpolation compatibility.
                        return;
                    }
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master2Pattern = _tmpBuffer.Clone();
                    savedRoi = _layout.Master2Pattern;
                    SaveRoiCropPreview(_layout.Master2Pattern, "M2_pattern");
                    _layout.Master2PatternImagePath = SaveMasterPatternCanonical(_layout.Master2Pattern, "master2_pattern");

                    KeepOnlyMaster2InCanvas();
                    LogHeatmap("KeepOnlyMaster2InCanvas called after saving Master2Pattern.");

                    _tmpBuffer = null;
                    if (!restoreStateAfterSave)
                        _state = MasterState.DrawM2_Search;

                    // Auto-cambiar el combo de rol a "Inspección Master 2"
                    try { ComboM2Role.SelectedIndex = 1; } catch { }
                    break;

                case MasterState.DrawM2_Search:
                    savedRole = RoiRole.Master2Search;
                    AppendLog($"[wizard] save state={stateForSave} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    _tmpBuffer.Role = savedRole.Value;

                    _layout.Master2Search = _tmpBuffer.Clone();
                    savedRoi = _layout.Master2Search;

                    SaveRoiCropPreview(_layout.Master2Search, "M2_search");

                    KeepOnlyMaster2InCanvas();

                    // Ensure overlay fully refreshed so Master2 doesn't appear missing
                    try { ScheduleSyncOverlay(true, reason: "Wizard.Master2Search"); }
                    catch
                    {
                        SyncOverlayToImage();
                        try { RedrawOverlaySafe(); } catch { RedrawOverlay(); }
                        UpdateHeatmapOverlayLayoutAndClip();
                        try { RedrawAnalysisCrosses(); } catch {}
                    }
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Redraw forced after saving Master2-Search.");

                    _tmpBuffer = null;

                    // En este punto M1+M2 podrían estar completos → permite inspección pero NO la exige
                    if (!restoreStateAfterSave)
                        _state = MasterState.DrawInspection; // Puedes seguir con inspección si quieres
                    break;

                case MasterState.DrawInspection:
                    savedRole = RoiRole.Inspection;
                    AppendLog($"[wizard] save state={stateForSave} role={savedRole} source={bufferSource} roi={DescribeRoi(_tmpBuffer)}");
                    _tmpBuffer.Role = savedRole.Value;

                    var savedClone = _tmpBuffer.Clone();
                    SetInspectionSlotModel(_activeInspectionIndex, savedClone, updateActive: false);
                    RefreshInspectionRoiSlots();
                    savedRoi = GetInspectionSlotModel(_activeInspectionIndex);
                    SetInspectionBaseline(_layout.Inspection);
                    SyncCurrentRoiFromInspection(_layout.Inspection);

                    // (Opcional) también puedes guardar un preview de la inspección inicial:
                    if (_layout.Inspection != null)
                    {
                        SaveRoiCropPreview(_layout.Inspection, "INS_init");
                    }

                    _tmpBuffer = null;
                    _state = MasterState.Ready;
                    break;
            }

            if (restoreStateAfterSave)
            {
                _state = originalState;
            }

            var savedRoiModel = savedRoi;

            bool skipRedrawForMasterInspection = savedRoiModel != null &&
                (savedRoiModel.Role == RoiRole.Master1Search);

            if (skipRedrawForMasterInspection)
            {
                ClearCanvasShapesAndLabels();
                ClearCanvasInternalMaps();
                DetachPreviewAndAdorner();

                // IMPORTANT: Do NOT call RedrawOverlay / RedrawOverlaySafe here.
                // The model/layout remains intact, but UI stays blank as requested.
            }

            // Ensure saved ROI has a stable Label for unique TextBlock names
            try
            {
                if (savedRoiModel != null)
                {
                    string resolved = ResolveRoiLabelText(savedRoiModel);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        savedRoiModel.Label = resolved;
                }
            }
            catch { /* ignore */ }

            // Clear preview if present (so it doesn’t overlay)
            try
            {
                if (_previewShape != null)
                {
                    CanvasROI.Children.Remove(_previewShape);
                    _previewShape = null;
                }
            }
            catch { /* ignore */ }

            // Redraw saved ROIs (now with visible stroke/fill and unique labels)
            if (!skipRedrawForMasterInspection)
            {
                RedrawOverlay();
            }

            // If we can find the shape for this saved ROI, position its label explicitly
            try
            {
                if (savedRoiModel != null)
                {
                    var shape = CanvasROI.Children.OfType<Shape>()
                        .FirstOrDefault(s => s.Tag is RoiModel rm && rm.Id == savedRoiModel.Id);
                    if (shape != null) UpdateRoiLabelPosition(shape);
                }
            }
            catch { /* ignore */ }

            // Limpia preview/adorner y persiste
            ClearPreview();

            var saved = TrySaveLayoutGuarded("[wizard]", $"save => {layoutPath}", requireMasters: true, out var saveError);

            if (!skipRedrawForMasterInspection)
            {
                RedrawOverlaySafe();
            }
            RedrawAnalysisCrosses();

            // IMPORTANTE: recalcula habilitaciones (esto ya deja el botón "Analizar Master" activo si M1+M2 están listos)
            UpdateWizardState();

            if (!saved)
            {
                Snack($"Error guardando layout: {saveError ?? "desconocido"}"); // CODEX: string interpolation compatibility.
                return;
            }

            var savedSummary = savedRoi != null
                ? $"{savedRole}: {DescribeRoi(savedRoi)}"
                : "<sin ROI>";
            _editModeActive = false;
            Snack($"Guardado. {savedSummary}");
        }


        private void ClearPreview()
        {
            if (_previewShape != null)
            {
                var al = AdornerLayer.GetAdornerLayer(_previewShape);
                if (al != null)
                {
                    var prev = al.GetAdorners(_previewShape);
                    if (prev != null)
                    {
                        foreach (var ad in prev.OfType<RoiAdorner>())
                            al.Remove(ad);
                    }
                }
                CanvasROI.Children.Remove(_previewShape);
                _previewShape = null;
            }
        }

        private static bool IsAllowedMasterShape(RoiShape s)
            => s == RoiShape.Rectangle || s == RoiShape.Circle || s == RoiShape.Annulus;

        private void BtnValidateInsp_Click(object sender, RoutedEventArgs e)
        {
            if (_layout.Inspection == null)
            {
                Snack($"Falta ROI Inspección"); // CODEX: string interpolation compatibility.
                return;
            }
            if (!ValidateRoiInImage(_layout.Inspection)) return;
            Snack($"Inspección válida."); // CODEX: string interpolation compatibility.
        }

        private bool ValidateMasterGroup(RoiModel? pattern, RoiModel? search)
        {
            if (pattern == null || search == null)
            {
                Snack($"Faltan patrón o zona de búsqueda"); // CODEX: string interpolation compatibility.
                return false;
            }
            if (!ValidateRoiInImage(pattern)) return false;
            if (!ValidateRoiInImage(search)) return false;

            var patRect = RoiToRect(pattern);
            var seaRect = RoiToRect(search);

            // Centro del patrón
            var pc = new SWPoint(patRect.X + patRect.Width / 2, patRect.Y + patRect.Height / 2);

            // Permitir validación si el centro cae en BÚSQUEDA o en INSPECCIÓN
            bool inSearch = seaRect.Contains(pc);
            bool inInspection = false;
            if (_layout.Inspection != null)
            {
                var insRect = RoiToRect(_layout.Inspection);
                inInspection = insRect.Contains(pc);
            }

            if (!inSearch && !inInspection)
            {
                Snack($"Aviso: el centro del patrón no está dentro de la zona de búsqueda ni de la zona de inspección."); // CODEX: string interpolation compatibility.
            }

            // Guardar imágenes de depuración para verificar coordenadas
            try { SaveDebugRoiImages(pattern, search, _layout.Inspection!); }
            catch { /* no bloquear validación por errores de I/O */ }

            return true;
        }

        private bool ValidateRoiInImage(RoiModel roi)
        {
            if (_imgW <= 0 || _imgH <= 0)
            {
                Snack($"Carga primero una imagen."); // CODEX: string interpolation compatibility.
                return false;
            }
            var r = RoiToRect(roi);
            if (r.Width < 2 || r.Height < 2)
            {
                Snack($"ROI demasiado pequeño."); // CODEX: string interpolation compatibility.
                return false;
            }
            if (r.X < 0 || r.Y < 0 || r.Right > _imgW || r.Bottom > _imgH)
            {
                Snack($"ROI fuera de límites. Se recomienda reajustar."); // CODEX: string interpolation compatibility.
                return false;
            }
            return true;
        }

        private SWRect RoiToRect(RoiModel r)
        {
            if (r.Shape == RoiShape.Rectangle) return new SWRect(r.Left, r.Top, r.Width, r.Height);
            var ro = r.R; return new SWRect(r.CX - ro, r.CY - ro, 2 * ro, 2 * ro);
        }

        // ====== Analizar Master / ROI ======
        // --------- BOTÓN ANALIZAR MASTERS ---------
        // ===== En MainWindow.xaml.cs =====
        private async Task AnalyzeMastersAsync()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await AnalyzeMastersCoreAsync().ConfigureAwait(false);
                    return;
                }
                catch (BackendMemoryNotFittedException ex)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[ANALYZE] backend memory not fitted: {ex.Detail ?? ex.Message}");

                    if (attempt >= 1)
                    {
                        MessageBox.Show(
                            "No hay memoria preparada en el backend. Ejecuta Fit OK desde Dataset y vuelve a intentarlo.",
                            "Memoria no preparada",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    var choice = MessageBox.Show(
                        "No hay memoria/baseline cargada para inferencia. ¿Quieres ajustarla ahora (Fit OK) y reintentar?",
                        "Memoria no preparada",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (choice != MessageBoxResult.Yes)
                    {
                        MessageBox.Show(
                            "Operación cancelada. Ejecuta Fit OK desde la pestaña Dataset antes de volver a analizar.",
                            "Memoria no preparada",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return;
                    }

                    bool fitted = await EnsureMasterBaselinesAsync().ConfigureAwait(false);
                    if (!fitted)
                    {
                        MessageBox.Show(
                            "No se pudo ajustar la memoria automáticamente. Revisa la carpeta del dataset OK y los logs.",
                            "Fit OK",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    // CODEX: string interpolation compatibility.
                    AppendLog($"[ANALYZE] Baseline ajustada, reintentando Analyze Masters...");
                }
                catch (BackendBadRequestException ex)
                {
                    var detail = ex.Detail ?? ex.Message;
                    MessageBox.Show($"Error del backend (400): {detail}", "Inferencia", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                catch (Exception ex)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[ANALYZE] error inesperado: {ex}");
                    MessageBox.Show($"Analyze Masters error: {ex.Message}", "Analyze", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        private async Task AnalyzeMastersCoreAsync()
        {
            // CODEX: string interpolation compatibility.
            AppendLog($"[ANALYZE] Begin AnalyzeMastersAsync");
            // CODEX: string interpolation compatibility.
            AppendLog($"[FLOW] Entrando en AnalyzeMastersAsync");

            ViewModel?.TraceManual($"[manual-master] analyze file='{Path.GetFileName(ViewModel?.CurrentManualImagePath ?? string.Empty)}'");

            SWPoint? c1 = null, c2 = null;
            double s1 = 0, s2 = 0;

            // 1) Intento local primero (opcional)
            if (ChkUseLocalMatcher.IsChecked == true)
            {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[ANALYZE] Using local matcher first...");
                    try
                    {
                        // CODEX: string interpolation compatibility.
                        AppendLog($"[FLOW] Usando matcher local");
                        using var img = Cv.Cv2.ImRead(_currentImagePathWin);
                        Mat? m1Override = null;
                        Mat? m2Override = null;
                        try
                        {
                            if (_layout.Master1Pattern != null)
                                m1Override = TryLoadMasterPatternOverride(_layout.Master1PatternImagePath, "M1");
                            if (_layout.Master2Pattern != null)
                                m2Override = TryLoadMasterPatternOverride(_layout.Master2PatternImagePath, "M2");

                            var res1 = LocalMatcher.MatchInSearchROI(img, _layout.Master1Pattern, _layout.Master1Search,
                                _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax, m1Override,
                                LogToFileAndUI);
                            if (res1.center.HasValue) { c1 = new SWPoint(res1.center.Value.X, res1.center.Value.Y); s1 = res1.score; AppendLog($"[LOCAL] M1 hit score={res1.score:0.###}"); }
                            else
                            {
                                // CODEX: string interpolation compatibility.
                                AppendLog($"[LOCAL] M1 no encontrado");
                            }

                            var res2 = LocalMatcher.MatchInSearchROI(img, _layout.Master2Pattern, _layout.Master2Search,
                                _preset.Feature, _preset.MatchThr, _preset.RotRange, _preset.ScaleMin, _preset.ScaleMax, m2Override,
                                LogToFileAndUI);
                            if (res2.center.HasValue) { c2 = new SWPoint(res2.center.Value.X, res2.center.Value.Y); s2 = res2.score; AppendLog($"[LOCAL] M2 hit score={res2.score:0.###}"); }
                            else
                            {
                                // CODEX: string interpolation compatibility.
                                AppendLog($"[LOCAL] M2 no encontrado");
                            }
                        }
                        finally
                        {
                            m1Override?.Dispose();
                            m2Override?.Dispose();
                        }
                    }
                    catch (DllNotFoundException ex)
                    {
                        // CODEX: string interpolation compatibility.
                        AppendLog($"[OpenCV] DllNotFound: {ex.Message}");
                        Snack($"OpenCvSharp no está disponible. Desactivo 'matcher local'."); // CODEX: string interpolation compatibility.
                    ChkUseLocalMatcher.IsChecked = false;
                }
                catch (Exception ex)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[local matcher] ERROR: {ex.Message}");
                }
            }

            if (c1 is null || c2 is null)
            {
                if (ChkUseLocalMatcher.IsChecked != true)
                {
                    AppendLog($"[FLOW] Matcher local deshabilitado; uso backend para detectar masters");

                    if (c1 is null)
                    {
                        var inferM1 = await BackendAPI
                            .InferAsync(_currentImagePathWin, _layout.Master1Pattern!, _preset, AppendLog)
                            .ConfigureAwait(false);

                        if (inferM1.ok && inferM1.result != null)
                        {
                            var result = inferM1.result;
                            var (cx, cy) = _layout.Master1Pattern!.GetCenter();
                            c1 = new SWPoint(cx, cy);
                            s1 = 100;
                            string thrText = result.threshold.HasValue
                                ? result.threshold.Value.ToString("0.###", CultureInfo.InvariantCulture)
                                : "n/a";
                            bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
                            AppendLog($"[FLOW] backend M1 score={result.score:0.###} thr={thrText} status={(pass ? "OK" : "NG")}");
                        }
                        else
                        {
                            AppendLog($"[FLOW] backend M1 failed: {inferM1.error ?? "unknown"}");
                        }
                    }

                    if (c2 is null)
                    {
                        var inferM2 = await BackendAPI
                            .InferAsync(_currentImagePathWin, _layout.Master2Pattern!, _preset, AppendLog)
                            .ConfigureAwait(false);

                        if (inferM2.ok && inferM2.result != null)
                        {
                            var result = inferM2.result;
                            var (cx, cy) = _layout.Master2Pattern!.GetCenter();
                            c2 = new SWPoint(cx, cy);
                            s2 = 100;
                            string thrText = result.threshold.HasValue
                                ? result.threshold.Value.ToString("0.###", CultureInfo.InvariantCulture)
                                : "n/a";
                            bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
                            AppendLog($"[FLOW] backend M2 score={result.score:0.###} thr={thrText} status={(pass ? "OK" : "NG")}");
                        }
                        else
                        {
                            AppendLog($"[FLOW] backend M2 failed: {inferM2.error ?? "unknown"}");
                        }
                    }
                }
                else
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[FLOW] Matcher local no encontró alguno de los masters; sin fallback al backend (/infer desactivado)");
                }
            }


            // 3) Manejo de fallo
            if (c1 is null)
            {
                Snack($"No se ha encontrado Master 1 en su zona de búsqueda"); // CODEX: string interpolation compatibility.
                // CODEX: string interpolation compatibility.
                AppendLog($"[FLOW] c1 null");
                ViewModel?.TraceManual($"[manual-master] FAIL file='{Path.GetFileName(ViewModel?.CurrentManualImagePath ?? string.Empty)}' reason=M1 not found");
                return;
            }
            if (c2 is null)
            {
                Snack($"No se ha encontrado Master 2 en su zona de búsqueda"); // CODEX: string interpolation compatibility.
                // CODEX: string interpolation compatibility.
                AppendLog($"[FLOW] c2 null");
                ViewModel?.TraceManual($"[manual-master] FAIL file='{Path.GetFileName(ViewModel?.CurrentManualImagePath ?? string.Empty)}' reason=M2 not found");
                return;
            }

            // 4) Dibujar cruces siempre para la imagen actual
            var mid = new SWPoint((c1.Value.X + c2.Value.X) / 2.0, (c1.Value.Y + c2.Value.Y) / 2.0);
            AppendLog($"[FLOW] mid=({mid.X:0.##},{mid.Y:0.##})");

            EnterAnalysisView();

            _lastM1CenterPx = new CvPoint((int)System.Math.Round(c1.Value.X), (int)System.Math.Round(c1.Value.Y));
            _lastM2CenterPx = new CvPoint((int)System.Math.Round(c2.Value.X), (int)System.Math.Round(c2.Value.Y));
            _lastMidCenterPx = new CvPoint((int)System.Math.Round(mid.X), (int)System.Math.Round(mid.Y));
            RedrawAnalysisCrosses();

            ViewModel?.TraceManual(
                $"[manual-master] OK file='{Path.GetFileName(ViewModel?.CurrentManualImagePath ?? string.Empty)}' " +
                $"M1=({c1.Value.X:0.0},{c1.Value.Y:0.0}) M2=({c2.Value.X:0.0},{c2.Value.Y:0.0}) score=({s1:0},{s2:0})");
            GuiLog.Info($"[analyze-master] file='{Path.GetFileName(ViewModel?.CurrentManualImagePath ?? string.Empty)}' found M1=({c1.Value.X:0.0},{c1.Value.Y:0.0}) M2=({c2.Value.X:0.0},{c2.Value.Y:0.0}) score=({s1:0},{s2:0})");

            // === BEGIN: reposition Masters & Inspections ===
            try
            {
                var crossM1 = c1.Value;
                var crossM2 = c2.Value;
                var master1Baseline = _layout?.Master1Pattern?.Clone();
                var master2Baseline = _layout?.Master2Pattern?.Clone();
                bool scaleLock = ScaleLock;

                AcceptNewDetectionIfDifferent(crossM1, crossM2, scaleLock, master1Baseline, master2Baseline);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnalyzeMaster-Reposition] {ex.Message}");
            }
            // === END: reposition Masters & Inspections ===

            // 5) Reubicar inspección si existe
            var inspectionRoi = _layout.Inspection;
            if (inspectionRoi == null)
            {
                Snack($"Masters OK. Falta ROI de Inspección: dibújalo y guarda. Las cruces ya están dibujadas."); // CODEX: string interpolation compatibility.
                // CODEX: string interpolation compatibility.
                AppendLog($"[FLOW] Inspection null");
                _state = MasterState.DrawInspection;
                UpdateWizardState();
                return;
            }

            if (inspectionRoi.IsFrozen)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[FLOW] Inspection ROI frozen; skipping auto reposition.");
            }
            else
            {
                using (SuppressAutoSaves())
                {
                    MoveInspectionTo(inspectionRoi, c1.Value, c2.Value);
                    ClipInspectionROI(inspectionRoi, _imgW, _imgH);
                    RedrawOverlay();
                }

                // CODEX: string interpolation compatibility.
                AppendLog($"[FLOW] Inspection movida y recortada");
            }

            try
            {
                // Si el flujo de inferencia ha dejado _lastHeatmapRoi, persiste en el layout según su rol.
                if (_lastHeatmapRoi != null)
                {
                    switch (_lastHeatmapRoi.Role)
                    {
                        case RoiRole.Inspection:
                            _layout.Inspection = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master1Pattern:
                            _layout.Master1Pattern = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master1Search:
                            _layout.Master1Search = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master2Pattern:
                            _layout.Master2Pattern = _lastHeatmapRoi.Clone();
                            break;
                        case RoiRole.Master2Search:
                            _layout.Master2Search = _lastHeatmapRoi.Clone();
                            break;
                        default:
                            // Si no encaja en roles conocidos, no sobrescribir nada
                            break;
                    }
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Persisted detected ROI into layout: {_lastHeatmapRoi.Role}");
                    UpdateRoiVisibilityControls();
                }
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[UI] Persist layout with detected ROI failed: {ex.Message}");
            }

            SaveInspectionBaselineForCurrentImage();
            if (TryAutosaveLayout("analyze masters"))
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[FLOW] Layout guardado");
            }

            _hasInspectionAnalysisTransform = true;
            _isFirstImageForCurrentKey = false;

            if (_inspectionBaselineRestorePending)
            {
                RestoreInspectionBaselineForCurrentImage();
            }

            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    RedrawOverlay();
                    UpdateHeatmapOverlayLayoutAndClip();
                    RedrawAnalysisCrosses();
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Post-Analyze refresh applied (no scheduler).");
                }
                catch (Exception ex)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Post-Analyze refresh failed: {ex.Message}");
                }
            });

            Snack($"Masters OK. Scores: M1={s1:0.000}, M2={s2:0.000}. ROI inspección reubicado.");
            _state = MasterState.Ready;
            UpdateWizardState();
            // CODEX: string interpolation compatibility.
            AppendLog($"[FLOW] AnalyzeMastersAsync terminado");
        }

        private async Task<bool> EnsureMasterBaselinesAsync()
        {
            if (_backendClient == null)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[ANALYZE] Backend client no disponible para auto-fit.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[ANALYZE] Ruta de imagen inválida para auto-fit.");
                return false;
            }

            double mmPerPx = BackendAPI.ResolveMmPerPx(_preset);
            var rois = new[] { _layout.Master1Pattern, _layout.Master2Pattern };
            bool any = false;
            bool allOk = true;

            foreach (var roi in rois)
            {
                if (roi == null)
                {
                    continue;
                }

                any = true;
                string roleId = BackendAPI.ResolveRoleId(roi);
                string roiId = BackendAPI.ResolveRoiId(roi);

                try
                {
                    bool fitted = await _backendClient.EnsureFittedAsync(
                        roleId,
                        roiId,
                        mmPerPx,
                        async _ =>
                        {
                            if (!BackendAPI.TryPrepareCanonicalRoi(_currentImagePathWin, roi, out var payload, out var fileName, AppendLog) || payload == null)
                            {
                                GuiLog.Warn($"Auto-fit sin muestras para role='{roleId}' roi='{roiId}'");
                                return Array.Empty<BackendClient.FitImage>();
                            }

                            GuiLog.Info($"Auto-fit enviando muestra para role='{roleId}' roi='{roiId}'");
                            return new[]
                            {
                                new BackendClient.FitImage(payload.PngBytes, fileName)
                            };
                        },
                        memoryFit: false).ConfigureAwait(false);

                    if (!fitted)
                    {
                        AppendLog($"[ANALYZE] Auto-fit fallido o incompleto para {roleId}/{roiId}.");
                        GuiLog.Warn($"Auto-fit incompleto para role='{roleId}' roi='{roiId}'");
                        allOk = false;
                    }
                    else
                    {
                        GuiLog.Info($"Auto-fit completado para role='{roleId}' roi='{roiId}'");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[ANALYZE] Auto-fit error para {roleId}/{roiId}: {ex.Message}");
                    GuiLog.Error($"Auto-fit error para role='{roleId}' roi='{roiId}'", ex);
                    allOk = false;
                }
            }

            return any && allOk;
        }












        // Log seguro desde cualquier hilo
        private void AppendLog(string line)
        {
            LogToFileAndUI(line);
        }

        private void AppendLogBulk(IEnumerable<string> lines)
        {
            foreach (var entry in lines)
            {
                LogToFileAndUI(entry);
            }
        }

        private void LogToFileAndUI(string message)
        {
#if DEBUG
            var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Debug.WriteLine(stamped);
#endif
        }

        private void AppendLogLine(string line)
        {
#if DEBUG
            Debug.WriteLine(line);
#endif
        }

        // --------- AppendLog (para evitar CS0119 en invocaciones) ---------

        private static string FInsp(RoiModel? r) =>
            r == null ? "<null>"
                      : $"L={r.Left:F3},T={r.Top:F3},W={r.Width:F3},H={r.Height:F3},CX={r.CX:F3},CY={r.CY:F3},R={r.R:F3},Rin={r.RInner:F3},Ang={r.AngleDeg:F3}";

        private void InspLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InspAlignLogPath)!);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
                lock (_inspLogLock) { File.AppendAllText(InspAlignLogPath, line, Encoding.UTF8); }
            }
            catch { /* never throw from logging */ }
        }

        private string ComputeImageSeedKey()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_currentImagePathWin) && File.Exists(_currentImagePathWin))
                {
                    var bytes = File.ReadAllBytes(_currentImagePathWin);
                    return HashSHA256(bytes);
                }

                var bs = ImgMain?.Source as BitmapSource ?? _currentImageSource;
                if (bs != null)
                {
                    return HashSHA256(GetPixels(bs));
                }
            }
            catch
            {
                // ignored — fallback below
            }

            return "0x0|0|0|";
        }

        private static double AngleDeg(double dy, double dx)
            => (System.Math.Atan2(dy, dx) * 180.0 / System.Math.PI);

        private static double Dist(double x1, double y1, double x2, double y2)
            => System.Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));

        private void SeedInspectionBaselineOnce(RoiModel? insp, string seedKey)
        {
            if (!_useFixedInspectionBaseline) return;
            if (_inspectionBaselineSeededForImage)
            {
                InspLog($"[Seed] Skip: already seeded for key='{_lastImageSeedKey}'");
                return;
            }
            // Always prefer the persisted inspection baseline when seeding.
            var baseline = _layout?.InspectionBaseline;
            if (baseline == null)
            {
                // Fallback only if the persisted baseline is not available yet.
                baseline = insp ?? _layout?.Inspection;
            }

            if (baseline == null)
            {
                InspLog("[Seed] Skip: baseline is null");
                return;
            }

            _inspectionBaselineFixed = baseline.Clone();
            _inspectionBaselineSeededForImage = true;
            _lastImageSeedKey = seedKey;
            InspLog($"[Seed] Fixed baseline SEEDED (key='{seedKey}') from: {FInsp(_inspectionBaselineFixed)}");
        }

        private void ReseedInspectionBaselineFromLayout(string context)
        {
            if (!_useFixedInspectionBaseline || !_hasLoadedImage)
            {
                return;
            }

            var seedKey = ComputeImageSeedKey();
            _currentImageHash = seedKey;
            _inspectionBaselineSeededForImage = false;
            _inspectionBaselineFixed = null;

            var seedRoi = GetInspectionSlotModel(_activeInspectionIndex)
                ?? GetFirstInspectionRoi();

            if (seedRoi == null)
            {
                RoiDiag($"[preset:inspection-baseline] skip context={context} reason=no-seed-roi");
                return;
            }

            SeedInspectionBaselineOnce(seedRoi, seedKey);
            RoiDiag(string.Format(
                CultureInfo.InvariantCulture,
                "[preset:inspection-baseline] context={0} seedId={1} base=({2}x{3}) L={4:0.###} T={5:0.###} W={6:0.###} H={7:0.###}",
                context,
                seedRoi.Id ?? "<null>",
                seedRoi.BaseImgW?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-",
                seedRoi.BaseImgH?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-",
                seedRoi.Left,
                seedRoi.Top,
                seedRoi.Width,
                seedRoi.Height));
        }

        private List<RoiModel> SnapshotInspectionRois()
        {
            return CollectSavedInspectionRois()
                .Select(r => r.Clone())
                .ToList();
        }

        private void SaveInspectionBaselineForCurrentImage()
        {
            if (string.IsNullOrWhiteSpace(_currentImageHash))
            {
                return;
            }

            var snapshot = SnapshotInspectionRois();
            if (snapshot.Count == 0)
            {
                _inspectionBaselinesByImage.Remove(_currentImageHash);
            }
            else
            {
                _inspectionBaselinesByImage[_currentImageHash] = snapshot;
            }

            if (_layout != null)
            {
                _layout.InspectionBaselinesByImage = new Dictionary<string, List<RoiModel>>(_inspectionBaselinesByImage, StringComparer.OrdinalIgnoreCase);
                TryPersistLayout();
            }
        }

        private void RestoreInspectionBaselineForCurrentImage()
        {
            if (string.IsNullOrWhiteSpace(_currentImageHash))
            {
                _inspectionBaselineRestorePending = false;
                return;
            }

            if (!_hasInspectionAnalysisTransform || _isFirstImageForCurrentKey)
            {
                _inspectionBaselineRestorePending = true;
                return;
            }

            _inspectionBaselineRestorePending = false;

            if (_inspectionBaselinesByImage.TryGetValue(_currentImageHash, out var snapshot) && snapshot.Count > 0)
            {
                var clones = snapshot.Select(r => r.Clone()).ToList();
                if (clones.Count > 0)
                {
                    _layout.Inspection = clones[0];
                }

                RefreshInspectionRoiSlots(clones);
                RedrawAllRois();
            }
        }

        // Apply translation/rotation/scale to a target ROI using a baseline ROI and an old->new pivot
        // angleDelta in RADIANS; pivotOld/new in IMAGE coordinates
        private static void ApplyRoiTransform(RoiModel target, RoiModel baseline,
                                              double pivotOldX, double pivotOldY,
                                              double pivotNewX, double pivotNewY,
                                              double scale, double angleDeltaRad)
        {
            if (target == null || baseline == null) return;

            // Baseline center (image space)
            var (baseCx, baseCy) = GetCenterShapeAware(baseline);
            double relX = baseCx - pivotOldX;
            double relY = baseCy - pivotOldY;

            double cos = Math.Cos(angleDeltaRad), sin = Math.Sin(angleDeltaRad);
            double relXr = scale * (cos * relX - sin * relY);
            double relYr = scale * (sin * relX + cos * relY);

            // New center
            double newCX = pivotNewX + relXr;
            double newCY = pivotNewY + relYr;

            // Scale size (generic: Width/Height; for circles/annulus R, RInner)
            double newW = baseline.Width  * scale;
            double newH = baseline.Height * scale;

            target.Width  = newW;
            target.Height = newH;

            // Update center & box
            target.CX  = newCX;
            target.CY  = newCY;
            target.Left = newCX - (newW * 0.5);
            target.Top  = newCY - (newH * 0.5);

            // If circular radii exist, scale them (no-ops if zero)
            target.R      = baseline.R      * scale;
            target.RInner = baseline.RInner * scale;

            // Apply angle rotation for rectangular ROIs
            double dAngDeg = (angleDeltaRad * 180.0 / Math.PI);
            target.AngleDeg = baseline.AngleDeg + dAngDeg;
        }

        private void MoveInspectionTo(RoiModel insp, SWPoint master1, SWPoint master2)
        {
            bool isBatch = ViewModel?.CurrentRowIndex > 0;
            bool detectionAccepted = _lastDetectionAccepted || isBatch;
            _lastDetectionAccepted = false;

            if (insp?.IsFrozen == true)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[Analyze] Inspection ROI frozen; skipping MoveInspectionTo.");
                return;
            }

            bool useFixedBaseline = _useFixedInspectionBaseline && !isBatch;

            double oldLeft = insp?.Left ?? 0;
            double oldTop = insp?.Top ?? 0;
            double oldWidth = insp?.Width ?? 0;
            double oldHeight = insp?.Height ?? 0;

            // === Analyze: BEFORE state & current image key ===
            var __seedKeyNow = ComputeImageSeedKey();
            _currentImageHash = __seedKeyNow;
            InspLog($"[Analyze] Key='{__seedKeyNow}' BEFORE insp: {FInsp(insp)}  M1=({master1.X:F3},{master1.Y:F3}) M2=({master2.X:F3},{master2.Y:F3})");

            // DEFENSIVE: do NOT re-seed here if already seeded for this image
            var fallbackBaseline = _layout?.InspectionBaseline ?? insp;
            if (useFixedBaseline && !_inspectionBaselineSeededForImage && fallbackBaseline != null)
            {
                // Only seed if the image key is different (should not happen if [3] ran properly)
                if (!string.Equals(__seedKeyNow, _lastImageSeedKey, System.StringComparison.Ordinal))
                {
                    SeedInspectionBaselineOnce(fallbackBaseline, __seedKeyNow);
                    InspLog("[Analyze] Fallback seed performed (unexpected), key differed.");
                }
                else
                {
                    InspLog("[Analyze] Fallback seed skipped (already seeded for current image key).");
                    _inspectionBaselineSeededForImage = true;
                }
            }

            // Keep original size to restore after move (size lock)
            double __inspW0   = insp?.Width  ?? 0;
            double __inspH0   = insp?.Height ?? 0;
            double __inspR0   = insp?.R      ?? 0;
            double __inspRin0 = insp?.RInner ?? 0;

            if (insp == null)
                return;

            RoiModel? baselineInspection;
            if (useFixedBaseline)
            {
                baselineInspection = _inspectionBaselineFixed;
                if (baselineInspection == null)
                {
                    var persistedBaseline = GetInspectionBaselineClone();
                    if (persistedBaseline != null)
                    {
                        _inspectionBaselineFixed = persistedBaseline;
                        baselineInspection = _inspectionBaselineFixed;
                        if (!_inspectionBaselineSeededForImage)
                        {
                            _inspectionBaselineSeededForImage = true;
                            _lastImageSeedKey = __seedKeyNow;
                            InspLog("[Analyze] Fallback seed from persisted baseline.");
                        }
                    }
                }
            }
            else
            {
                baselineInspection = GetInspectionBaselineClone() ?? insp.Clone();
            }

            RoiModel? __baseM1S = _layout?.Master1Search ?.Clone();
            RoiModel? __baseM2S = _layout?.Master2Search ?.Clone();
            var __baseHeat = _lastHeatmapRoi?.Clone();

            double m1NewX = master1.X, m1NewY = master1.Y;
            double m2NewX = master2.X, m2NewY = master2.Y;
            var m1_new = new SWPoint(m1NewX, m1NewY);
            var m2_new = new SWPoint(m2NewX, m2NewY);

            bool haveLast = !double.IsNaN(_lastAccM1X) && !double.IsNaN(_lastAccM2X);
            if (haveLast)
            {
                double dM1 = Dist(m1NewX, m1NewY, _lastAccM1X, _lastAccM1Y);
                double dM2 = Dist(m2NewX, m2NewY, _lastAccM2X, _lastAccM2Y);
                double angOld = AngleDeg(_lastAccM2Y - _lastAccM1Y, _lastAccM2X - _lastAccM1X);
                double angNew = AngleDeg(m2NewY - m1NewY, m2NewX - m1NewX);
                double dAng = System.Math.Abs(angNew - angOld);
                if (dAng > 180.0) dAng = 360.0 - dAng;

                if (!detectionAccepted && dM1 <= _analyzePosTolPx && dM2 <= _analyzePosTolPx && dAng <= _analyzeAngTolDeg)
                {
                    InspLog($"[Analyze] NO-OP: detection within tolerance (dM1={dM1:F3}px, dM2={dM2:F3}px, dAng={dAng:F3}°).");
                    return;
                }
            }

            double m1OldX = _m1BaseX, m1OldY = _m1BaseY;
            double m2OldX = _m2BaseX, m2OldY = _m2BaseY;
            var m1_base = new SWPoint(_m1BaseX, _m1BaseY);
            var m2_base = new SWPoint(_m2BaseX, _m2BaseY);
            double scale = 1.0;
            double effectiveScale = 1.0;
            double angDelta = 0.0;
            bool __canTransform = baselineInspection != null && _mastersSeededForImage;
            SWVector eB = new SWVector(0, 0);
            SWVector eN = new SWVector(0, 0);

            if (__canTransform)
            {
                double dxOld = m2OldX - m1OldX;
                double dyOld = m2OldY - m1OldY;
                double lenOld = Math.Sqrt(dxOld * dxOld + dyOld * dyOld);

                double dxNew = m2NewX - m1NewX;
                double dyNew = m2NewY - m1NewY;
                double lenNew = Math.Sqrt(dxNew * dxNew + dyNew * dyNew);

                scale = (lenOld > 1e-9) ? (lenNew / lenOld) : 1.0;
                effectiveScale = ScaleLock ? 1.0 : scale;
                AppendLog($"[UI] AnalyzeMaster scale lock={ScaleLock}, scale={scale:F6} -> eff={effectiveScale:F6}");

                double angOldRad = Math.Atan2(dyOld, dxOld);
                double angNewRad = Math.Atan2(dyNew, dxNew);
                angDelta = angNewRad - angOldRad;

                eB = Normalize(new SWVector(dxOld, dyOld));
                eN = Normalize(new SWVector(dxNew, dyNew));

                // Normalize angle delta to [-180°, +180°)
                double deg = angDelta * 180.0 / Math.PI;
                deg = (deg + 540.0) % 360.0 - 180.0;
                angDelta = deg * Math.PI / 180.0;

                InspLog($"[Transform] BASE→NEW: M1_base=({m1OldX:F3},{m1OldY:F3}) → M1_new=({m1NewX:F3},{m1NewY:F3}); " +
                        $"M2_base=({m2OldX:F3},{m2OldY:F3}) → M2_new=({m2NewX:F3},{m2NewY:F3}); angΔ={angDelta * 180 / Math.PI:F3}°, effScale={effectiveScale:F6}");
            }

            if (!__canTransform && ScaleLock && insp != null)
            {
                double cx = insp.CX, cy = insp.CY;
                insp.Width  = __inspW0;
                insp.Height = __inspH0;
                insp.R      = __inspR0;
                insp.RInner = __inspRin0;
                insp.Left = cx - (__inspW0 * 0.5);
                insp.Top  = cy - (__inspH0 * 0.5);
            }

            if (!useFixedBaseline)
            {
                try
                {
                    SetInspectionBaseline(insp.Clone());
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Inspection baseline refreshed (rolling mode).");
                }
                catch (Exception ex)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Failed to refresh inspection baseline: {ex.Message}");
                }
            }
            else
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[UI] Fixed Inspection baseline in use (no refresh after Analyze).");
            }

            try
            {
                if (__canTransform && _layout != null)
                {
                    // Congelar los ROIs Master Search durante Analyze (no desplazar ni rotar)
                    if (!FREEZE_MASTER_SEARCH_ON_ANALYZE)
                    {
                        if (_layout.Master1Search != null && __baseM1S != null)
                            ApplyRoiTransform(_layout.Master1Search,  __baseM1S, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                        if (_layout.Master2Search != null && __baseM2S != null)
                            ApplyRoiTransform(_layout.Master2Search,  __baseM2S, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);
                    }
                    else
                    {
                        Dbg("[Analyze] Master Search ROIs frozen: no transform applied");
                    }


                    if (_lastHeatmapRoi != null && __baseHeat != null)
                        ApplyRoiTransform(_lastHeatmapRoi, __baseHeat, m1OldX, m1OldY, m1NewX, m1NewY, effectiveScale, angDelta);

                    try
                    {
                        RedrawOverlay();
                        UpdateHeatmapOverlayLayoutAndClip();
                        RedrawAnalysisCrosses();
                    }
                    catch (Exception ex)
                    {
                        // CODEX: string interpolation compatibility.
                        AppendLog($"[UI] Unified transform redraw failed: {ex.Message}");
                    }

                    // CODEX: string interpolation compatibility.
                    AppendLog($"[UI] Unified transform applied to search/heatmap ROIs.");
                }
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[UI] Unified transform failed: {ex.Message}");
            }

            SyncCurrentRoiFromInspection(insp);

            AppendLog($"[analyze] move inspection from=({oldLeft:0.##},{oldTop:0.##},{oldWidth:0.##},{oldHeight:0.##}) to=({insp.Left:0.##},{insp.Top:0.##},{insp.Width:0.##},{insp.Height:0.##})");

            // === AnalyzeMaster: AFTER state + delta (vs FIXED baseline) ===
            InspLog($"[Analyze] AFTER  insp: {FInsp(insp)}");
            if (_inspectionBaselineFixed != null)
            {
                InspLog($"[Analyze] DELTA  : dCX={(insp.CX - _inspectionBaselineFixed.CX):F3}, dCY={(insp.CY - _inspectionBaselineFixed.CY):F3}  (fixedBaseline={useFixedBaseline})");
            }

            _lastAccM1X = m1NewX; _lastAccM1Y = m1NewY;
            _lastAccM2X = m2NewX; _lastAccM2Y = m2NewY;
        }

        private RoiModel? GetInspectionBaselineClone()
        {
            return _layout?.InspectionBaseline?.Clone();
        }

        private void SetInspectionBaseline(RoiModel? source)
        {
            if (_layout == null)
                return;

            _layout.InspectionBaseline = source?.Clone();
        }

        private void EnsureInspectionBaselineInitialized()
        {
            if (_layout?.InspectionBaseline == null && _layout?.Inspection != null)
            {
                SetInspectionBaseline(_layout.Inspection);
            }
        }

        private void ClipInspectionROI(RoiModel insp, int imgW, int imgH)
        {
            if (imgW <= 0 || imgH <= 0) return;
            if (insp.Shape == RoiShape.Rectangle)
            {
                if (insp.Width < 1) insp.Width = 1;
                if (insp.Height < 1) insp.Height = 1;
                double left = Math.Max(0, insp.Left);
                double top = Math.Max(0, insp.Top);
                double right = Math.Min(imgW, left + insp.Width);
                double bottom = Math.Min(imgH, top + insp.Height);

                double newWidth = Math.Max(1, right - left);
                double newHeight = Math.Max(1, bottom - top);

                insp.Width = newWidth;
                insp.Height = newHeight;
                insp.Left = Math.Max(0, Math.Min(left, imgW - newWidth));
                insp.Top = Math.Max(0, Math.Min(top, imgH - newHeight));
            }
            else
            {
                var ro = insp.R; var ri = insp.RInner;
                if (insp.Shape == RoiShape.Annulus)
                {
                    if (ro < 2) ro = 2;
                    if (ri < 1) ri = 1;
                    if (ri >= ro) ri = ro - 1;
                    insp.R = ro; insp.RInner = ri;
                }
                if (insp.CX < ro) insp.CX = ro;
                if (insp.CY < ro) insp.CY = ro;
                if (insp.CX > imgW - ro) insp.CX = imgW - ro;
                if (insp.CY > imgH - ro) insp.CY = imgH - ro;
            }

            SyncCurrentRoiFromInspection(insp);
        }



        private RoiModel BuildCurrentRoiModel(RoiRole? roleOverride = null)
        {
            var model = new RoiModel
            {
                Shape = CurrentRoi.Shape,
                AngleDeg = CurrentRoi.AngleDeg,
                Role = roleOverride ?? RoiRole.Inspection
            };

            if (_imgW > 0 && _imgH > 0)
            {
                model.BaseImgW = _imgW;
                model.BaseImgH = _imgH;
            }

            if (!string.IsNullOrWhiteSpace(CurrentRoi.Legend))
            {
                model.Label = CurrentRoi.Legend;
            }

            if (CurrentRoi.Shape == RoiShape.Rectangle)
            {
                model.X = CurrentRoi.X;
                model.Y = CurrentRoi.Y;
                model.Width = Math.Max(1.0, CurrentRoi.Width);
                model.Height = Math.Max(1.0, CurrentRoi.Height);
                model.CX = model.X;
                model.CY = model.Y;
                model.R = Math.Max(model.Width, model.Height) / 2.0;
                model.RInner = 0;
            }
            else
            {
                model.CX = CurrentRoi.CX;
                model.CY = CurrentRoi.CY;
                model.R = Math.Max(1.0, CurrentRoi.R);
                model.Width = model.R * 2.0;
                model.Height = model.Width;
                model.X = model.CX;
                model.Y = model.CY;
                model.RInner = CurrentRoi.Shape == RoiShape.Annulus
                    ? AnnulusDefaults.ClampInnerRadius(CurrentRoi.RInner, model.R)
                    : 0;
                if (CurrentRoi.Shape == RoiShape.Annulus && CurrentRoi.RInner > 0)
                {
                    model.Height = Math.Max(model.Height, CurrentRoi.R * 2.0);
                }
            }

            var role = roleOverride ?? _layout.Inspection?.Role ?? GetCurrentStateRole() ?? RoiRole.Inspection;
            model.Role = role;
            return model;
        }

        private Mat GetRotatedCrop(Mat source)
        {
            if (source == null || source.Empty())
                return new Mat();

            CurrentRoi.EnforceMinSize(10, 10);
            var currentModel = BuildCurrentRoiModel();
            if (!RoiCropUtils.TryBuildRoiCropInfo(currentModel, out var info))
                return new Mat();

            if (RoiCropUtils.TryGetRotatedCrop(source, info, currentModel.AngleDeg, out var crop, out _))
                return crop;

            return new Mat();
        }

        private bool LooksLikeCanvasCoords(RoiModel roi)
        {
            if (roi == null)
                return false;

            var disp = GetImageDisplayRect();
            if (disp.Width <= 0 || disp.Height <= 0)
                return false;

            var (pw, ph) = GetImagePixelSize();

            double width = roi.Shape == RoiShape.Rectangle ? roi.Width : Math.Max(1.0, roi.R * 2.0);
            double height = roi.Shape == RoiShape.Rectangle
                ? roi.Height
                : (roi.Shape == RoiShape.Annulus && roi.Height > 0 ? roi.Height : Math.Max(1.0, roi.R * 2.0));
            double left = roi.Shape == RoiShape.Rectangle ? roi.Left : roi.CX - width / 2.0;
            double top = roi.Shape == RoiShape.Rectangle ? roi.Top : roi.CY - height / 2.0;

            bool withinCanvas = left >= -1 && top >= -1 && width <= disp.Width + 2 && height <= disp.Height + 2;
            bool clearlyNotImageScale = width > pw + 2 || height > ph + 2;
            return withinCanvas && clearlyNotImageScale;
        }

        private Mat GetUiMatOrReadFromDisk()
        {
            if (ImgMain?.Source is BitmapSource bs)
            {
                return BitmapSourceConverter.ToMat(bs);
            }

            if (!string.IsNullOrWhiteSpace(_currentImagePathWin))
            {
                var mat = Cv2.ImRead(_currentImagePathWin, ImreadModes.Unchanged);
                if (!mat.Empty())
                    return mat;
            }

            throw new InvalidOperationException("No hay imagen disponible para exportar el ROI.");
        }

        private string EnsureAndGetPreviewDir()
        {
            var imgDir = Path.GetDirectoryName(_currentImagePathWin) ?? string.Empty;
            var previewDir = Path.Combine(imgDir, "roi_previews");
            Directory.CreateDirectory(previewDir);
            return previewDir;
        }

        private string EnsureAndGetMasterPatternDir()
        {
            var layoutPath = MasterLayoutManager.GetDefaultPath(_preset);
            var layoutDir = Path.GetDirectoryName(layoutPath);
            if (string.IsNullOrEmpty(layoutDir))
                layoutDir = _preset.Home;
            var masterDir = Path.Combine(layoutDir!, "master_patterns");
            Directory.CreateDirectory(masterDir);
            return masterDir;
        }

        private bool TryBuildRoiCrop(RoiModel roi, string logTag, out Mat? cropWithAlpha,
            out RoiCropInfo cropInfo, out Cv.Rect cropRect)
        {
            cropWithAlpha = null;
            cropInfo = default;
            cropRect = default;

            try
            {
                if (roi == null)
                {
                    AppendLog($"[{logTag}] ROI == null");
                    return false;
                }

                var roiImage = LooksLikeCanvasCoords(roi) ? CanvasToImage(roi) : roi.Clone();

                using var src = GetUiMatOrReadFromDisk();
                if (src.Empty())
                {
                    AppendLog($"[{logTag}] Imagen fuente vacía.");
                    return false;
                }

                if (!RoiCropUtils.TryBuildRoiCropInfo(roiImage, out cropInfo))
                {
                    AppendLog($"[{logTag}] ROI no soportado para recorte.");
                    return false;
                }

                if (!RoiCropUtils.TryGetRotatedCrop(src, cropInfo, roiImage.AngleDeg, out var cropMat, out var cropRectLocal))
                {
                    AppendLog($"[{logTag}] No se pudo obtener el recorte rotado.");
                    return false;
                }

                Mat? alphaMask = null;
                try
                {
                    alphaMask = RoiCropUtils.BuildRoiMask(cropInfo, cropRectLocal);
                    cropWithAlpha = RoiCropUtils.ConvertCropToBgra(cropMat, alphaMask);
                    cropRect = cropRectLocal;
                    return true;
                }
                finally
                {
                    alphaMask?.Dispose();
                    cropMat.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[{logTag}] error: " + ex.Message);
                return false;
            }
        }

        private void SaveRoiCropPreview(RoiModel roi, string tag)
        {
            if (!TryBuildRoiCrop(roi, "preview", out var cropWithAlpha, out var cropInfo, out var cropRect))
                return;

            using (cropWithAlpha)
            {
                var outDir = EnsureAndGetPreviewDir();
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                string fname = $"{tag}_{ts}.png";
                var outPath = Path.Combine(outDir, fname);

                Cv2.ImWrite(outPath, cropWithAlpha);
                AppendLog($"[preview] Guardado {fname} ROI=({cropInfo.Left:0.#},{cropInfo.Top:0.#},{cropInfo.Width:0.#},{cropInfo.Height:0.#}) " +
                          $"crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) ang={roi.AngleDeg:0.##}");
            }
        }

        private string? SaveMasterPatternCanonical(RoiModel roi, string fileNameBase)
        {
            if (!TryBuildRoiCrop(roi, "master", out var cropWithAlpha, out var cropInfo, out var cropRect))
                return null;

            using (cropWithAlpha)
            {
                var dir = EnsureAndGetMasterPatternDir();
                var fileName = fileNameBase + ".png";
                var outPath = Path.Combine(dir, fileName);
                Cv2.ImWrite(outPath, cropWithAlpha);
                AppendLog($"[master] Guardado {fileName} ROI=({cropInfo.Left:0.#},{cropInfo.Top:0.#},{cropInfo.Width:0.#},{cropInfo.Height:0.#}) " +
                          $"crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) ang={roi.AngleDeg:0.##}");
                return outPath;
            }
        }

        private Mat? TryLoadMasterPatternOverride(string? path, string tag)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                if (!File.Exists(path))
                {
                    AppendLog($"[master] PNG de patrón {tag} no encontrado: {path}");
                    return null;
                }

                var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
                if (mat.Empty())
                {
                    mat.Dispose();
                    AppendLog($"[master] PNG de patrón {tag} vacío: {path}");
                    return null;
                }

                return mat;
            }
            catch (Exception ex)
            {
                AppendLog($"[master] Error cargando patrón {tag}: {ex.Message}");
                return null;
            }
        }

        private async Task<Workflow.RoiExportResult?> ExportCurrentRoiCanonicalAsync()
        {
            RoiModel? roiImage = null;
            BitmapSource? imageSource = null;

            await Dispatcher.InvokeAsync(() =>
            {
                // Usar siempre la geometría del slot activo (o buffer del mismo slot) para evitar
                // mezclar ROIs cuando _layout.Inspection se reutiliza temporalmente.
                RoiModel? candidate = null;
                string candidateSource = "none";
                int slotIndex = ResolveActiveInspectionIndex();

                if (_tmpBuffer != null && _tmpBuffer.Role == RoiRole.Inspection)
                {
                    var tmpIdx = TryParseInspectionIndex(_tmpBuffer);
                    if (tmpIdx.HasValue && tmpIdx.Value == slotIndex)
                    {
                        candidate = _tmpBuffer.Clone();
                        candidateSource = "tmpBuffer";
                    }
                    else if (tmpIdx.HasValue)
                    {
                        LogDebug($"[Eval] tmpBuffer slot mismatch tmpIdx={tmpIdx.Value} active={slotIndex}");
                    }
                }

                if (candidate == null)
                {
                    var slotModel = GetInspectionSlotModel(slotIndex);
                    if (slotModel != null)
                    {
                        candidate = slotModel.Clone();
                        candidateSource = $"slot:{slotIndex}";
                    }
                }

                if (candidate == null && _layout?.Inspection != null)
                {
                    candidate = _layout.Inspection.Clone();
                    candidateSource = "layoutInspection";
                }

                if (candidate == null)
                {
                    candidate = BuildCurrentRoiModel(RoiRole.Inspection);
                    if (candidate != null)
                    {
                        candidateSource = "buildCurrent";
                    }
                }

                if (candidate != null)
                {
                    roiImage = LooksLikeCanvasCoords(candidate)
                        ? CanvasToImage(candidate)
                        : candidate.Clone();

                    try
                    {
                        var message = string.Format(
                            CultureInfo.InvariantCulture,
                            "[EVAL-EXPORT] slot={0} source={1} roiId='{2}' L={3:0.###} T={4:0.###} W={5:0.###} H={6:0.###} CX={7:0.###} CY={8:0.###} R={9:0.###} Rin={10:0.###} Ang={11:0.###}",
                            slotIndex,
                            candidateSource,
                            roiImage.Id ?? "<null>",
                            roiImage.Left,
                            roiImage.Top,
                            roiImage.Width,
                            roiImage.Height,
                            roiImage.CX,
                            roiImage.CY,
                            roiImage.R,
                            roiImage.RInner,
                            roiImage.AngleDeg);
                        AppendLog(message);
                    }
                    catch
                    {
                        // logging best-effort
                    }
                }
                else
                {
                    LogDebug($"[Eval] No ROI candidate available for slot {slotIndex}.");
                }

                var src = _currentImageSource ?? ImgMain?.Source as BitmapSource;
                if (src != null)
                {
                    if (!src.IsFrozen)
                    {
                        try
                        {
                            if (src.CanFreeze)
                            {
                                src.Freeze();
                            }
                            else
                            {
                                var clone = src.Clone();
                                if (clone.CanFreeze && !clone.IsFrozen)
                                {
                                    clone.Freeze();
                                }
                                src = clone;
                            }
                        }
                        catch
                        {
                            try
                            {
                                var clone = src.Clone();
                                if (clone.CanFreeze && !clone.IsFrozen)
                                {
                                    clone.Freeze();
                                }
                                src = clone;
                            }
                            catch
                            {
                                // swallow clone issues
                            }
                        }
                    }

                    imageSource = src;
                }
            });

            if (roiImage == null)
            {
                Snack($"No hay ROI de inspección definido."); // CODEX: string interpolation compatibility.
                return null;
            }

            if (imageSource == null)
            {
                Snack($"Carga primero una imagen válida."); // CODEX: string interpolation compatibility.
                LogDebug("[Eval] currentImageSource is NULL. Aborting export.");
                return null;
            }

            return await Task.Run(() =>
            {
                var roiForExport = roiImage!.Clone();
                var roiRect = RoiRectImageSpace(roiForExport);

                using var srcMat = BitmapSourceConverter.ToMat(imageSource);
                if (srcMat.Empty())
                {
                    LogDebug("[Eval] Source Mat empty.");
                    return null;
                }

                if (!RoiCropUtils.TryBuildRoiCropInfo(roiForExport, out var cropInfo))
                {
                    LogDebug("[Eval] TryBuildRoiCropInfo failed for inspection ROI.");
                    return null;
                }

                if (!RoiCropUtils.TryGetRotatedCrop(srcMat, cropInfo, roiForExport.AngleDeg, out var cropMat, out var cropRect))
                {
                    LogDebug("[Eval] TryGetRotatedCrop failed for inspection ROI.");
                    return null;
                }

                Mat? maskMat = null;
                Mat? encodeMat = null;
                try
                {
                    bool needsMask = roiForExport.Shape == RoiShape.Circle || roiForExport.Shape == RoiShape.Annulus;
                    if (needsMask)
                    {
                        maskMat = RoiCropUtils.BuildRoiMask(cropInfo, cropRect);
                    }

                    encodeMat = RoiCropUtils.ConvertCropToBgra(cropMat, maskMat);
                    if (!Cv2.ImEncode(".png", encodeMat, out var pngBytes) || pngBytes == null || pngBytes.Length == 0)
                    {
                        LogDebug("[Eval] Failed to encode ROI crop to PNG.");
                        return null;
                    }

                    var cropBitmap = BitmapSourceConverter.ToBitmapSource(encodeMat);
                    if (cropBitmap.CanFreeze && !cropBitmap.IsFrozen)
                    {
                        try { cropBitmap.Freeze(); } catch { }
                    }

                    var imgHash = HashSHA256(GetPixels(imageSource));
                    var cropHash = HashSHA256(GetPixels(cropBitmap));
                    var cropRectInt = new System.Windows.Int32Rect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);

                    LogDebug($"[Eval] imgHash={imgHash} cropHash={cropHash} rect=({cropRectInt.X},{cropRectInt.Y},{cropRectInt.Width},{cropRectInt.Height}) roiRect=({roiRect.X},{roiRect.Y},{roiRect.Width},{roiRect.Height}) angle={roiForExport.AngleDeg:0.##}");

                    var shapeJson = BuildShapeJsonForExport(roiForExport, cropInfo, cropRect);
                    return new Workflow.RoiExportResult(pngBytes, shapeJson, roiForExport.Clone(), imgHash, cropHash, cropRectInt);
                }
                finally
                {
                    if (encodeMat != null && !ReferenceEquals(encodeMat, cropMat))
                    {
                        encodeMat.Dispose();
                    }
                    maskMat?.Dispose();
                    cropMat.Dispose();
                }
            }).ConfigureAwait(false);
        }

        private static string BuildShapeJsonForExport(RoiModel roi, RoiCropInfo cropInfo, Cv.Rect cropRect)
        {
            double w = cropRect.Width;
            double h = cropRect.Height;

            object shape = roi.Shape switch
            {
                RoiShape.Rectangle => new { kind = "rect", x = 0, y = 0, w, h },
                RoiShape.Circle => new
                {
                    kind = "circle",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = Math.Min(w, h) / 2.0
                },
                RoiShape.Annulus => new
                {
                    kind = "annulus",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = ResolveOuterRadiusPx(cropInfo, cropRect),
                    r_inner = ResolveInnerRadiusPx(cropInfo, cropRect)
                },
                _ => new { kind = "rect", x = 0, y = 0, w, h }
            };

            return JsonSerializer.Serialize(shape);
        }

        private static double ResolveOuterRadiusPx(RoiCropInfo cropInfo, Cv.Rect cropRect)
        {
            double outer = cropInfo.Radius > 0 ? cropInfo.Radius : Math.Max(cropInfo.Width, cropInfo.Height) / 2.0;
            double scale = Math.Min(
                cropRect.Width / Math.Max(cropInfo.Width, 1.0),
                cropRect.Height / Math.Max(cropInfo.Height, 1.0));
            double result = outer * scale;
            if (result <= 0)
            {
                result = Math.Min(cropRect.Width, cropRect.Height) / 2.0;
            }

            return result;
        }

        private static double ResolveInnerRadiusPx(RoiCropInfo cropInfo, Cv.Rect cropRect)
        {
            if (cropInfo.Shape != RoiShape.Annulus)
            {
                return 0;
            }

            double scale = Math.Min(
                cropRect.Width / Math.Max(cropInfo.Width, 1.0),
                cropRect.Height / Math.Max(cropInfo.Height, 1.0));
            double inner = Math.Clamp(cropInfo.InnerRadius, 0, cropInfo.Radius);
            double result = inner * scale;
            if (result < 0)
            {
                result = 0;
            }

            return result;
        }

        private async Task<bool> VerifyPathsAndConnectivityAsync()
        {
            // CODEX: string interpolation compatibility.
            AppendLog($"== VERIFY: comenzando verificación de paths/IP ==");
            bool ok = true;

            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            {
                Snack($"Imagen no válida o no existe. Carga una imagen primero."); // CODEX: string interpolation compatibility.
                ok = false;
            }
            else
            {
                try
                {
                    using var bmp = new System.Drawing.Bitmap(_currentImagePathWin);
                    AppendLog($"[VERIFY] Imagen OK: {bmp.Width}x{bmp.Height}");
                }
                catch (Exception ex)
                {
                    Snack($"No se pudo abrir la imagen: {ex.Message}"); // CODEX: string interpolation compatibility.
                    ok = false;
                }
            }

            try
            {
                var uri = new Uri(BackendAPI.BaseUrl);
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                {
                    Snack($"BaseUrl no es http/https"); // CODEX: string interpolation compatibility.
                    ok = false;
                }
                AppendLog($"[VERIFY] BaseUrl OK: {uri}");
            }
            catch (Exception ex)
            {
                Snack($"BaseUrl inválida: {ex.Message}"); // CODEX: string interpolation compatibility.
                ok = false;
            }

            var url = BackendAPI.BaseUrl.TrimEnd('/') + "/" + BackendAPI.TrainStatusEndpoint.TrimStart('/');
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await hc.GetAsync(url).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                AppendLog($"[VERIFY] GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (resp.IsSuccessStatusCode)
                {
                    AppendLog($"[VERIFY] train_status body (tail): {body.Substring(0, Math.Min(body.Length, 200))}");
                }
                else
                {
                    Snack($"El backend respondió {resp.StatusCode} en /train_status");
                }
            }
            catch (Exception ex)
            {
                Snack($"No hay conexión con el backend: {ex.Message}"); // CODEX: string interpolation compatibility.
                ok = false;
            }

            // CODEX: string interpolation compatibility.
            AppendLog($"== VERIFY: fin verificación ==");
            return ok;
        }
        private void LogPathSnapshot()
        {
            // CODEX: string interpolation compatibility.
            AppendLog($"========== PATH SNAPSHOT ==========");
            try
            {
                AppendLog($"[CFG] BaseUrl={BackendAPI.BaseUrl}");
                AppendLog($"[CFG] InferEndpoint={BackendAPI.InferEndpoint} TrainStatusEndpoint={BackendAPI.TrainStatusEndpoint}");
                AppendLog($"[CFG] DefaultMmPerPx={BackendAPI.DefaultMmPerPx:0.###}");
                var exists = !string.IsNullOrWhiteSpace(_currentImagePathWin) && File.Exists(_currentImagePathWin);
                AppendLog($"[IMG] _currentImagePathWin='{_currentImagePathWin}'  exists={exists}");

                if (_layout.Master1Pattern != null)
                    AppendLog($"[ROI] M1 Pattern  {DescribeRoi(_layout.Master1Pattern)}");
                if (_layout.Master1Search != null)
                    AppendLog($"[ROI] M1 Search   {DescribeRoi(_layout.Master1Search)}");
                if (_layout.Master2Pattern != null)
                    AppendLog($"[ROI] M2 Pattern  {DescribeRoi(_layout.Master2Pattern)}");
                if (_layout.Master2Search != null)
                    AppendLog($"[ROI] M2 Search   {DescribeRoi(_layout.Master2Search)}");
                if (_layout.Inspection != null)
                    AppendLog($"[ROI] Inspection  {DescribeRoi(_layout.Inspection)}");

                AppendLog($"[PRESET] Feature='{_preset.Feature}' Thr={_preset.MatchThr} RotRange={_preset.RotRange} Scale=[{_preset.ScaleMin:0.###},{_preset.ScaleMax:0.###}] MmPerPx={_preset.MmPerPx:0.###}");
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[SNAPSHOT] ERROR: {ex.Message}");
            }
            // CODEX: string interpolation compatibility.
            AppendLog($"===================================");
        }

        private async void BtnAnalyzeMaster_Click(object sender, RoutedEventArgs e)
        {
            // CODEX: string interpolation compatibility.
            AppendLog($"[UI] BtnAnalyzeMaster_Click");

            // 1) (opcional) snapshot/verificación que ya tienes
            LogPathSnapshot();
            if (!await VerifyPathsAndConnectivityAsync())
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[VERIFY] Falló verificación. Abortando Analyze.");
                return;
            }

            // 2) Validaciones rápidas
            if (string.IsNullOrWhiteSpace(_currentImagePathWin))
            {
                Snack($"No hay imagen actual"); // CODEX: string interpolation compatibility.
                return;
            }
            if (_layout.Master1Pattern == null || _layout.Master1Search == null ||
                _layout.Master2Pattern == null || _layout.Master2Search == null)
            {
                Snack($"Faltan ROIs Master"); // CODEX: string interpolation compatibility.
                return;
            }

            // 4) Leer preset desde la UI
            SyncPresetFromUI();

            // 5) Lanzar análisis
            _ = AnalyzeMastersAsync();
        }
        // ====== Overlay persistente + Adorner ======
        private void OnRoiChanged(Shape shape, RoiModel roi)
        {
            TryAutosaveLayout("adorner change");
            AppendLog($"[adorner] ROI actualizado: {roi.Role} => {DescribeRoi(roi)}");
        }


        // ====== Preset/Layout ======
        private static double ParseDoubleOrDefault(string? text, double defaultValue)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return value;
            return defaultValue;
        }

        private static string NormalizeFeature(string? feature)
        {
            return string.IsNullOrWhiteSpace(feature)
                ? "auto"
                : feature.Trim().ToLowerInvariant();
        }

        private static string GetFeatureLabel(object? item)
        {
            return item switch
            {
                ComboBoxItem cbi => (cbi.Content?.ToString() ?? string.Empty).Trim(),
                _ => (item?.ToString() ?? string.Empty).Trim()
            };
        }

        private string ReadFeatureFromUI()
        {
            return NormalizeFeature(GetFeatureLabel(ComboFeature.SelectedItem));
        }

        private void SetFeatureSelection(string feature)
        {
            string normalized = NormalizeFeature(feature);
            foreach (var item in ComboFeature.Items)
            {
                if (NormalizeFeature(GetFeatureLabel(item)) == normalized)
                {
                    ComboFeature.SelectedItem = item;
                    return;
                }
            }
            if (ComboFeature.Items.Count > 0 && ComboFeature.SelectedIndex < 0)
            {
                ComboFeature.SelectedIndex = 0;
            }
        }

        private void ApplyPresetToUI(Preset preset)
        {
            preset.Feature = NormalizeFeature(preset.Feature);
            TxtThr.Text = preset.MatchThr.ToString(CultureInfo.InvariantCulture);
            TxtRot.Text = preset.RotRange.ToString(CultureInfo.InvariantCulture);
            TxtSMin.Text = preset.ScaleMin.ToString(CultureInfo.InvariantCulture);
            TxtSMax.Text = preset.ScaleMax.ToString(CultureInfo.InvariantCulture);
            SetFeatureSelection(preset.Feature);
        }

        private void ApplyInspectionPresetToUI(Preset preset)
        {
            if (preset == null || _layout == null)
            {
                return;
            }

            bool applied = false;

            for (int slot = 1; slot <= 2; slot++)
            {
                var roi = PresetManager.GetInspection(preset, slot, line => RoiDiag(line));
                if (roi == null)
                {
                    continue;
                }

                NormalizeInspectionRoi(roi, slot);
                RoiDiag(string.Format(
                    CultureInfo.InvariantCulture,
                    "[preset-apply] slot={0} id={1} shape={2} base=({3}x{4})",
                    slot,
                    roi.Id ?? "<null>",
                    roi.Shape,
                    roi.BaseImgW,
                    roi.BaseImgH));

                SetInspectionSlotModel(slot, roi, updateActive: false);
                applied = true;
            }

            if (applied)
            {
                RefreshInspectionRoiSlots();
                RedrawAllRois();
                DumpUiShapesMap("preset-apply");
            }
        }

        private void SyncPresetFromUI()
        {
            _preset.MatchThr = (int)Math.Round(ParseDoubleOrDefault(TxtThr.Text, _preset.MatchThr));
            _preset.RotRange = (int)Math.Round(ParseDoubleOrDefault(TxtRot.Text, _preset.RotRange));
            _preset.ScaleMin = ParseDoubleOrDefault(TxtSMin.Text, _preset.ScaleMin);
            _preset.ScaleMax = ParseDoubleOrDefault(TxtSMax.Text, _preset.ScaleMax);
            _preset.Feature = ReadFeatureFromUI();
        }

        private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
        {
            SyncPresetFromUI();
            var s = new SaveFileDialog { Filter = "Preset JSON|*.json", FileName = "preset.json" };
            if (s.ShowDialog() == true) PresetManager.Save(_preset, s.FileName);
            Snack($"Preset guardado."); // CODEX: string interpolation compatibility.
        }

        private void BtnLoadPreset_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Seleccionar preset",
                Filter = "Preset JSON (*.json)|*.json",
                InitialDirectory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets")
            };

            if (dlg.ShowDialog() == true)
            {
                LoadPresetFromFile(dlg.FileName);
            }
        }

        private void LoadPresetFromFile(string filePath)
        {
            try
            {
                var preset = PresetSerializer.LoadMastersPreset(filePath);
                ApplyMastersPreset(preset);
                OnPresetLoaded();
                Snack($"Preset cargado."); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo cargar el preset: {ex.Message}", "Preset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyMastersPreset(MastersPreset preset)
        {
            if (preset == null)
            {
                return;
            }

            _preset.MmPerPx = preset.mm_per_px;
            ChkScaleLock.IsChecked = preset.scale_lock;
            ChkUseLocalMatcher.IsChecked = preset.use_local_matcher;

            _layout ??= new MasterLayout();

            _layout.Master1Pattern = ConvertDtoToModel(preset.Master1, RoiRole.Master1Pattern);
            _layout.Master1Search = ConvertDtoToModel(preset.Master1Inspection, RoiRole.Master1Search);
            _layout.Master2Pattern = ConvertDtoToModel(preset.Master2, RoiRole.Master2Pattern);
            _layout.Master2Search = ConvertDtoToModel(preset.Master2Inspection, RoiRole.Master2Search);

            EnsureInspectionDatasetStructure();
            _workflowViewModel?.SetInspectionRoisCollection(_layout?.InspectionRois);
            RedrawAllRois();
        }

        private static RoiModel? ConvertDtoToModel(RoiDto? dto, RoiRole role)
        {
            if (dto == null)
            {
                return null;
            }

            var model = new RoiModel
            {
                Role = role,
                Shape = MapShape(dto.Shape),
                AngleDeg = dto.AngleDeg
            };

            switch (model.Shape)
            {
                case RoiShape.Rectangle:
                    model.X = dto.CenterX;
                    model.Y = dto.CenterY;
                    model.Width = dto.Width;
                    model.Height = dto.Height;
                    break;
                case RoiShape.Circle:
                    model.CX = dto.CenterX;
                    model.CY = dto.CenterY;
                    model.R = dto.Width > 0 ? dto.Width / 2.0 : dto.Height / 2.0;
                    model.Width = dto.Width;
                    model.Height = dto.Height;
                    break;
                case RoiShape.Annulus:
                    model.CX = dto.CenterX;
                    model.CY = dto.CenterY;
                    model.R = dto.Width > 0 ? dto.Width / 2.0 : dto.Height / 2.0;
                    if (dto.InnerRadius.HasValue)
                    {
                        model.RInner = dto.InnerRadius.Value;
                    }
                    else if (dto.InnerDiameter > 0)
                    {
                        model.RInner = dto.InnerDiameter / 2.0;
                    }
                    else
                    {
                        model.RInner = dto.Height > 0 ? dto.Height / 2.0 : 0.0;
                    }
                    model.Width = dto.Width;
                    model.Height = dto.Height;
                    break;
            }

            return model;
        }

        private static RoiShape MapShape(string? shape)
        {
            return (shape ?? string.Empty).ToLowerInvariant() switch
            {
                "circle" => RoiShape.Circle,
                "annulus" => RoiShape.Annulus,
                _ => RoiShape.Rectangle
            };
        }

        private void BtnSaveLayout_Click(object sender, RoutedEventArgs e)
        {
            var preset = _preset;
            if (preset == null || _layout == null)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[layout-save] blocked (layout/preset null)");
                Snack($"Layout no inicializado."); // CODEX: string interpolation compatibility.
                return;
            }

            using var layoutIo = ViewModel?.BeginLayoutIo("save-layout");

            var targetPath = MasterLayoutManager.GetDefaultPath(preset);
            AppendLog($"[save] requested reason=btn-save path={targetPath} suppress={_suppressSaves}");

            if (_suppressSaves > 0)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[save] suppressed programmatic change");
                return;
            }

            if (!MastersReady(_layout))
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[layout-save] blocked (Master1 incompleto)");
                MessageBox.Show(
                    "Dibuja/guarda Master 1 (Pattern y Search) antes de 'Save Layout'.",
                    "Save Layout",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(_activeEditableRoiId))
                {
                    CaptureCurrentUiShapeIntoSlot(_activeEditableRoiId);
                }

                SyncActiveInspectionToLayout();

                var snapshot = BuildLayoutSnapshotForSave();
                // CODEX: string interpolation compatibility.
                AppendLog($"[layout] Saving from ROI model snapshot (manual button)"); // CODEX: confirm manual saves never use batch heatmap geometry.
                MasterLayoutManager.Save(preset, snapshot);
                AppendLog($"[layout-save] ok -> {targetPath}");
                Snack($"Layout guardado ✅"); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                AppendLog($"[layout-save] ERROR: {ex.Message}");
                Snack($"❌ Error guardando layout (ver log)."); // CODEX: string interpolation compatibility.
            }
        }

        private void SaveCurrentLayoutAs_Click(object sender, RoutedEventArgs e)
        {
            if (_layout == null)
                return;

            try
            {
                // 1) First, save using the standard mechanism so that last.layout.json and the
                //    usual timestamped snapshot are updated.
                MasterLayoutManager.Save(_preset, _layout);

                // 2) Ask the user where to save this layout as a .layout file.
                var dlg = new SaveFileDialog
                {
                    Title = "Save layout as",
                    Filter = "Layout files (*.layout.json)|*.layout.json",
                    DefaultExt = ".layout.json",
                    AddExtension = true,
                    FileName = "layout.layout.json"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    var finalPath = MasterLayoutManager.EnsureLayoutJsonExtension(dlg.FileName);

                    MasterLayoutManager.SaveAs(_preset, _layout, finalPath);

                    AppendLog($"[layout] Saved as '{finalPath}'");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[layout] Save-as failed: {ex}");
                MessageBox.Show(this,
                    "Failed to save layout.\n" + ex.Message,
                    "Save layout",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private MasterLayout BuildLayoutSnapshotForSave()
        {
            var source = _layout ?? new MasterLayout();
            var snapshot = new MasterLayout
            {
                Master1Pattern = CloneRoiForSave(source.Master1Pattern),
                Master1PatternImagePath = source.Master1PatternImagePath,
                Master1Search = CloneRoiForSave(source.Master1Search),
                Master2Pattern = CloneRoiForSave(source.Master2Pattern),
                Master2PatternImagePath = source.Master2PatternImagePath,
                Master2Search = CloneRoiForSave(source.Master2Search),
                Inspection = CloneRoiForSave(source.Inspection),
                InspectionBaseline = CloneRoiForSave(source.InspectionBaseline),
                Inspection1 = CloneRoiForSave(source.Inspection1),
                Inspection2 = CloneRoiForSave(source.Inspection2),
                Inspection3 = CloneRoiForSave(source.Inspection3),
                Inspection4 = CloneRoiForSave(source.Inspection4)
            };

            snapshot.Analyze.PosTolPx = source.Analyze?.PosTolPx ?? _analyzePosTolPx;
            snapshot.Analyze.AngTolDeg = source.Analyze?.AngTolDeg ?? _analyzeAngTolDeg;
            snapshot.Analyze.ScaleLock = source.Analyze?.ScaleLock ?? _scaleLock;
            snapshot.Analyze.UseLocalMatcher = source.Analyze?.UseLocalMatcher ?? (ChkUseLocalMatcher?.IsChecked == true);

            snapshot.Ui.HeatmapOverlayOpacity = _heatmapOverlayOpacity;
            snapshot.Ui.HeatmapGain = _heatmapGain;
            snapshot.Ui.HeatmapGamma = _heatmapGamma;

            snapshot.InspectionBaselinesByImage.Clear();
            if (source.InspectionBaselinesByImage != null)
            {
                foreach (var kv in source.InspectionBaselinesByImage)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0)
                    {
                        continue;
                    }

                    var clones = kv.Value
                        .Select(CloneRoiForSave)
                        .Where(r => r != null)
                        .Cast<RoiModel>()
                        .ToList();

                    if (clones.Count > 0)
                    {
                        snapshot.InspectionBaselinesByImage[kv.Key] = clones;
                    }
                }
            }

            snapshot.InspectionRois.Clear();
            if (source.InspectionRois != null)
            {
                foreach (var cfg in source.InspectionRois)
                {
                    if (cfg == null)
                    {
                        continue;
                    }

                    snapshot.InspectionRois.Add(CloneInspectionConfigForSave(cfg));
                }
            }

            return snapshot;
        }

        private RoiModel? CloneRoiForSave(RoiModel? roi)
        {
            if (roi == null)
            {
                return null;
            }

            var clone = roi.Clone();
            if (_imgW > 0 && _imgH > 0)
            {
                clone.BaseImgW ??= _imgW;
                clone.BaseImgH ??= _imgH;
            }

            return clone;
        }

        private InspectionRoiConfig CloneInspectionConfigForSave(InspectionRoiConfig cfg)
        {
            var clone = new InspectionRoiConfig(cfg.Index)
            {
                Id = cfg.Id,
                Name = cfg.Name,
                Enabled = cfg.Enabled,
                ModelKey = cfg.ModelKey,
                DatasetPath = cfg.DatasetPath,
                TrainMemoryFit = cfg.TrainMemoryFit,
                CalibratedThreshold = cfg.CalibratedThreshold,
                ThresholdDefault = cfg.ThresholdDefault,
                Shape = cfg.Shape,
                BaseImgW = cfg.BaseImgW,
                BaseImgH = cfg.BaseImgH,
                HasFitOk = cfg.HasFitOk,
                DatasetReady = cfg.DatasetReady,
                Threshold = cfg.Threshold,
                LastScore = cfg.LastScore,
                LastResultOk = cfg.LastResultOk,
                LastEvaluatedAt = cfg.LastEvaluatedAt,
                DatasetStatus = cfg.DatasetStatus,
                DatasetOkCount = cfg.DatasetOkCount,
                DatasetKoCount = cfg.DatasetKoCount
            };

            return clone;
        }

        private void BtnLoadLayout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel?.IsLayoutPersistenceLocked == true)
                {
                    AppendLog("[batch] layout-persist:SKIP");
                    return;
                }

                CancelMasterEditing(redraw: false);

                var dir = MasterLayoutManager.GetLayoutsFolder(_preset);
                Directory.CreateDirectory(dir);

                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    InitialDirectory = dir,
                    Filter = "Layout (*.layout.json)|*.layout.json",
                    FileName = "last.layout.json",
                    Title = "Select layout"
                };

                if (dlg.ShowDialog(this) != true)
                    return;

                using var layoutIo = ViewModel?.BeginLayoutIo("load-layout");

                var selectedPath = MasterLayoutManager.EnsureLayoutJsonExtension(dlg.FileName);
                var loaded = MasterLayoutManager.LoadFromFile(selectedPath);
                ApplyLayout(loaded ?? new MasterLayout(), "manual-load");
                _editModeActive = false;
                Snack($"Layout loaded: {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                Snack($"Load Layout error: {ex.Message}"); // CODEX: string interpolation compatibility.
            }
        }

        // ====== Logs / Polling ======
        private void InitTrainPollingTimer()
        {
            _trainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _trainTimer.Tick += async (s, e) =>
            {
                try
                {
                    var url = BackendAPI.BaseUrl.TrimEnd('/') + BackendAPI.TrainStatusEndpoint;
                    using var hc = new System.Net.Http.HttpClient();
                    var resp = await hc.GetAsync(url);
                    var text = await resp.Content.ReadAsStringAsync();
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[train_status] {text.Trim()}");
                }
                catch (Exception ex)
                {
                    // CODEX: string interpolation compatibility.
                    AppendLog($"[train_status] ERROR {ex.Message}");
                }
            };
            // _trainTimer.Start(); // opcional
        }

        private void Snack(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var payload = "[SNACK] " + message;

            try
            {
                AppendLog(payload);
            }
            catch
            {
                // Never allow snack logging to throw.
            }

            try
            {
                GuiLog.Info(payload);
            }
            catch
            {
                // Ignore logging failures (e.g., during early boot).
            }
        }

        private void SyncModelFromShape(Shape shape)
        {
            if (shape.Tag is not RoiModel roiCanvas) return;

            var x = Canvas.GetLeft(shape);
            var y = Canvas.GetTop(shape);
            var w = shape.Width;
            var h = shape.Height;

            double? renderAngle = null;
            if (shape.RenderTransform is RotateTransform rotateTransform)
            {
                renderAngle = rotateTransform.Angle;
            }
            else if (shape.RenderTransform is TransformGroup transformGroup)
            {
                var rotate = transformGroup.Children.OfType<RotateTransform>().FirstOrDefault();
                if (rotate != null)
                    renderAngle = rotate.Angle;
            }

            if (renderAngle.HasValue)
            {
                roiCanvas.AngleDeg = renderAngle.Value;
            }

            if (shape is System.Windows.Shapes.Rectangle)
            {
                roiCanvas.Shape = RoiShape.Rectangle;
                roiCanvas.Width = w;
                roiCanvas.Height = h;
                roiCanvas.Left = x;
                roiCanvas.Top = y;

                // Centro correcto del bounding box
                double cx = x + w / 2.0;
                double cy = y + h / 2.0;
                roiCanvas.CX = cx;
                roiCanvas.CY = cy;
                roiCanvas.X = cx;   // En este proyecto X,Y representan el centro
                roiCanvas.Y = cy;

                roiCanvas.R = Math.Max(roiCanvas.Width, roiCanvas.Height) / 2.0;
                roiCanvas.RInner = 0;
            }
            else if (shape is AnnulusShape annulusShape)
            {
                double radius = Math.Max(w, h) / 2.0;

                roiCanvas.Shape = RoiShape.Annulus;
                roiCanvas.Width = w;
                roiCanvas.Height = h;
                roiCanvas.R = radius;
                roiCanvas.Left = x;
                roiCanvas.Top = y;

                // Centro correcto del bounding box
                double cx = x + w / 2.0;
                double cy = y + h / 2.0;
                roiCanvas.CX = cx;
                roiCanvas.CY = cy;
                roiCanvas.X = cx;   // X,Y = centro
                roiCanvas.Y = cy;

                double inner = annulusShape.InnerRadius;
                double maxInner = radius > 0 ? radius : Math.Max(w, h) / 2.0;
                inner = Math.Max(0, Math.Min(inner, maxInner));
                roiCanvas.RInner = inner;
                annulusShape.InnerRadius = inner;
            }
            else if (shape is System.Windows.Shapes.Ellipse)
            {
                double radius = Math.Max(w, h) / 2.0;

                roiCanvas.Shape = RoiShape.Circle;
                roiCanvas.Width = w;
                roiCanvas.Height = h;
                roiCanvas.R = radius;
                roiCanvas.Left = x;
                roiCanvas.Top = y;

                // Centro correcto del bounding box
                double cx = x + w / 2.0;
                double cy = y + h / 2.0;
                roiCanvas.CX = cx;
                roiCanvas.CY = cy;
                roiCanvas.X = cx;   // X,Y = centro
                roiCanvas.Y = cy;

                roiCanvas.RInner = 0;
            }

            UpdateRoiLabelPosition(shape);

            var roiPixel = CanvasToImage(roiCanvas);

            if (ReferenceEquals(shape, _previewShape))
            {
                _tmpBuffer = roiPixel.Clone();
            }
            else
            {
                UpdateLayoutFromPixel(roiPixel);
            }

            UpdateOverlayFromPixelModel(roiPixel);

            AppendLog($"[model] sync {roiPixel.Role} => {DescribeRoi(roiPixel)}");
        }

        private void UpdateLayoutFromPixel(RoiModel roiPixel)
        {
            var clone = roiPixel.Clone();

            bool hadRoiBefore = roiPixel.Role switch
            {
                RoiRole.Master1Pattern => _layout.Master1Pattern != null,
                RoiRole.Master1Search => _layout.Master1Search != null,
                RoiRole.Master2Pattern => _layout.Master2Pattern != null,
                RoiRole.Master2Search => _layout.Master2Search != null,
                RoiRole.Inspection => _layout.Inspection != null,
                _ => true
            };

            switch (roiPixel.Role)
            {
                case RoiRole.Master1Pattern:
                    _layout.Master1Pattern = clone;
                    break;
                case RoiRole.Master1Search:
                    _layout.Master1Search = clone;
                    break;
                case RoiRole.Master2Pattern:
                    _layout.Master2Pattern = clone;
                    break;
                case RoiRole.Master2Search:
                    _layout.Master2Search = clone;
                    break;
                case RoiRole.Inspection:
                    _layout.Inspection = clone;
                    if (!_analysisViewActive)
                    {
                        SetInspectionBaseline(clone);
                    }
                    SyncCurrentRoiFromInspection(clone);
                    break;
            }

            if (!hadRoiBefore)
            {
                UpdateRoiVisibilityControls();
            }

            var currentRole = GetCurrentStateRole();
            if (currentRole.HasValue && roiPixel.Role == currentRole.Value)
            {
                _tmpBuffer = clone.Clone();
            }
        }

        private void ApplyPixelModelToCurrentRoi(RoiModel pixelModel)
        {
            if (pixelModel == null)
                return;

            if (pixelModel.Shape == RoiShape.Rectangle)
            {
                CurrentRoi.Shape = RoiShape.Rectangle;
                CurrentRoi.SetCenter(pixelModel.X, pixelModel.Y);
                CurrentRoi.Width = pixelModel.Width;
                CurrentRoi.Height = pixelModel.Height;
                CurrentRoi.R = Math.Max(CurrentRoi.Width, CurrentRoi.Height) / 2.0;
                CurrentRoi.RInner = 0;
            }
            else
            {
                var shape = pixelModel.Shape == RoiShape.Annulus ? RoiShape.Annulus : RoiShape.Circle;
                double radius = pixelModel.R > 0 ? pixelModel.R : Math.Max(pixelModel.Width, pixelModel.Height) / 2.0;
                if (radius <= 0)
                {
                    radius = Math.Max(CurrentRoi.R, Math.Max(CurrentRoi.Width, CurrentRoi.Height) / 2.0);
                }

                double diameter = radius * 2.0;
                CurrentRoi.Shape = shape;
                CurrentRoi.SetCenter(pixelModel.CX, pixelModel.CY);
                CurrentRoi.Width = diameter;
                CurrentRoi.Height = diameter;
                CurrentRoi.R = radius;

                if (shape == RoiShape.Annulus)
                {
                    double innerCandidate = pixelModel.RInner;
                    if (innerCandidate <= 0 && CurrentRoi.RInner > 0)
                    {
                        innerCandidate = CurrentRoi.RInner;
                    }

                    double inner = innerCandidate > 0
                        ? AnnulusDefaults.ClampInnerRadius(innerCandidate, radius)
                        : AnnulusDefaults.ResolveInnerRadius(innerCandidate, radius);
                    CurrentRoi.RInner = inner;
                }
                else
                {
                    CurrentRoi.RInner = 0;
                }
            }

            CurrentRoi.AngleDeg = pixelModel.AngleDeg;

            var legend = ResolveRoiLabelText(pixelModel);
            if (!string.IsNullOrWhiteSpace(legend))
            {
                CurrentRoi.Legend = legend!;
            }
        }

        private void UpdateOverlayFromCurrentRoi()
        {
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // if (RoiOverlay == null)
            //     return;

            // RoiOverlay disabled: labels are now drawn on Canvas only
            // RoiOverlay.Roi = CurrentRoi;
            // RoiOverlay.InvalidateOverlay();
        }

        private void UpdateOverlayFromPixelModel(RoiModel pixelModel)
        {
            if (pixelModel == null)
                return;

            ApplyPixelModelToCurrentRoi(pixelModel);
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // UpdateOverlayFromCurrentRoi();
        }

        private void SyncCurrentRoiFromInspection(RoiModel inspectionPixel)
        {
            if (inspectionPixel == null) return;

            ApplyPixelModelToCurrentRoi(inspectionPixel);
            UpdateInspectionShapeRotation(CurrentRoi.AngleDeg);
            // RoiOverlay disabled: labels are now drawn on Canvas only
            // UpdateOverlayFromCurrentRoi();
            UpdateHeatmapOverlayLayoutAndClip();
        }

        private void InvalidateAdornerFor(Shape shape)
        {
            var layer = AdornerLayer.GetAdornerLayer(shape);
            if (layer == null) return;
            var ads = layer.GetAdorners(shape);
            if (ads == null) return;
            foreach (var ad in ads.OfType<RoiAdorner>())
            {
                ad.InvalidateArrange(); // recoloca thumbs en la nueva bbox
            }
        }

        // =============================================================
        // Guarda imágenes de depuración (patrón, búsqueda, inspección, full)
        // =============================================================
        private void SaveDebugRoiImages(RoiModel pattern, RoiModel search, RoiModel inspection)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !System.IO.File.Exists(_currentImagePathWin)) return;

                string baseDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(_currentImagePathWin) ?? "",
                    "debug_rois");
                System.IO.Directory.CreateDirectory(baseDir);

                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                System.Drawing.Bitmap? Crop(RoiModel roi)
                {
                    if (roi == null) return null;
                    var r = RoiToRect(roi);
                    using var src = new System.Drawing.Bitmap(_currentImagePathWin);
                    var rectInt = new System.Drawing.Rectangle(
                        (int)System.Math.Max(0, r.X),
                        (int)System.Math.Max(0, r.Y),
                        (int)System.Math.Min(r.Width, src.Width - r.X),
                        (int)System.Math.Min(r.Height, src.Height - r.Y));
                    if (rectInt.Width <= 0 || rectInt.Height <= 0) return null;
                    return src.Clone(rectInt, src.PixelFormat);
                }

                using (var patBmp = Crop(pattern))
                    patBmp?.Save(System.IO.Path.Combine(baseDir, $"pattern_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);
                using (var seaBmp = Crop(search))
                    seaBmp?.Save(System.IO.Path.Combine(baseDir, $"search_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);
                using (var insBmp = Crop(inspection))
                    insBmp?.Save(System.IO.Path.Combine(baseDir, $"inspection_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);

                using (var full = new System.Drawing.Bitmap(_currentImagePathWin))
                using (var g = System.Drawing.Graphics.FromImage(full))
                using (var penSearch = new System.Drawing.Pen(System.Drawing.Color.Yellow, 2))
                using (var penPattern = new System.Drawing.Pen(System.Drawing.Color.Cyan, 2))
                using (var penInspection = new System.Drawing.Pen(System.Drawing.Color.Lime, 2))
                using (var penCross = new System.Drawing.Pen(System.Drawing.Color.Magenta, 2))
                {
                    if (search != null) g.DrawRectangle(penSearch, ToDrawingRect(RoiToRect(search)));
                    if (pattern != null) g.DrawRectangle(penPattern, ToDrawingRect(RoiToRect(pattern)));
                    if (inspection != null) g.DrawRectangle(penInspection, ToDrawingRect(RoiToRect(inspection)));

                    if (pattern != null)
                    {
                        var r = RoiToRect(pattern);
                        var center = new System.Drawing.PointF((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2));
                        g.DrawLine(penCross, center.X - 20, center.Y, center.X + 20, center.Y);
                        g.DrawLine(penCross, center.X, center.Y - 20, center.X, center.Y + 20);
                    }

                    full.Save(System.IO.Path.Combine(baseDir, $"full_annotated_{ts}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                Snack($"Error guardando imágenes debug: {ex.Message}"); // CODEX: string interpolation compatibility.
            }
        }

        // Helper: convertir WPF Rect -> System.Drawing.Rectangle
        private static System.Drawing.Rectangle ToDrawingRect(SWRect r)
        {
            return new System.Drawing.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }

        // ====== Backend (multipart) helpers ======
        private static byte[] CropTemplatePng(string imagePathWin, SWRect rect)
        {
            using var bmp = new System.Drawing.Bitmap(imagePathWin);
            var x = Math.Max(0, (int)rect.X);
            var y = Math.Max(0, (int)rect.Y);
            var w = Math.Max(1, (int)rect.Width);
            var h = Math.Max(1, (int)rect.Height);
            if (x + w > bmp.Width) w = Math.Max(1, bmp.Width - x);
            if (y + h > bmp.Height) h = Math.Max(1, bmp.Height - y);
            using var crop = bmp.Clone(new System.Drawing.Rectangle(x, y, w, h), bmp.PixelFormat);
            using var ms = new System.IO.MemoryStream();
            crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }

        private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1) Validaciones básicas
                var currentFrame = bgrFrame;
                if (currentFrame == null || currentFrame.Empty())
                {
                    MessageBox.Show("No hay imagen cargada.");
                    return;
                }

                // 2) Obtener el crop YA ROTADO desde tu ROI actual
                //    Nota: se asume que tienes implementado GetRotatedCrop(Mat bgr)
                using var crop = GetRotatedCrop(currentFrame);
                if (crop == null || crop.Empty())
                {
                    MessageBox.Show("No se pudo obtener el recorte.");
                    return;
                }

                // 3) Codificar PNG (SIN 'using'; ImEncode devuelve byte[])
                byte[] cropPng = crop.ImEncode(".png");

                // 4) (Opcional) parámetros de anillo perfecto (annulus) si quieres usarlos
                //    Si no usas annulus, deja 'annulus' en null y 'maskPng' en null.
                object annulus = null;
                // bool useAnnulus = false; // habilítalo según tu UI
                // if (useAnnulus)
                // {
                //     annulus = new
                //     {
                //         cx = crop.Width / 2,
                //         cy = crop.Height / 2,
                //         ri = 40,
                //         ro = 60
                //     };
                // }
                // 5) Llamada al backend /infer con el ROI canónico
                string? shapeJson = annulus != null ? System.Text.Json.JsonSerializer.Serialize(annulus) : null;
                var request = new InferRequest("DemoRole", "DemoROI", BackendAPI.DefaultMmPerPx, cropPng)
                {
                    ShapeJson = shapeJson
                };
                var resp = await BackendAPI.InferAsync(request);

                // 6) Mostrar texto (si tienes el TextBlock en XAML)
                bool isNg = resp.threshold.HasValue && resp.score > resp.threshold.Value;
                _lastIsNg = isNg;
                if (ResultLabel != null)
                {
                    string thrText = resp.threshold.HasValue ? resp.threshold.Value.ToString("0.###") : "n/a";
                    ResultLabel.Text = isNg
                        ? $"NG (score={resp.score:F3} / thr {thrText})"
                        : $"OK (score={resp.score:F3} / thr {thrText})";
                    ResultLabel.Foreground = isNg ? Brushes.OrangeRed : Brushes.LightGreen;
                }

                // 7) Decodificar heatmap y pintarlo en el Image del XAML
                if (!string.IsNullOrWhiteSpace(resp.heatmap_png_base64))
                {
                    var heatBytes = Convert.FromBase64String(resp.heatmap_png_base64);
                    using var heat = OpenCvSharp.Cv2.ImDecode(heatBytes, OpenCvSharp.ImreadModes.Color);
                    using var heatGray = new Mat();
                    OpenCvSharp.Cv2.CvtColor(heat, heatGray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
                    _lastHeatmapRoi = HeatmapRoiModel.From(BuildCurrentRoiModel());
                    WireExistingHeatmapControls();

                    byte[] gray = new byte[heatGray.Rows * heatGray.Cols];
                    heatGray.GetArray(out byte[]? tmpGray);
                    if (tmpGray != null && tmpGray.Length == gray.Length)
                    {
                        gray = tmpGray;
                    }
                    else if (tmpGray != null)
                    {
                        Array.Copy(tmpGray, gray, Math.Min(gray.Length, tmpGray.Length));
                    }

                    _lastHeatmapGray = gray;
                    _lastHeatmapW = heatGray.Cols;
                    _lastHeatmapH = heatGray.Rows;

                    RebuildHeatmapOverlayFromCache();
                    SyncHeatmapBitmapFromOverlay();

                    if (_lastHeatmapBmp != null)
                    {
                        LogHeatmap($"Heatmap Source: {_lastHeatmapBmp.PixelWidth}x{_lastHeatmapBmp.PixelHeight}, Fmt={_lastHeatmapBmp.Format}");
                    }
                    else
                    {
                        LogHeatmap("Heatmap Source: <null>");
                    }

                    if (HeatmapOverlay != null) HeatmapOverlay.Opacity = _heatmapOverlayOpacity;

                    UpdateHeatmapOverlayLayoutAndClip();

                    _heatmapOverlayOpacity = HeatmapOverlay?.Opacity ?? _heatmapOverlayOpacity;
                }
                else
                {
                    _lastHeatmapBmp = null;
                    _lastHeatmapRoi = null;
                    LogHeatmap("Heatmap Source: <null>");
                    UpdateHeatmapOverlayLayoutAndClip();
                }

                // (Opcional) Log
                // AppendLog?.Invoke($"Infer -> score={resp.score:F3}, thr={resp.threshold?.ToString("0.###") ?? "n/a"}");
            }
            catch (Exception ex)
            {
                // (Opcional) Log
                // AppendLog?.Invoke("[Analyze] EX: " + ex.Message);
                MessageBox.Show("Error en Analyze: " + ex.Message);
            }
        }
        private System.Windows.Point GetCurrentRoiCenterOnCanvas()
        {
            return ImagePxToCanvasPt(CurrentRoi.X, CurrentRoi.Y);
        }

        private (double x, double y) GetCurrentRoiCornerImage(RoiCorner corner)
        {
            double halfW = CurrentRoi.Width / 2.0;
            double halfH = CurrentRoi.Height / 2.0;

            double rawOffsetX = corner switch
            {
                RoiCorner.TopLeft or RoiCorner.BottomLeft => -halfW,
                RoiCorner.TopRight or RoiCorner.BottomRight => halfW,
                _ => 0.0
            };

            double rawOffsetY = corner switch
            {
                RoiCorner.TopLeft or RoiCorner.TopRight => -halfH,
                RoiCorner.BottomLeft or RoiCorner.BottomRight => halfH,
                _ => 0.0
            };

            double angleRad = CurrentRoi.AngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);

            double rotatedX = rawOffsetX * cos - rawOffsetY * sin;
            double rotatedY = rawOffsetX * sin + rawOffsetY * cos;

            return (CurrentRoi.X + rotatedX, CurrentRoi.Y + rotatedY);
        }

        private string BuildShapeLogContext(Shape shape)
        {
            if (shape.Tag is RoiModel roiModel)
            {
                var (pivotCanvasX, pivotCanvasY, pivotLocalX, pivotLocalY, width, height) = GetShapePivotMetrics(shape, roiModel);
                return $"role={roiModel.Role} id={roiModel.Id} angle={roiModel.AngleDeg:0.##} pivotCanvas=({pivotCanvasX:0.##},{pivotCanvasY:0.##}) pivotLocal=({pivotLocalX:0.##},{pivotLocalY:0.##}) size=({width:0.##},{height:0.##})";
            }

            var tagText = shape.Tag != null ? shape.Tag.ToString() : "<null>";
            return $"shape={shape.GetType().Name} tag={tagText}";
        }

        private (double pivotCanvasX, double pivotCanvasY, double pivotLocalX, double pivotLocalY, double width, double height) GetShapePivotMetrics(Shape shape, RoiModel roiModel)
        {
            double width = !double.IsNaN(shape.Width) && shape.Width > 0 ? shape.Width : roiModel.Width;
            double height = !double.IsNaN(shape.Height) && shape.Height > 0 ? shape.Height : roiModel.Height;

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roiModel, width, height);
            double pivotLocalX = pivotLocal.X;
            double pivotLocalY = pivotLocal.Y;
            double pivotCanvasX = left + pivotLocalX;
            double pivotCanvasY = top + pivotLocalY;

            return (pivotCanvasX, pivotCanvasY, pivotLocalX, pivotLocalY, width, height);
        }

        private Shape? FindInspectionShapeOnCanvas()
        {
            if (CanvasROI == null)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[inspect] CanvasROI missing when searching inspection shape");
                return null;
            }

            if (_state == MasterState.DrawInspection && _previewShape != null)
            {
                AppendLog($"[inspect] using preview inspection shape {BuildShapeLogContext(_previewShape)}");
                return _previewShape;
            }

            var inspectionShapes = CanvasROI.Children
                .OfType<Shape>()
                .Where(shape =>
                    shape.Tag is RoiModel roi &&
                    roi.Role == RoiRole.Inspection)
                .ToList();

            var persisted = inspectionShapes.FirstOrDefault();
            if (persisted != null)
            {
                AppendLog($"[inspect] using persisted inspection shape {BuildShapeLogContext(persisted)}");
                return persisted;
            }

            int totalShapes = CanvasROI.Children.OfType<Shape>().Count();
            AppendLog($"[inspect] no inspection shape found (state={_state}, preview={_previewShape != null}, inspectionCount={inspectionShapes.Count}, totalShapes={totalShapes})");
            return null;
        }

        private void ApplyRoiRotationToShape(Shape shape, double angle)
        {
            if (shape.Tag is not RoiModel roiModel)
                return;

            roiModel.AngleDeg = angle;

            double width = !double.IsNaN(shape.Width) && shape.Width > 0 ? shape.Width : roiModel.Width;
            double height = !double.IsNaN(shape.Height) && shape.Height > 0 ? shape.Height : roiModel.Height;

            if (width <= 0 || height <= 0)
            {
                AppendLog($"[rotate] skip apply {roiModel.Role} width={width:0.##} height={height:0.##} angle={angle:0.##}");
                return;
            }

            var pivotLocal = RoiAdorner.GetRotationPivotLocalPoint(roiModel, width, height);
            double pivotLocalX = pivotLocal.X;
            double pivotLocalY = pivotLocal.Y;

            double left = Canvas.GetLeft(shape); if (double.IsNaN(left)) left = 0;
            double top = Canvas.GetTop(shape); if (double.IsNaN(top)) top = 0;
            double pivotCanvasX = left + pivotLocalX;
            double pivotCanvasY = top + pivotLocalY;

            if (shape.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = angle;
                rotate.CenterX = pivotLocalX;
                rotate.CenterY = pivotLocalY;
                InvalidateAdornerFor(shape);
            }
            else
            {
                shape.RenderTransform = new RotateTransform(angle, pivotLocalX, pivotLocalY);
                InvalidateAdornerFor(shape);
            }

            AppendLog($"[rotate] apply role={roiModel.Role} shape={roiModel.Shape} pivotLocal=({pivotLocalX:0.##},{pivotLocalY:0.##}) pivotCanvas=({pivotCanvasX:0.##},{pivotCanvasY:0.##}) angle={angle:0.##}");
        }

        private void UpdateInspectionShapeRotation(double angle)
        {
            var inspectionShape = FindInspectionShapeOnCanvas();
            if (inspectionShape == null)
            {
                AppendLog($"[rotate] update skip angle={angle:0.##} target=none");
                return;
            }

            AppendLog($"[rotate] update target angle={angle:0.##} {BuildShapeLogContext(inspectionShape)}");

            ApplyRoiRotationToShape(inspectionShape, angle);

            if (_layout?.Inspection != null)
            {
                _layout.Inspection.AngleDeg = angle;
            }
        }

        // === Helpers para mapear coordenadas ===
        public (int pw, int ph) GetImagePixelSize()
        {
            if (ImgMain?.Source is System.Windows.Media.Imaging.BitmapSource b)
                return (b.PixelWidth, b.PixelHeight);
            return (0, 0);
        }

        /// Rect donde realmente se pinta la imagen dentro del control ImgMain (con letterbox)
        public System.Windows.Rect GetImageDisplayRect()
        {
            double cw = ImgMain.ActualWidth;
            double ch = ImgMain.ActualHeight;
            var (pw, ph) = GetImagePixelSize();
            if (pw <= 0 || ph <= 0 || cw <= 0 || ch <= 0)
                return new System.Windows.Rect(0, 0, 0, 0);

            double scale = Math.Min(cw / pw, ch / ph);
            double w = pw * scale;
            double h = ph * scale;
            double x = (cw - w) * 0.5;
            double y = (ch - h) * 0.5;
            return new System.Windows.Rect(x, y, w, h);
        }

        /// Convierte un punto en píxeles de imagen -> punto en CanvasROI (coordenadas locales del Canvas)
        private SWPoint ImagePxToCanvasPt(double px, double py)
        {
            var transform = GetImageToCanvasTransform();
            double x = px * transform.sx + transform.offX;
            double y = py * transform.sy + transform.offY;
            return new SWPoint(x, y);
        }

        private SWPoint ImagePxToCanvasPt(CvPoint px)
        {
            return ImagePxToCanvasPt(px.X, px.Y);
        }




        private SWPoint CanvasToImage(SWPoint pCanvas)
        {
            var transform = GetImageToCanvasTransform();
            double scaleX = transform.sx;
            double scaleY = transform.sy;
            double offsetX = transform.offX;
            double offsetY = transform.offY;
            if (scaleX <= 0 || scaleY <= 0) return new SWPoint(0, 0);
            double ix = (pCanvas.X - offsetX) / scaleX;
            double iy = (pCanvas.Y - offsetY) / scaleY;
            return new SWPoint(ix, iy);
        }


        private RoiModel CanvasToImage(RoiModel roiCanvas)
        {
            var result = roiCanvas.Clone();
            var transform = GetImageToCanvasTransform();
            double scaleX = transform.sx;
            double scaleY = transform.sy;
            double offsetX = transform.offX;
            double offsetY = transform.offY;
            if (scaleX <= 0 || scaleY <= 0) return result;

            result.AngleDeg = roiCanvas.AngleDeg;
            double k = Math.Min(scaleX, scaleY);

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = (roiCanvas.X - offsetX) / scaleX;
                result.Y = (roiCanvas.Y - offsetY) / scaleY;
                result.Width = roiCanvas.Width / scaleX;
                result.Height = roiCanvas.Height / scaleY;

                result.CX = result.X;
                result.CY = result.Y;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = (roiCanvas.CX - offsetX) / scaleX;
                result.CY = (roiCanvas.CY - offsetY) / scaleY;

                result.R = roiCanvas.R / k;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiCanvas.RInner / k;

                result.X = result.CX;
                result.Y = result.CY;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }
            return result;
        }



        private RoiModel ImageToCanvas(RoiModel roiImage)
        {
            var result = roiImage.Clone();
            var transform = GetImageToCanvasTransform();
            double scaleX = transform.sx;
            double scaleY = transform.sy;
            double offsetX = transform.offX;
            double offsetY = transform.offY;
            if (scaleX <= 0 || scaleY <= 0) return result;

            result.AngleDeg = roiImage.AngleDeg;
            double k = Math.Min(scaleX, scaleY);

            if (result.Shape == RoiShape.Rectangle)
            {
                result.X = roiImage.X * scaleX + offsetX;
                result.Y = roiImage.Y * scaleY + offsetY;
                result.Width = roiImage.Width * scaleX;
                result.Height = roiImage.Height * scaleY;

                result.CX = result.X;
                result.CY = result.Y;
                result.R = Math.Max(result.Width, result.Height) / 2.0;
            }
            else
            {
                result.CX = roiImage.CX * scaleX + offsetX;
                result.CY = roiImage.CY * scaleY + offsetY;

                result.R = roiImage.R * k;
                if (result.Shape == RoiShape.Annulus)
                    result.RInner = roiImage.RInner * k;

                result.X = result.CX;
                result.Y = result.CY;
                result.Width = result.R * 2.0;
                result.Height = result.R * 2.0;
            }
            return result;
        }

        private void RecomputePreviewShapeAfterSync()
        {
            if (_previewShape == null) return;
            if (_tmpBuffer == null) return; // image-space ROI model while drawing

            // Map image-space preview ROI → canvas-space model
            var rc = ImageToCanvas(_tmpBuffer); // rc is canvas-space ROI

            // Position the preview shape according to rc
            if (_previewShape is System.Windows.Shapes.Rectangle)
            {
                Canvas.SetLeft(_previewShape, rc.Left);
                Canvas.SetTop(_previewShape,  rc.Top);
                _previewShape.Width  = Math.Max(1.0, rc.Width);
                _previewShape.Height = Math.Max(1.0, rc.Height);
            }
            else if (_previewShape is System.Windows.Shapes.Ellipse)
            {
                double d = Math.Max(1.0, rc.R * 2.0);
                Canvas.SetLeft(_previewShape, rc.CX - d / 2.0);
                Canvas.SetTop(_previewShape,  rc.CY - d / 2.0);
                _previewShape.Width  = d;
                _previewShape.Height = d;
            }
            else if (_previewShape is AnnulusShape ann)
            {
                double d = Math.Max(1.0, rc.R * 2.0);
                Canvas.SetLeft(ann, rc.CX - d / 2.0);
                Canvas.SetTop(ann,  rc.CY - d / 2.0);
                ann.Width  = d;
                ann.Height = d;
                double inner = Math.Max(0.0, Math.Min(rc.RInner, rc.R)); // clamp
                ann.InnerRadius = inner;
            }

            // Rotation (if applicable on preview)
            try { ApplyRoiRotationToShape(_previewShape, rc.AngleDeg); } catch { /* ignore */ }

            // Optional: reposition label tied to preview shape, if in use
            try { UpdateRoiLabelPosition(_previewShape); } catch { /* ignore if labels disabled */ }

            AppendResizeLog($"[preview] recomputed: img({(_tmpBuffer.Left):0},{(_tmpBuffer.Top):0},{(_tmpBuffer.Width):0},{(_tmpBuffer.Height):0}) → canvas L={Canvas.GetLeft(_previewShape):0},T={Canvas.GetTop(_previewShape):0}, W={_previewShape.Width:0}, H={_previewShape.Height:0}");
        }



        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen (letterbox) ===
        // === Sincroniza CanvasROI para que SE ACOMODE EXACTAMENTE al área visible de la imagen ===
        // === Sincroniza CanvasROI EXACTAMENTE al área visible de la imagen (letterbox) ===
        private void SyncOverlayToImage()
        {
            SyncOverlayToImage(scheduleResync: true);
        }

        // Overload mínima para compatibilidad con llamadas que pasan 'force: ...'
        private void SyncOverlayToImage(bool force, bool scheduleResync = true)
        {
            // Por ahora preserva el comportamiento actual, ignorando el valor de 'force'.
            // En el futuro, si se desea, se podrá usar para forzar el refresco.
            SyncOverlayToImage(scheduleResync: scheduleResync);
        }

        private void SyncOverlayToImage(bool scheduleResync)
        {
            if (_suspendManualOverlayInvalidations)
            {
                AppendLog("[batch] skip overlay sync (batch running)");
                return;
            }

            if (ImgMain == null || CanvasROI == null) return;
            if (ImgMain.Source is not System.Windows.Media.Imaging.BitmapSource bmp) return;

            var displayRect = GetImageDisplayRect();
            if (displayRect.Width <= 0 || displayRect.Height <= 0) return;

            if (CanvasROI.Parent is not FrameworkElement parent) return;
            var imgTopLeft = ImgMain.TranslatePoint(new System.Windows.Point(0, 0), parent);

            double left = imgTopLeft.X + displayRect.Left;
            double top = imgTopLeft.Y + displayRect.Top;
            double w = displayRect.Width;
            double h = displayRect.Height;

            double roundedLeft = Math.Round(left);
            double roundedTop = Math.Round(top);
            double roundedWidth = Math.Round(w);
            double roundedHeight = Math.Round(h);

            CanvasROI.HorizontalAlignment = HorizontalAlignment.Left;
            CanvasROI.VerticalAlignment = VerticalAlignment.Top;
            CanvasROI.Margin = new Thickness(roundedLeft, roundedTop, 0, 0);
            LogHeatmap($"SyncOverlayToImage: roundedLeft={roundedLeft:F0}, roundedTop={roundedTop:F0}");
            CanvasROI.Width = roundedWidth;
            CanvasROI.Height = roundedHeight;

            if (RoiOverlay != null)
            {
                RoiOverlay.HorizontalAlignment = HorizontalAlignment.Left;
                RoiOverlay.VerticalAlignment = VerticalAlignment.Top;
                RoiOverlay.Margin = new Thickness(roundedLeft, roundedTop, 0, 0);
                RoiOverlay.Width = roundedWidth;
                RoiOverlay.Height = roundedHeight;
                RoiOverlay.SnapsToDevicePixels = true;
                RenderOptions.SetEdgeMode(RoiOverlay, EdgeMode.Aliased);
            }

            CanvasROI.SnapsToDevicePixels = true;
            RenderOptions.SetEdgeMode(CanvasROI, EdgeMode.Aliased);

            AppendLog($"[sync] Canvas px=({roundedWidth:0}x{roundedHeight:0}) Offset=({roundedLeft:0},{roundedTop:0})  Img={bmp.PixelWidth}x{bmp.PixelHeight}");

            if (scheduleResync)
            {
                ScheduleSyncOverlay(force: true, reason: "SyncOverlayToImage(resync)");
            }

            var disp = GetImageDisplayRect();
            AppendLog($"[sync] set width/height=({disp.Width:0}x{disp.Height:0}) margin=({CanvasROI.Margin.Left:0},{CanvasROI.Margin.Top:0})");
            AppendLog($"[sync] AFTER layout? canvasActual=({CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0}) imgActual=({ImgMain.ActualWidth:0}x{ImgMain.ActualHeight:0})");

            UpdateHeatmapOverlayLayoutAndClip();
        }

        private void ScheduleSyncOverlay(bool force = false, string reason = "")
        {
            reason ??= string.Empty;

            if (_syncScheduled && !force)
            {
                AppendLog($"[sync] coalesced reason={reason} seq={_syncSeq}");
                return;
            }

            _syncScheduled = true;
            var mySeq = System.Threading.Interlocked.Increment(ref _syncSeq);
            AppendLog($"[sync] scheduled={_syncScheduled} reason={reason} seq={mySeq}");

            bool delayCompleted = false;

            async void Runner()
            {
                AppendLog($"[sync] Runner ENTER seq={mySeq} delayCompleted={delayCompleted}");
                if (!delayCompleted)
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(60).ConfigureAwait(true);
                    }
                    catch (TaskCanceledException)
                    {
                        AppendLog($"[sync] Runner CANCELLED seq={mySeq}");
                        return;
                    }

                    delayCompleted = true;
                }

                if (mySeq != _syncSeq)
                {
                    AppendLog($"[sync] Runner ABORT seq={mySeq} (latest={_syncSeq})");
                    return;
                }

                if (_isSyncRunning)
                {
                    AppendLog($"[sync] Runner defers (already running) seq={mySeq}");
                    Dispatcher.BeginInvoke((Action)Runner, System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }

                _isSyncRunning = true;
                try
                {
                    _syncScheduled = false;
                    AppendLog($"[sync] Runner → RunSyncOverlayCore seq={mySeq} reason={reason}");
                    RunSyncOverlayCore(mySeq, reason);
                }
                finally
                {
                    _isSyncRunning = false;
                    AppendLog($"[sync] Runner EXIT seq={mySeq}");
                }
            }

            Dispatcher.BeginInvoke((Action)Runner, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RunSyncOverlayCore(int seq, string reason)
        {
            var disp = GetImageDisplayRect();
            if (disp.Width <= 0 || disp.Height <= 0)
            {
                AppendLog($"[sync] skip empty display seq={seq} reason={reason} " +
                          $"disp=({disp.X:0.##},{disp.Y:0.##},{disp.Width:0.##},{disp.Height:0.##}) " +
                          $"canvasActual=({CanvasROI?.ActualWidth:0.##}x{CanvasROI?.ActualHeight:0.##}) " +
                          $"imgActual=({ImgMain?.ActualWidth:0.##}x{ImgMain?.ActualHeight:0.##})");
                return;
            }

            bool changed =
                _lastDisplayRect.IsEmpty ||
                System.Math.Abs(_lastDisplayRect.X - disp.X) > DisplayEps ||
                System.Math.Abs(_lastDisplayRect.Y - disp.Y) > DisplayEps ||
                System.Math.Abs(_lastDisplayRect.Width - disp.Width) > DisplayEps ||
                System.Math.Abs(_lastDisplayRect.Height - disp.Height) > DisplayEps;

            bool needsWork = changed || _overlayNeedsRedraw;

            AppendLog($"[sync] run seq={seq} reason={reason} changed={changed} redraw={_overlayNeedsRedraw} " +
                      $"disp=({disp.X:0.##},{disp.Y:0.##},{disp.Width:0.##},{disp.Height:0.##})");

            if (!needsWork)
            {
                AppendLog($"[sync] no work needed seq={seq} reason={reason}");
                return;
            }

            _lastDisplayRect = disp;

            try
            {
                SyncOverlayToImage(scheduleResync: false);
            }
            catch (Exception ex)
            {
                AppendLog($"[sync] SyncOverlayToImage failed: {ex.Message}");
            }

            try
            {
                RedrawOverlay();
                _overlayNeedsRedraw = false;
            }
            catch (Exception ex)
            {
                AppendLog($"[sync] RedrawOverlay failed: {ex.Message}");
            }

            try
            {
                RecomputePreviewShapeAfterSync();
            }
            catch (Exception ex)
            {
                AppendLog($"[sync] preview recompute failed: {ex.Message}");
            }

            try
            {
                UpdateHeatmapOverlayLayoutAndClip();
            }
            catch (Exception ex)
            {
                AppendLog($"[sync] heatmap layout failed: {ex.Message}");
            }

            try
            {
                RedrawAnalysisCrosses();
            }
            catch (Exception ex)
            {
                AppendLog($"[sync] crosses redraw failed: {ex.Message}");
            }

            // CODEX: tras un sync exitoso liberamos la política de interacción si el overlay ya está alineado.
            try
            {
                if (IsOverlayAligned())
                {
                    ApplyInspectionInteractionPolicy("redraw"); // CODEX: reset policy tras sync
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[sync] ApplyInspectionInteractionPolicy(redraw) failed: {ex.Message}");
            }

            AppendResizeLog($"[after_sync] CanvasROI={CanvasROI.ActualWidth:0}x{CanvasROI.ActualHeight:0} displayRect={disp.Width:0}x{disp.Height:0}");
        }

        // Helper moved to class scope so it is visible at call sites (fixes CS0103)
        private void DrawCrossAt(SWPoint p, double size = 12.0, double th = 2.0)
        {
            var h = new System.Windows.Shapes.Line
            {
                X1 = p.X - size, Y1 = p.Y,
                X2 = p.X + size, Y2 = p.Y,
                Stroke = System.Windows.Media.Brushes.Lime,
                StrokeThickness = th,
                IsHitTestVisible = false,
                Tag = "AnalysisCross"
            };
            var v = new System.Windows.Shapes.Line
            {
                X1 = p.X, Y1 = p.Y - size,
                X2 = p.X, Y2 = p.Y + size,
                Stroke = System.Windows.Media.Brushes.Lime,
                StrokeThickness = th,
                IsHitTestVisible = false,
                Tag = "AnalysisCross"
            };
            CanvasROI?.Children.Add(h);
            CanvasROI?.Children.Add(v);
            System.Windows.Controls.Panel.SetZIndex(h, int.MaxValue - 1);
            System.Windows.Controls.Panel.SetZIndex(v, int.MaxValue - 1);
        }

        /* ======================
         * Repositioning helpers (class scope)
         * ====================== */
        // ===== Helpers: logging, centering and anchored reposition (class scope) =====
        [System.Diagnostics.Conditional("DEBUG")]
        private void LogInfo(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
        }

        private void LogDeltaToCross(string label, double roiCxImg, double roiCyImg, double crossCxImg, double crossCyImg)
        {
            var crossCanvas = ImagePxToCanvasPt(crossCxImg, crossCyImg);
            var roiCanvas   = ImagePxToCanvasPt(roiCxImg,   roiCyImg);
            double dx = roiCanvas.X - crossCanvas.X;
            double dy = roiCanvas.Y - crossCanvas.Y;
            LogInfo($"[AlignCheck] {label}: Cross(canvas)=({crossCanvas.X:F3},{crossCanvas.Y:F3}) " +
                    $"ROI(canvas)=({roiCanvas.X:F3},{roiCanvas.Y:F3}) Δ=({dx:F3},{dy:F3})");
        }

        private void RecenterAnchoredToPivot(
            RoiModel roi,
            System.Windows.Point pivotBase,
            System.Windows.Point pivotNew,
            double scale,
            double cosΔ,
            double sinΔ)
        {
            double vx = roi.CX - pivotBase.X;
            double vy = roi.CY - pivotBase.Y;
            double vxr = scale * (cosΔ * vx - sinΔ * vy);
            double vyr = scale * (sinΔ * vx + cosΔ * vy);
            SetRoiCenterImg(roi, pivotNew.X + vxr, pivotNew.Y + vyr);
        }

        private bool TryGetMasterInspection(int masterId, out RoiModel roi)
        {
            roi = null;
            if (_layout == null) return false;

            string[] candidates = new[]
            {
                masterId == 1 ? "Master1Search"      : "Master2Search",
                masterId == 1 ? "Master1Inspection" : "Master2Inspection",
                masterId == 1 ? "Master1Inspect"    : "Master2Inspect",
                masterId == 1 ? "InspectionMaster1" : "InspectionMaster2",
                masterId == 1 ? "M1Inspection"      : "M2Inspection",
                masterId == 1 ? "M1Inspect"         : "M2Inspect"
            };

            var t = _layout.GetType();
            foreach (var name in candidates)
            {
                var p = t.GetProperty(name,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.IgnoreCase);
                if (p != null && typeof(RoiModel).IsAssignableFrom(p.PropertyType))
                {
                    var val = p.GetValue(_layout) as RoiModel;
                    if (val != null) { roi = val; return true; }
                }
            }
            return false;
        }

        private void RepositionMastersAndSubRois(System.Windows.Point m1_new, System.Windows.Point m2_new)
        {
            var m1_base = new System.Windows.Point(_m1BaseX, _m1BaseY);
            var m2_base = new System.Windows.Point(_m2BaseX, _m2BaseY);

            double dxB = m2_base.X - m1_base.X, dyB = m2_base.Y - m1_base.Y;
            double dxN = m2_new.X - m1_new.X, dyN = m2_new.Y - m1_new.Y;
            double lenB = System.Math.Sqrt(dxB * dxB + dyB * dyB);
            double lenN = System.Math.Sqrt(dxN * dxN + dyN * dyN);
            double scale = (lenB > 1e-6) ? (lenN / lenB) : 1.0; // respects analyze scale lock: we don't change ROI sizes
            double angB = System.Math.Atan2(dyB, dxB);
            double angN = System.Math.Atan2(dyN, dxN);
            double angΔ = angN - angB;
            double cosΔ = System.Math.Cos(angΔ), sinΔ = System.Math.Sin(angΔ);

            // Center master pattern ROIs on detected cross centers
            if (_layout?.Master1Pattern != null) SetRoiCenterImg(_layout.Master1Pattern, m1_new.X, m1_new.Y);
            if (_layout?.Master2Pattern != null) SetRoiCenterImg(_layout.Master2Pattern, m2_new.X, m2_new.Y);

            // Anchor master inspections to their master (if present). We do NOT touch Width/Height here.
            if (TryGetMasterInspection(1, out var m1Insp)) RecenterAnchoredToPivot(m1Insp, m1_base, m1_new, scale, cosΔ, sinΔ);
            if (TryGetMasterInspection(2, out var m2Insp)) RecenterAnchoredToPivot(m2Insp, m2_base, m2_new, scale, cosΔ, sinΔ);

            // Logs for quick visual verification in canvas pixels
            if (_layout?.Master1Pattern != null) LogDeltaToCross("M1 Pattern", _layout.Master1Pattern.CX, _layout.Master1Pattern.CY, m1_new.X, m1_new.Y);
            if (_layout?.Master2Pattern != null) LogDeltaToCross("M2 Pattern", _layout.Master2Pattern.CX, _layout.Master2Pattern.CY, m2_new.X, m2_new.Y);

            if (TryGetMasterInspection(1, out m1Insp)) LogDeltaToCross("M1 Insp", m1Insp.CX, m1Insp.CY, m1_new.X, m1_new.Y);
            if (TryGetMasterInspection(2, out m2Insp)) LogDeltaToCross("M2 Insp", m2Insp.CX, m2Insp.CY, m2_new.X, m2_new.Y);
        }

        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T frameworkElement && frameworkElement.Name == name)
                {
                    return frameworkElement;
                }

                var result = FindVisualChildByName<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }





        private RoiShape ReadShapeFrom(ComboBox combo)
        {
            if (combo?.SelectedItem is ComboBoxItem item)
            {
                if (item.Tag is RoiShape taggedShape)
                {
                    return taggedShape;
                }

                var content = item.Content?.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var loweredContent = content.ToLowerInvariant();
                    if (loweredContent.Contains("annulus")) return RoiShape.Annulus;
                    if (loweredContent.Contains("círculo") || loweredContent.Contains("circulo") || loweredContent.Contains("circle"))
                        return RoiShape.Circle;
                }
            }

            string text = (combo?.SelectedItem?.ToString() ?? string.Empty).ToLowerInvariant();
            if (text.Contains("annulus")) return RoiShape.Annulus;
            if (text.Contains("círculo") || text.Contains("circulo") || text.Contains("circle")) return RoiShape.Circle;
            return RoiShape.Rectangle;
        }

        private void SetDrawToolFromShape(RoiShape shape)
        {
            // Mantiene la sincronización de la UI con el estado interno usando el camino
            // centralizado que contempla la ausencia de botones de toggle específicos.
            ApplyDrawToolSelection(shape, updateViewModel: false);
        }

        private void StartDrawingFor(MasterState state, ComboBox shapeCombo)
        {
            _editModeActive = true;
            _state = state;
            var shape = ReadShapeFrom(shapeCombo);
            SetDrawToolFromShape(shape);
            UpdateWizardState();
            Snack($"Dibuja el ROI en el canvas y pulsa Guardar."); // CODEX: string interpolation compatibility.
        }

        private void SaveFor(MasterState state)
        {
            // Reutiliza la ruta ya probada por BtnSaveMaster_Click (usa _state actual internamente)
            var prev = _state;
            _state = state;
            try
            {
                BtnSaveMaster_Click(this, new RoutedEventArgs());
            }
            catch
            {
                _state = prev;
                UpdateWizardState();
                throw;
            }
        }

        private void RemoveFor(MasterState state)
        {
            var prev = _state;
            _state = state;
            try
            {
                var cleared = TryClearCurrentStatePersistedRoi(out var role);
                if (cleared)
                {
                    AppendLog($"[align] cleared {role}");
                    RedrawOverlaySafe();
                    UpdateWizardState();
                    Snack($"{role} eliminado.");
                }
                else
                {
                    Snack($"No hay ROI que eliminar para este slot."); // CODEX: string interpolation compatibility.
                }
            }
            finally
            {
                _state = prev;
            }
        }

        private void BtnM1_Create_Click(object sender, RoutedEventArgs e) => StartDrawingFor(MasterState.DrawM1_Pattern, ComboMasterRoiShape);
        private void BtnM1_Save_Click  (object sender, RoutedEventArgs e) => SaveFor(MasterState.DrawM1_Pattern);
        private void BtnM1_Remove_Click(object sender, RoutedEventArgs e) => RemoveFor(MasterState.DrawM1_Pattern);

        private RoiRole GetSelectedMasterRole(int masterIndex)
        {
            var combo = masterIndex == 1 ? ComboMasterRoiRole : ComboM2Role;
            var isSearch = combo?.SelectedIndex == 1;
            return masterIndex == 1
                ? (isSearch ? RoiRole.Master1Search : RoiRole.Master1Pattern)
                : (isSearch ? RoiRole.Master2Search : RoiRole.Master2Pattern);
        }

        private RoiModel? GetMasterRoiForRole(RoiRole role)
        {
            if (_layout == null)
            {
                return null;
            }

            return role switch
            {
                RoiRole.Master1Pattern => _layout.Master1Pattern,
                RoiRole.Master1Search => _layout.Master1Search,
                RoiRole.Master2Pattern => _layout.Master2Pattern,
                RoiRole.Master2Search => _layout.Master2Search,
                _ => null
            };
        }

        private void SetMasterRoleFrozen(RoiRole role, bool frozen)
        {
            var target = GetMasterRoiForRole(role);
            if (target != null)
            {
                target.IsFrozen = frozen;
            }
        }

        private void SetMasterFrozen(int masterIndex, bool frozen)
        {
            if (_layout == null)
            {
                return;
            }

            if (masterIndex == 1)
            {
                if (_layout.Master1Pattern != null) _layout.Master1Pattern.IsFrozen = frozen;
                if (_layout.Master1Search != null) _layout.Master1Search.IsFrozen = frozen;
            }
            else
            {
                if (_layout.Master2Pattern != null) _layout.Master2Pattern.IsFrozen = frozen;
                if (_layout.Master2Search != null) _layout.Master2Search.IsFrozen = frozen;
            }
        }

        private void AttachAdornersForMaster(int masterIndex)
        {
            if (_layout == null)
            {
                return;
            }

            var activeRole = masterIndex == 1 ? _activeMaster1Role : _activeMaster2Role;
            if (activeRole.HasValue)
            {
                AttachAdornerForRoi(GetMasterRoiForRole(activeRole.Value));
            }
        }

        private void RemoveAdornersForMaster(int masterIndex)
        {
            if (_layout == null)
            {
                return;
            }

            if (masterIndex == 1)
            {
                RemoveAdornersForRoi(_layout.Master1Pattern);
                RemoveAdornersForRoi(_layout.Master1Search);
            }
            else
            {
                RemoveAdornersForRoi(_layout.Master2Pattern);
                RemoveAdornersForRoi(_layout.Master2Search);
            }
        }

        private void BtnEditM1_Click(object sender, RoutedEventArgs e)
        {
            if (_layout == null)
            {
                Snack("Carga un layout antes de editar Master 1.");
                return;
            }

            if (_editingInspectionSlot.HasValue)
            {
                ExitInspectionEditMode(saveChanges: true);
            }

            if (_editingM2)
            {
                CancelMasterEditing(redraw: false);
            }

            var targetRole = GetSelectedMasterRole(1);
            var targetRoi = GetMasterRoiForRole(targetRole);

            if (!_editingM1)
            {
                if (targetRoi == null)
                {
                    Snack("Faltan ROI de Master 1.");
                    return;
                }

                // Freeze every master ROI and only unfreeze the one selected in the combobox.
                SetMasterFrozen(1, true);
                SetMasterFrozen(2, true);
                SetMasterRoleFrozen(targetRole, false);

                _activeMaster1Role = targetRole;
                _state = MasterState.Ready;
                _isDrawing = false;
                _editingM1 = true;
                _editModeActive = true;
                BtnEditM1.Content = "Save Master 1";

                RemoveAdornersForMaster(1);
                AttachAdornersForMaster(1);
                RedrawOverlaySafe();
            }
            else
            {
                SaveMasterWizardFlow();

                SetMasterFrozen(1, true);

                RemoveAdornersForMaster(1);
                _editingM1 = false;
                _activeMaster1Role = null;
                _editModeActive = _editingM2;
                BtnEditM1.Content = "Edit Master 1";
                RedrawOverlaySafe();
                Snack("Master 1 válido.");
            }
        }

        private void BtnM1S_Create_Click(object sender, RoutedEventArgs e) => StartDrawingFor(MasterState.DrawM1_Search, ComboMasterRoiShape);
        private void BtnM1S_Save_Click  (object sender, RoutedEventArgs e) => SaveFor(MasterState.DrawM1_Search);
        private void BtnM1S_Remove_Click(object sender, RoutedEventArgs e) => RemoveFor(MasterState.DrawM1_Search);

        private void BtnEditM2_Click(object sender, RoutedEventArgs e)
        {
            if (_layout == null)
            {
                Snack("Carga un layout antes de editar Master 2.");
                return;
            }

            if (_editingInspectionSlot.HasValue)
            {
                ExitInspectionEditMode(saveChanges: true);
            }

            if (_editingM1)
            {
                CancelMasterEditing(redraw: false);
            }

            var targetRole = GetSelectedMasterRole(2);
            var targetRoi = GetMasterRoiForRole(targetRole);

            if (!_editingM2)
            {
                if (targetRoi == null)
                {
                    Snack("Faltan ROI de Master 2.");
                    return;
                }

                SetMasterFrozen(1, true);
                SetMasterFrozen(2, true);
                SetMasterRoleFrozen(targetRole, false);

                _activeMaster2Role = targetRole;
                _state = MasterState.Ready;
                _isDrawing = false;
                _editingM2 = true;
                _editModeActive = true;
                BtnEditM2.Content = "Save Master 2";

                RemoveAdornersForMaster(2);
                AttachAdornersForMaster(2);
                RedrawOverlaySafe();
            }
            else
            {
                SaveMasterWizardFlow();

                SetMasterFrozen(2, true);

                RemoveAdornersForMaster(2);
                _editingM2 = false;
                _activeMaster2Role = null;
                _editModeActive = _editingM1;
                BtnEditM2.Content = "Edit Master 2";
                RedrawOverlaySafe();
                Snack("Master 2 válido.");
            }
        }

        private async void BtnLoadModel_Click(object sender, RoutedEventArgs e)
        {
            var index = _activeInspectionIndex;
            if (sender is FrameworkElement fe && fe.Tag != null)
            {
                if (fe.Tag is int intTag)
                {
                    index = intTag;
                }
                else if (fe.Tag is string strTag && int.TryParse(strTag, out var parsed))
                {
                    index = parsed;
                }
            }

            await LoadModelForInspectionAsync(index);
        }

        private async Task LoadModelForInspectionAsync(int requestedIndex)
        {
            using var freeze = FreezeRoiRepositionScope(nameof(LoadModelForInspectionAsync));

            var index = Math.Max(1, Math.Min(4, requestedIndex));

            try
            {
                var modelDir = GetInspectionModelFolder(index);
                var settings = Properties.Settings.Default;
                string modelKey = $"LastModelDirROI{index}";
                string? lastModelDir = settings[modelKey] as string;

                string initialDirectory = !string.IsNullOrWhiteSpace(lastModelDir) && Directory.Exists(lastModelDir)
                    ? lastModelDir
                    : Directory.Exists(modelDir)
                        ? modelDir
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                var dialog = new OpenFileDialog
                {
                    Title = $"Selecciona un modelo para Inspection {index}",
                    InitialDirectory = initialDirectory,
                    Filter = "Model files|*.json;*.onnx;*.bin;*.npz;*.faiss|All files|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                if (_workflowViewModel == null)
                {
                    Snack($"Workflow no disponible."); // CODEX: string interpolation compatibility.
                    return;
                }

                var roiConfig = _workflowViewModel.InspectionRois?.FirstOrDefault(r => r.Index == index);
                if (roiConfig == null)
                {
                    Snack($"Inspection {index} no está configurado.");
                    return;
                }

                bool loaded = await _workflowViewModel.LoadModelManifestAsync(roiConfig, dialog.FileName);
                if (!loaded)
                {
                    return;
                }

                var selectedDir = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrWhiteSpace(selectedDir))
                {
                    settings[modelKey] = selectedDir;
                    settings.Save();
                }

                AppendLog($"[model] Modelo cargado en Inspection {index}: {dialog.FileName}");

                await Dispatcher.InvokeAsync(() =>
                {
                    _workflowViewModel.SelectedInspectionRoi = roiConfig;
                    RefreshInspectionRoiSlots();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[model] Error al cargar modelo: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "No se pudo cargar el modelo seleccionado. Revisa el log para más detalles.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }
        }

        private void WorkflowHostOnToggleEditRequested(object? sender, ToggleEditRequestedEventArgs e)
        {
            if (e == null)
            {
                GuiLog.Warn("[workflow-edit] ToggleEditRequested ignored: event args null");
                return;
            }

            ApplyInspectionToggleEdit(e.RoiId, e.Index, source: "workflow-tab");
        }

        private async void WorkflowHostOnLoadModelRequested(object? sender, LoadModelRequestedEventArgs e)
        {
            await LoadModelForInspectionAsync(e.Index);
        }

        private void BtnClearCanvas_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Esto borrará TODOS los ROI y reiniciará el estado. ¿Deseas continuar?",
                "Confirmar borrado y reset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            CancelMasterEditing(redraw: false);

            // CODEX: string interpolation compatibility.
            AppendLog($"[align] Reset solicitado por el usuario (Borrar Canvas).");

            try
            {
                ExitEditMode("reset", skipCapture: true);
                _isDrawing = false;
                _dragShape = null;
                _tmpBuffer = null;
                _analysisViewActive = false;

                try { DetachPreviewAndAdorner(); } catch { }
                try { ClearCanvasShapesAndLabels(); } catch { }
                try { ClearCanvasInternalMaps(); } catch { }
                try { ClearPersistedRoisFromCanvas(); } catch { }
                try { RemoveAllRoiAdorners(); } catch { }

                if (RoiOverlay != null)
                {
                    RoiOverlay.Visibility = Visibility.Collapsed;
                }

                _inspectionBaselinesByImage.Clear();

                if (_layout != null)
                {
                    _layout.Master1Pattern = null;
                    _layout.Master1PatternImagePath = null;
                    _layout.Master1Search = null;
                    _layout.Master2Pattern = null;
                    _layout.Master2PatternImagePath = null;
                    _layout.Master2Search = null;
                    _layout.Inspection = null;
                    _layout.InspectionBaseline = null;
                    _layout.Inspection1 = null;
                    _layout.Inspection2 = null;
                    _layout.Inspection3 = null;
                    _layout.Inspection4 = null;
                    _layout.InspectionBaselinesByImage?.Clear();
                }

                for (int i = 1; i <= 4; i++)
                {
                    SetInspectionSlotModel(i, null, updateActive: false);
                }

                Inspection1 = null;
                Inspection2 = null;
                Inspection3 = null;
                Inspection4 = null;

                _workflowViewModel?.SetMasterLayout(_layout);
                _workflowViewModel?.SetInspectionRoiModels(null, null, null, null);
                _workflowViewModel?.ResetModelStates();

                _state = MasterState.DrawM1_Pattern;
                ApplyDrawToolSelection(RoiShape.Rectangle, updateViewModel: true);
                SetActiveInspectionIndex(1);

                ScheduleSyncOverlay(force: true, reason: "ClearCanvas");
                RequestRoiVisibilityRefresh();
                RedrawOverlaySafe();
                UpdateRoiHud();
                UpdateWizardState();

                TryPersistLayout();

                // CODEX: string interpolation compatibility.
                AppendLog($"[align] Reset completado. Estado listo para crear Masters nuevamente.");
                Snack($"Canvas borrado."); // CODEX: string interpolation compatibility.
            }
            catch (Exception ex)
            {
                // CODEX: string interpolation compatibility.
                AppendLog($"[align] Error al limpiar canvas: {ex.Message}");
                MessageBox.Show(
                    "Error al limpiar canvas.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

    }
}
