using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrakeDiscInspector_GUI_ROI.Helpers;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrakeDiscInspector_GUI_ROI;
using BrakeDiscInspector_GUI_ROI.Helpers;
using BrakeDiscInspector_GUI_ROI.Util;
using BrakeDiscInspector_GUI_ROI.Imaging;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Properties;
using OpenCvSharp;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Forms = System.Windows.Forms;
using GlobalInferResult = global::BrakeDiscInspector_GUI_ROI.InferResult;
using WorkflowInferResult = global::BrakeDiscInspector_GUI_ROI.Workflow.InferResult;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public sealed class BatchRow : INotifyPropertyChanged
    {
        private int _indexOneBased;
        private BatchCellStatus _roi1 = BatchCellStatus.Unknown;
        private BatchCellStatus _roi2 = BatchCellStatus.Unknown;
        private BatchCellStatus _roi3 = BatchCellStatus.Unknown;
        private BatchCellStatus _roi4 = BatchCellStatus.Unknown;

        public BatchRow(string fullPath)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
        }

        public string FileName { get; }
        public string FullPath { get; }

        public int IndexOneBased
        {
            get => _indexOneBased;
            internal set
            {
                if (_indexOneBased != value)
                {
                    _indexOneBased = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI1
        {
            get => _roi1;
            set
            {
                if (_roi1 != value)
                {
                    _roi1 = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI2
        {
            get => _roi2;
            set
            {
                if (_roi2 != value)
                {
                    _roi2 = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI3
        {
            get => _roi3;
            set
            {
                if (_roi3 != value)
                {
                    _roi3 = value;
                    OnPropertyChanged();
                }
            }
        }

        public BatchCellStatus ROI4
        {
            get => _roi4;
            set
            {
                if (_roi4 != value)
                {
                    _roi4 = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

    }

    public sealed class MasterRoleOption
    {
        public MasterRoleOption(string label, RoiRole role)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Role = role;
        }

        public string Label { get; }

        public RoiRole Role { get; }

        public override string ToString() => Label;
    }

    public sealed class MasterDetection
    {
        public Point2d? Center { get; set; }
        public double AngleDeg { get; set; }
        public int Score { get; set; }
        public bool IsOk => Center.HasValue;
    }

    public sealed partial class WorkflowViewModel : INotifyPropertyChanged
    {
        private readonly BackendClient _client;
        private readonly DatasetManager _datasetManager;
        private readonly Func<Task<RoiExportResult?>> _exportRoiAsync;
        private readonly Func<string?> _getSourceImagePath;
        private readonly Action<string> _log;
        private readonly Action<string>? _trace;
        private readonly Action<string>? _showSnackbar;
        private readonly Action<string>? _showBusyDialog;
        private readonly Action<double?>? _updateBusyProgress;
        private readonly Action? _hideBusyDialog;
        private readonly Func<RoiExportResult, byte[], double, Task> _showHeatmapAsync;
        private readonly Action _clearHeatmap;
        private readonly Action<bool?> _updateGlobalBadge;
        private readonly Action<int>? _activateInspectionIndex;
        private readonly Func<string, long, CancellationToken, Task>? _repositionInspectionRoisAsyncExternal;
        private readonly Func<RoiRole, RoiShape, Task>? _createMasterRoiAsync;
        private readonly Func<RoiRole, Task<bool>>? _toggleEditSaveMasterRoiAsync;
        private readonly Func<RoiRole, Task>? _removeMasterRoiAsync;
        private readonly Func<bool>? _canEditMasterRoi;

        private ObservableCollection<InspectionRoiConfig>? _inspectionRois;
        private RoiModel? _inspection1;
        private RoiModel? _inspection2;
        private RoiModel? _inspection3;
        private RoiModel? _inspection4;
        private MasterLayout? _layout;
        private MasterLayout? _layoutOriginal;
        private InspectionRoiConfig? _selectedInspectionRoi;
        private bool? _hasFitEndpoint;
        private bool? _hasCalibrateEndpoint;
        private long _batchStepId;

        private volatile bool _batchAnchorM1Ready;
        private volatile bool _batchAnchorM2Ready;
        private volatile bool _batchXformComputed;
        private int _batchPlacementToken;
        private int _batchPlacementTokenPlaced = -1;
        private bool _batchCanvasMeasured;
        private string? _currentBatchFile;
        private bool _allowBatchInferWithoutAnchors;
        public string? CurrentManualImagePath { get; private set; }
        private int _currentRowIndex;
        private string? _currentImagePath;
        private bool? _batchRowOk;

        private bool _isBusy;
        private bool _isImageLoaded;
        private string _roleId = "Master1";
        private string _roiId = "Inspection";
        private double _mmPerPx = 0.20;
        private string _backendBaseUrl;
        private string _fitSummary = "";
        private string _calibrationSummary = "";
        private double? _inferenceScore;
        private double? _inferenceThreshold;
        private double _localThreshold;
        private string _inferenceSummary = string.Empty;
        private double _heatmapOpacity = 0.6;
        private string _healthSummary = "";
        private bool? _isBackendOnline;
        private readonly SemaphoreSlim _healthRefreshGate = new(1, 1);
        private readonly IReadOnlyList<RoiShape> _availableRoiShapes = Enum.GetValues(typeof(RoiShape)).Cast<RoiShape>().ToList();
        private RoiShape _selectedMaster1Shape = RoiShape.Rectangle;
        private RoiShape _selectedMaster2Shape = RoiShape.Rectangle;
        private bool _isMaster1Editing;
        private bool _isMaster2Editing;
        private MasterRoleOption? _selectedMaster1Role;
        private MasterRoleOption? _selectedMaster2Role;

        private readonly Dictionary<string, RoiBaseline> _baselines = new();
        private readonly object _baselineLock = new();

        private bool _showMaster1Pattern = true;
        private bool _showMaster1Inspection = true;
        private bool _showMaster2Pattern = true;
        private bool _showMaster2Inspection = true;
        private bool _showInspectionRoi = true;

        // CODEX: UI placement guards
        private bool _manualRepositionGuard;
        private bool _batchRepositionGuard;
        private int _batchPlacedForStep = -1;
        private volatile int _batchAnchorReadyForStep = -1;
        private int _batchAnchorM1Score;
        private int _batchAnchorM2Score;
        private bool _batchAnchorsOk;
        private MasterDetection? _m1Detection;
        private MasterDetection? _m2Detection;
        private readonly RoiModel?[] _batchBaselineRois = new RoiModel?[4];
        private Mat? _cachedM1Pattern;
        private Mat? _cachedM2Pattern;
        private string? _cachedM1PatternPath;
        private string? _cachedM2PatternPath;
        private DateTime? _cachedM1PatternLastWriteUtc;
        private long? _cachedM1PatternSize;
        private DateTime? _cachedM2PatternLastWriteUtc;
        private long? _cachedM2PatternSize;
        private bool _warnedMissingM1PatternPath;
        private bool _warnedMissingM2PatternPath;
        private readonly object _masterPatternCacheLock = new();

        // CODEX: batch reposition flags
        private CancellationToken _currentBatchCt = CancellationToken.None;

        // CODEX: bloquear persistencia de layout durante batch
        private int _layoutPersistenceLock; // 0=no bloqueado; >0 bloqueado

        private RoiExportResult? _lastExport;
        private byte[]? _lastHeatmapBytes;
        private InferResult? _lastInferResult;
        private CancellationTokenSource? _batchCts;
        private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
        private bool _isBatchRunning;
        private readonly ObservableCollection<BatchRow> _batchRows = new();
        private string? _batchFolder;
        private bool _canStartBatch;
        private string _batchSummary = string.Empty;
        private string _batchStatus = string.Empty;
        private ImageSource? _batchImageSource;
        private ImageSource? _batchHeatmapSource;
        private int _heatmapCutoffPercent = 50;
        private string _heatmapInfo = "Cutoff: 50%";
        private const double BatchPauseMinSeconds = 0.0;
        private const double BatchPauseMaxSeconds = 10.0;
        private double _batchPausePerRoiSeconds = 0.0;
        private byte[]? _lastHeatmapPngBytes;
        private WriteableBitmap? _lastHeatmapBitmap;
        private int _batchHeatmapRoiIndex = 0;
        private readonly Dictionary<int, byte[]> _batchHeatmapPngByRoi = new();
        private double _baseImageActualWidth;
        private double _baseImageActualHeight;
        private double _canvasRoiActualWidth;
        private double _canvasRoiActualHeight;
        private int _baseImagePixelWidth;
        private int _baseImagePixelHeight;
        private bool _useCanvasPlacementForBatchHeatmap = true;
        private readonly Dictionary<InspectionRoiConfig, RoiDatasetAnalysis> _roiDatasetCache = new();
        private readonly Dictionary<InspectionRoiConfig, FitOkResult> _lastFitResultsByRoi = new();
        private readonly Dictionary<InspectionRoiConfig, CalibResult> _lastCalibResultsByRoi = new();
        private readonly Dictionary<InspectionRoiConfig, bool> _backendStateAvailableByRoi = new();
        private readonly Func<InspectionRoiConfig, string?>? _resolveModelDirectory;
        private readonly object _initLock = new();
        private Task? _initializationTask;
        private bool _initialized;
        private bool _isInitializing;
        private string? _annotatedOutputDir;
        private string _currentLayoutName = "DefaultLayout";
        private bool _hasLoadedLayout;
        private int _layoutIoDepth;
        private readonly SemaphoreSlim _captureGate = new(1, 1);
        private bool _sharedHeatmapGuardLogged;

        public string CurrentLayoutName => _currentLayoutName;

        public bool HasLoadedLayout
        {
            get => _hasLoadedLayout;
            private set
            {
                if (_hasLoadedLayout != value)
                {
                    _hasLoadedLayout = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LayoutNameDisplay));
                }
            }
        }

        public string LayoutNameDisplay => HasLoadedLayout ? CurrentLayoutName : "Layout not loaded";

        public int AnchorScoreMin { get; set; } = 85;

        private int GetBatchThrM1()
        {
            var thr = _layoutOriginal?.Analyze?.ThrM1 ?? _layout?.Analyze?.ThrM1;
            return thr.HasValue && thr.Value > 0 ? thr.Value : AnchorScoreMin;
        }

        private int GetBatchThrM2()
        {
            var thr = _layoutOriginal?.Analyze?.ThrM2 ?? _layout?.Analyze?.ThrM2;
            return thr.HasValue && thr.Value > 0 ? thr.Value : AnchorScoreMin;
        }

        private string GetBatchFeatureM1()
        {
            var feature = _layoutOriginal?.Analyze?.FeatureM1 ?? _layout?.Analyze?.FeatureM1;
            return string.IsNullOrWhiteSpace(feature) ? "auto" : feature;
        }

        private string GetBatchFeatureM2()
        {
            var feature = _layoutOriginal?.Analyze?.FeatureM2 ?? _layout?.Analyze?.FeatureM2;
            return string.IsNullOrWhiteSpace(feature) ? "auto" : feature;
        }

        private record RoiBaseline(string RoiId, Rect BaseRect, Point Center, double R, double Rin, double AngleDeg);

        public Canvas? OverlayCanvas { get; set; }
        public Action<string, bool>? OverlayBatchCaption { get; set; }

        public event EventHandler? BatchStarted;
        public event EventHandler? BatchEnded;

        private sealed class BatchSimilarity
        {
            public double Scale { get; set; } = 1.0;
            public double RotationDeg { get; set; } = 0.0;
            public double Tx { get; set; } = 0.0;
            public double Ty { get; set; } = 0.0;
            public long StepId { get; set; }
        }

        private BatchSimilarity _batchXform = new();

        private sealed class BatchImageContext : IDisposable
        {
            public BatchImageContext(string path, byte[] bytes, Mat src, Mat gray)
            {
                Path = path;
                Bytes = bytes;
                Src = src;
                Gray = gray;
            }

            public string Path { get; }
            public byte[] Bytes { get; }
            public Mat Src { get; }
            public Mat Gray { get; }

            public void Dispose()
            {
                Src.Dispose();
                Gray.Dispose();
            }
        }

        private Point? _batchDetectedM1;
        private Point? _batchDetectedM2;
        private Point? _batchBaselineM1;
        private Point? _batchBaselineM2;

        private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private static readonly HashSet<string> BatchImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tif",
            ".tiff",
            ".webp"
        };

        public WorkflowViewModel(
            BackendClient client,
            DatasetManager datasetManager,
            Func<Task<RoiExportResult?>> exportRoiAsync,
            Func<string?> getSourceImagePath,
            Action<string> log,
            Func<RoiExportResult, byte[], double, Task> showHeatmapAsync,
            Action clearHeatmap,
            Action<bool?> updateGlobalBadge,
            Action<int>? activateInspectionIndex = null,
            Func<InspectionRoiConfig, string?>? resolveModelDirectory = null,
            Func<string, long, CancellationToken, Task>? repositionInspectionRoisAsync = null,
            Func<RoiRole, RoiShape, Task>? createMasterRoiAsync = null,
            Func<RoiRole, Task<bool>>? toggleEditSaveMasterRoiAsync = null,
            Func<RoiRole, Task>? removeMasterRoiAsync = null,
            Func<bool>? canEditMasterRoi = null,
            Action<string>? showSnackbar = null,
            Action<string>? showBusyDialog = null,
            Action<double?>? updateBusyProgress = null,
            Action? hideBusyDialog = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _datasetManager = datasetManager ?? throw new ArgumentNullException(nameof(datasetManager));
            _exportRoiAsync = exportRoiAsync ?? throw new ArgumentNullException(nameof(exportRoiAsync));
            _getSourceImagePath = getSourceImagePath ?? throw new ArgumentNullException(nameof(getSourceImagePath));
            _log = log ?? (_ => { });
            _trace = log;
            _showSnackbar = showSnackbar;
            _showBusyDialog = showBusyDialog;
            _updateBusyProgress = updateBusyProgress;
            _hideBusyDialog = hideBusyDialog;
            _showHeatmapAsync = showHeatmapAsync ?? throw new ArgumentNullException(nameof(showHeatmapAsync));
            _clearHeatmap = clearHeatmap ?? throw new ArgumentNullException(nameof(clearHeatmap));
            _updateGlobalBadge = updateGlobalBadge ?? (_ => { });
            _activateInspectionIndex = activateInspectionIndex;
            _resolveModelDirectory = resolveModelDirectory ?? GetModelDirectoryForRoi;
            _repositionInspectionRoisAsyncExternal = repositionInspectionRoisAsync;
            _createMasterRoiAsync = createMasterRoiAsync;
            _toggleEditSaveMasterRoiAsync = toggleEditSaveMasterRoiAsync;
            _removeMasterRoiAsync = removeMasterRoiAsync;
            _canEditMasterRoi = canEditMasterRoi;

            _backendBaseUrl = _client.BaseUrl;

            Master1RoleOptions = new List<MasterRoleOption>
            {
                new("Master 1 Pattern", RoiRole.Master1Pattern),
                new("Master 1 Search", RoiRole.Master1Search)
            };
            Master2RoleOptions = new List<MasterRoleOption>
            {
                new("Master 2 Pattern", RoiRole.Master2Pattern),
                new("Master 2 Search", RoiRole.Master2Search)
            };

            SelectedMaster1Role = Master1RoleOptions[0];
            SelectedMaster2Role = Master2RoleOptions[0];

            OkSamples = new ObservableCollection<DatasetSample>();
            NgSamples = new ObservableCollection<DatasetSample>();
            OkSamples.CollectionChanged += (_, __) =>
            {
                TrainFitCommand.RaiseCanExecuteChanged();
                CalibrateCommand.RaiseCanExecuteChanged();
            };
            NgSamples.CollectionChanged += (_, __) =>
            {
                CalibrateCommand.RaiseCanExecuteChanged();
            };

            AddOkFromCurrentRoiCommand = CreateCommand(_ => AddSampleAsync(isNg: false));
            AddNgFromCurrentRoiCommand = CreateCommand(_ => AddSampleAsync(isNg: true));
            RemoveSelectedCommand = CreateCommand(_ => RemoveSelectedAsync(), _ => !IsBusy && (SelectedOkSample != null || SelectedNgSample != null));
            RemoveSelectedPreviewCommand = CreateCommand(_ => RemoveSelectedPreviewAsync(), _ => CanRemoveSelectedPreview());
            OpenDatasetFolderCommand = CreateCommand(_ => OpenDatasetFolderAsync(), _ => !IsBusy);
            TrainFitCommand = CreateCommand(_ => TrainAsync(), _ => !IsBusy && OkSamples.Count >= 10);
            CalibrateCommand = CreateCommand(_ => CalibrateAsync(), _ => !IsBusy && OkSamples.Count >= 10 && NgSamples.Count >= 1);
            InferFromCurrentRoiCommand = CreateCommand(_ => InferCurrentAsync(), _ => !IsBusy);
            RefreshDatasetCommand = CreateCommand(_ => RefreshDatasetAsync(), _ => !IsBusy);
            RefreshHealthCommand = new AsyncCommand(_ => RefreshHealthAsync());

            BrowseBatchFolderCommand = new AsyncCommand(async _ =>
            {
                await DoBrowseBatchFolderAsync().ConfigureAwait(false);
            }, _ => !IsBusy);
            StartBatchCommand = new AsyncCommand(_ =>
            {
                if (_isBatchRunning)
                {
                    _pauseGate.Set();
                    SetBatchStatusSafe("Running");
                    UpdateBatchCommandStates();
                    return Task.CompletedTask;
                }

                if (!CanStartBatch || IsBusy)
                {
                    return Task.CompletedTask;
                }

                var snapshot = GetBatchRowsSnapshot();
                InvokeOnUi(() =>
                {
                    _batchRows.Clear();
                    int index = 0;
                    foreach (var row in snapshot)
                    {
                        index++;
                        row.IndexOneBased = index;
                        row.ROI1 = BatchCellStatus.Unknown;
                        row.ROI2 = BatchCellStatus.Unknown;
                        row.ROI3 = BatchCellStatus.Unknown;
                        row.ROI4 = BatchCellStatus.Unknown;
                        _batchRows.Add(row);
                    }

                    OnPropertyChanged(nameof(BatchRows));
                });

                _batchCts?.Cancel();
                _batchCts?.Dispose();
                _batchCts = new CancellationTokenSource();
                _pauseGate.Set();
                _isBatchRunning = true;
                IsBusy = true;
                UpdateBatchCommandStates();
                SetBatchStatusSafe("Running");

                var runTask = RunBatchAsync(_batchCts.Token);
                runTask.ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        _log($"[batch] run failed: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                    }
                }, TaskScheduler.Default);

                return Task.CompletedTask;
            }, _ => CanExecuteStart());

            PauseBatchCommand = new AsyncCommand(_ =>
            {
                if (_isBatchRunning && _pauseGate.IsSet)
                {
                    _pauseGate.Reset();
                    SetBatchStatusSafe("Paused");
                    UpdateBatchCommandStates();
                }

                return Task.CompletedTask;
            }, _ => _isBatchRunning && _pauseGate.IsSet);

            StopBatchCommand = new AsyncCommand(_ =>
            {
                StopBatch();
                return Task.CompletedTask;
            }, _ => _isBatchRunning);

            BrowseDatasetCommand = CreateCommand(_ => BrowseDatasetAsync(), _ => !IsBusy && SelectedInspectionRoi != null);
            TrainSelectedRoiCommand = CreateCommand(async _ => await TrainSelectedRoiAsync().ConfigureAwait(false), _ => !IsBusy && SelectedInspectionRoi != null && SelectedInspectionRoi.DatasetOkCount >= 10);
            CalibrateSelectedRoiCommand = CreateCommand(async _ => await CalibrateSelectedRoiAsync().ConfigureAwait(false), _ => !IsBusy && CanCalibrateSelectedRoi());
            EvaluateSelectedRoiCommand = CreateCommand(_ => EvaluateSelectedRoiAsync(), _ => !IsBusy && SelectedInspectionRoi != null && SelectedInspectionRoi.Enabled);
            var inferEnabledCommand = CreateCommand(_ => InferEnabledRoisAsync(), _ => !IsBusy && HasAnyEnabledInspectionRoi());
            EvaluateAllRoisCommand = inferEnabledCommand;
            InferEnabledRoisCommand = inferEnabledCommand;

            UpdateCanStart();
            UpdateHeatmapThreshold();

            AddRoiToDatasetOkCommand = new AsyncCommand(async param =>
            {
                if (param is InspectionRoiConfig roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, isOk: true)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToDatasetNgCommand = new AsyncCommand(async param =>
            {
                if (param is InspectionRoiConfig roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, isOk: false)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToOkCommand = new AsyncCommand(async param =>
            {
                if (param is RoiModel roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, positive: true)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            AddRoiToNgCommand = new AsyncCommand(async param =>
            {
                if (param is RoiModel roi)
                {
                    await RunExclusiveAsync(() => AddRoiToDatasetAsync(roi, positive: false)).ConfigureAwait(false);
                }
            }, _ => !IsBusy);

            CreateMaster1RoiCommand = CreateCommand(_ => CreateMasterRoiAsync(SelectedMaster1Role, SelectedMaster1Shape), _ => CanEditMasterRoi());
            ToggleEditMaster1RoiCommand = CreateCommand(_ => ToggleEditSaveMasterRoiAsync(SelectedMaster1Role), _ => CanEditMasterRoi());
            RemoveMaster1RoiCommand = CreateCommand(_ => RemoveMasterRoiAsync(SelectedMaster1Role), _ => CanEditMasterRoi());

            CreateMaster2RoiCommand = CreateCommand(_ => CreateMasterRoiAsync(SelectedMaster2Role, SelectedMaster2Shape), _ => CanEditMasterRoi());
            ToggleEditMaster2RoiCommand = CreateCommand(_ => ToggleEditSaveMasterRoiAsync(SelectedMaster2Role), _ => CanEditMasterRoi());
            RemoveMaster2RoiCommand = CreateCommand(_ => RemoveMasterRoiAsync(SelectedMaster2Role), _ => CanEditMasterRoi());

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ShowMaster1Pattern)
                    || e.PropertyName == nameof(ShowMaster1Inspection)
                    || e.PropertyName == nameof(ShowMaster2Pattern)
                    || e.PropertyName == nameof(ShowMaster2Inspection)
                    || e.PropertyName == nameof(ShowInspectionRoi))
                {
                    Application.Current?.Dispatcher.Invoke(RedrawOverlays);
                }
            };
        }

        public void SetLayoutName(string layoutName)
        {
            var normalized = string.IsNullOrWhiteSpace(layoutName) ? "DefaultLayout" : layoutName;

            // "last" es un layout efímero (last.layout.json). NO es un recipe válido y no se debe enviar al backend.
            var isReservedLast = string.Equals(normalized, "last", StringComparison.OrdinalIgnoreCase);

            _currentLayoutName = normalized;
            OnPropertyChanged(nameof(CurrentLayoutName));
            OnPropertyChanged(nameof(LayoutNameDisplay));

            // Si es "last", no mandamos header (backend resolverá 'default').
            _client.RecipeId = isReservedLast ? null : _currentLayoutName;

            InvalidateMasterPatternCache();
        }

        public void SetLayoutLoadedState(bool hasLoadedLayout)
        {
            HasLoadedLayout = hasLoadedLayout;
        }

        public void AlignDatasetPathsWithCurrentLayout()
        {
            if (_layout == null)
            {
                _log("[dataset] align skipped: no layout loaded");
                return;
            }

            var recipeRoot = RecipePathHelper.GetLayoutFolder(_currentLayoutName);
            MasterLayoutManager.AlignInspectionDatasetPaths(_layout, recipeRoot, null);
        }

        public ObservableCollection<DatasetSample> OkSamples { get; }
        public ObservableCollection<DatasetSample> NgSamples { get; }

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; private set; } = new();

        public ObservableCollection<BatchRow> BatchRows => _batchRows;

        public long BatchStepId => Interlocked.Read(ref _batchStepId);

        public int CurrentRowIndex
        {
            get => _currentRowIndex;
            private set
            {
                if (_currentRowIndex != value)
                {
                    _currentRowIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? CurrentImagePath
        {
            get => _currentImagePath;
            private set
            {
                if (!string.Equals(_currentImagePath, value, StringComparison.Ordinal))
                {
                    _currentImagePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool? BatchRowOk
        {
            get => _batchRowOk;
            private set
            {
                if (_batchRowOk != value)
                {
                    _batchRowOk = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsLayoutIo => Volatile.Read(ref _layoutIoDepth) > 0;

        // CODEX: helpers para bloquear persistencia de layout durante batch
        private void EnterLayoutPersistenceLock()
        {
            System.Threading.Interlocked.Increment(ref _layoutPersistenceLock);
            TraceBatch($"[batch] layout-persist:LOCK depth={_layoutPersistenceLock}");
        }

        private void ExitLayoutPersistenceLock()
        {
            var v = System.Threading.Interlocked.Decrement(ref _layoutPersistenceLock);
            if (v < 0)
            {
                _layoutPersistenceLock = 0;
            }

            TraceBatch($"[batch] layout-persist:UNLOCK depth={_layoutPersistenceLock}");
        }

        public bool IsLayoutPersistenceLocked => _layoutPersistenceLock > 0;

        private void ResetBatchTransform()
        {
            _batchXform = new BatchSimilarity { StepId = BatchStepId };
            _batchAnchorM1Score = 0;
            _batchAnchorM2Score = 0;
            _batchAnchorsOk = false;
            TraceBatch($"[batch-xform] reset step={_batchXform.StepId} S=1 R=0 Tx=0 Ty=0");
        }


        private bool AnchorsMeetThreshold()
        {
            var thrM1 = GetBatchThrM1();
            var thrM2 = GetBatchThrM2();
            return _batchAnchorM1Score >= thrM1 && _batchAnchorM2Score >= thrM2;
        }

        public void RegisterBatchAnchors(Point baselineM1, Point baselineM2, Point detectedM1, Point detectedM2, int scoreM1 = 0, int scoreM2 = 0)
        {
            _batchBaselineM1 = baselineM1;
            _batchBaselineM2 = baselineM2;
            _batchDetectedM1 = detectedM1;
            _batchDetectedM2 = detectedM2;
            _batchAnchorM1Ready = true;
            _batchAnchorM2Ready = true;
            _batchAnchorReadyForStep = (int)_batchStepId;
            _batchXformComputed = false;
            _batchAnchorM1Score = scoreM1;
            _batchAnchorM2Score = scoreM2;
            _batchAnchorsOk = AnchorsMeetThreshold();
            _currentBatchFile = System.IO.Path.GetFileName(CurrentImagePath ?? string.Empty);

            _batchXform.Scale = 1.0;
            _batchXform.RotationDeg = 0.0;
            _batchXform.Tx = 0.0;
            _batchXform.Ty = 0.0;
            _batchXform.StepId = BatchStepId;
            _batchXformComputed = true;

            TraceBatch($"[batch] anchors-ready file='{System.IO.Path.GetFileName(CurrentImagePath ?? string.Empty)}' M1=({detectedM1.X:0.0},{detectedM1.Y:0.0}) M2=({detectedM2.X:0.0},{detectedM2.Y:0.0}) scores=({_batchAnchorM1Score},{_batchAnchorM2Score})");
            GuiLog.Info($"[anchors] m1Score={_batchAnchorM1Score} m2Score={_batchAnchorM2Score} anchorsOk={_batchAnchorsOk}");
            GuiLog.Info($"[xform] deg=0 scale=1 dx=0 dy=0 anchorsOk={_batchAnchorsOk}");
        }

        public string? BatchFolder
        {
            get => _batchFolder;
            set
            {
                if (!string.Equals(_batchFolder, value, StringComparison.Ordinal))
                {
                    _batchFolder = value;
                    OnPropertyChanged();
                    UpdateCanStart();
                    _annotatedOutputDir = null;
                }
            }
        }

        public bool CanStartBatch
        {
            get => _canStartBatch;
            private set
            {
                if (_canStartBatch != value)
                {
                    _canStartBatch = value;
                    OnPropertyChanged();
                    UpdateBatchCommandStates();
                }
            }
        }

        public string BatchSummary
        {
            get => _batchSummary;
            private set
            {
                if (!string.Equals(_batchSummary, value, StringComparison.Ordinal))
                {
                    _batchSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BatchStatus
        {
            get => _batchStatus;
            private set
            {
                if (!string.Equals(_batchStatus, value, StringComparison.Ordinal))
                {
                    _batchStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImageSource? BatchImageSource
        {
            get => _batchImageSource;
            private set
            {
                if (!Equals(_batchImageSource, value))
                {
                    _batchImageSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImageSource? BatchHeatmapSource
        {
            get => _batchHeatmapSource;
            private set
            {
                if (!Equals(_batchHeatmapSource, value))
                {
                    _batchHeatmapSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public int BatchHeatmapRoiIndex
        {
            get => _batchHeatmapRoiIndex;
            private set
            {
                if (_batchHeatmapRoiIndex != value)
                {
                    _batchHeatmapRoiIndex = value;
                    OnPropertyChanged();
                }
                else
                {
                    OnPropertyChanged(nameof(BatchHeatmapRoiIndex));
                }
            }
        }

        public double BaseImageActualWidth
        {
            get => _baseImageActualWidth;
            set
            {
                if (Math.Abs(_baseImageActualWidth - value) <= 0.01)
                {
                    return;
                }

                _baseImageActualWidth = value;
                OnPropertyChanged();
                UpdateBatchMeasuredFlag();
            }
        }

        public double BaseImageActualHeight
        {
            get => _baseImageActualHeight;
            set
            {
                if (Math.Abs(_baseImageActualHeight - value) <= 0.01)
                {
                    return;
                }

                _baseImageActualHeight = value;
                OnPropertyChanged();
                UpdateBatchMeasuredFlag();
            }
        }

        public double CanvasRoiActualWidth
        {
            get => _canvasRoiActualWidth;
            set
            {
                if (Math.Abs(_canvasRoiActualWidth - value) <= 0.01)
                {
                    return;
                }

                _canvasRoiActualWidth = value;
                OnPropertyChanged();
                UpdateBatchMeasuredFlag();
            }
        }

        public double CanvasRoiActualHeight
        {
            get => _canvasRoiActualHeight;
            set
            {
                if (Math.Abs(_canvasRoiActualHeight - value) <= 0.01)
                {
                    return;
                }

                _canvasRoiActualHeight = value;
                OnPropertyChanged();
                UpdateBatchMeasuredFlag();
            }
        }

        private void UpdateBatchMeasuredFlag()
        {
            bool measured = _baseImageActualWidth > 0 && _baseImageActualHeight > 0 && _canvasRoiActualWidth > 0 && _canvasRoiActualHeight > 0;
            if (measured == _batchCanvasMeasured)
            {
                return;
            }

            _batchCanvasMeasured = measured;
            if (_batchCanvasMeasured)
            {
                GuiLog.Info($"[batch-ui] canvas measured base=({_baseImageActualWidth:0.##}x{_baseImageActualHeight:0.##}) roi=({_canvasRoiActualWidth:0.##}x{_canvasRoiActualHeight:0.##}) step={_batchStepId} file='{_currentBatchFile}'");
            }
        }

        public async Task WaitUntilMeasuredAsync(FrameworkElement baseImage, FrameworkElement roiCanvas, CancellationToken ct)
        {
            const int maxTries = 50;
            int tries = 0;
            while (!ct.IsCancellationRequested && (baseImage.ActualWidth <= 0 || baseImage.ActualHeight <= 0 || roiCanvas.ActualWidth <= 0 || roiCanvas.ActualHeight <= 0))
            {
                tries++;
                if (tries == 1)
                {
                    GuiLog.Info("[batch-ui] WaitUntilMeasured start");
                }

                await Task.Delay(20, ct).ConfigureAwait(false);
                if (tries > maxTries)
                {
                    GuiLog.Warn($"[batch-ui] WaitUntilMeasured timeout w={baseImage.ActualWidth}x{baseImage.ActualHeight} cv={roiCanvas.ActualWidth}x{roiCanvas.ActualHeight}");
                    break;
                }
            }

            _batchCanvasMeasured = baseImage.ActualWidth > 0 && baseImage.ActualHeight > 0 && roiCanvas.ActualWidth > 0 && roiCanvas.ActualHeight > 0;
            if (_batchCanvasMeasured)
            {
                _baseImageActualWidth = baseImage.ActualWidth;
                _baseImageActualHeight = baseImage.ActualHeight;
                _canvasRoiActualWidth = roiCanvas.ActualWidth;
                _canvasRoiActualHeight = roiCanvas.ActualHeight;
                GuiLog.Info($"[batch-ui] WaitUntilMeasured ready base=({_baseImageActualWidth:0.##}x{_baseImageActualHeight:0.##}) roi=({_canvasRoiActualWidth:0.##}x{_canvasRoiActualHeight:0.##})");
            }
        }

        public int BaseImagePixelWidth
        {
            get => _baseImagePixelWidth;
            set
            {
                if (_baseImagePixelWidth == value)
                {
                    return;
                }

                _baseImagePixelWidth = value;
                OnPropertyChanged();
            }
        }

        public int BaseImagePixelHeight
        {
            get => _baseImagePixelHeight;
            set
            {
                if (_baseImagePixelHeight == value)
                {
                    return;
                }

                _baseImagePixelHeight = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Toggle to revert to the legacy placement logic when investigating regressions.
        /// Bind this from XAML (e.g. a CheckBox) for quick rollback tests.
        /// </summary>
        public bool UseCanvasPlacementForBatchHeatmap
        {
            get => _useCanvasPlacementForBatchHeatmap;
            set
            {
                if (_useCanvasPlacementForBatchHeatmap == value)
                {
                    return;
                }

                _useCanvasPlacementForBatchHeatmap = value;
                OnPropertyChanged();
            }
        }

        public int HeatmapCutoffPercent
        {
            get => _heatmapCutoffPercent;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (_heatmapCutoffPercent != clamped)
                {
                    _heatmapCutoffPercent = clamped;
                    OnPropertyChanged();
                    UpdateHeatmapThreshold();
                }
            }
        }

        public string HeatmapInfo
        {
            get => _heatmapInfo;
            private set
            {
                if (!string.Equals(_heatmapInfo, value, StringComparison.Ordinal))
                {
                    _heatmapInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public double BatchPausePerRoiSeconds
        {
            get => _batchPausePerRoiSeconds;
            set
            {
                var clamped = value;

                if (clamped < BatchPauseMinSeconds)
                {
                    clamped = BatchPauseMinSeconds;
                }
                else if (clamped > BatchPauseMaxSeconds)
                {
                    clamped = BatchPauseMaxSeconds;
                }

                if (Math.Abs(_batchPausePerRoiSeconds - clamped) < 0.0001)
                {
                    return;
                }

                _batchPausePerRoiSeconds = clamped;
                OnPropertyChanged();
            }
        }

        public bool ShowMaster1Pattern
        {
            get => _showMaster1Pattern;
            set
            {
                if (_showMaster1Pattern == value)
                {
                    return;
                }

                _showMaster1Pattern = value;
                OnPropertyChanged(nameof(ShowMaster1Pattern));
            }
        }

        public bool ShowMaster1Inspection
        {
            get => _showMaster1Inspection;
            set
            {
                if (_showMaster1Inspection == value)
                {
                    return;
                }

                _showMaster1Inspection = value;
                OnPropertyChanged(nameof(ShowMaster1Inspection));
            }
        }

        public bool ShowMaster2Pattern
        {
            get => _showMaster2Pattern;
            set
            {
                if (_showMaster2Pattern == value)
                {
                    return;
                }

                _showMaster2Pattern = value;
                OnPropertyChanged(nameof(ShowMaster2Pattern));
            }
        }

        public bool ShowMaster2Inspection
        {
            get => _showMaster2Inspection;
            set
            {
                if (_showMaster2Inspection == value)
                {
                    return;
                }

                _showMaster2Inspection = value;
                OnPropertyChanged(nameof(ShowMaster2Inspection));
            }
        }

        public bool ShowInspectionRoi
        {
            get => _showInspectionRoi;
            set
            {
                if (_showInspectionRoi == value)
                {
                    return;
                }

                _showInspectionRoi = value;
                OnPropertyChanged(nameof(ShowInspectionRoi));
            }
        }

        public IReadOnlyList<RoiShape> AvailableRoiShapes => _availableRoiShapes;

        public IReadOnlyList<MasterRoleOption> Master1RoleOptions { get; }

        public IReadOnlyList<MasterRoleOption> Master2RoleOptions { get; }

        public MasterRoleOption? SelectedMaster1Role
        {
            get => _selectedMaster1Role;
            set
            {
                if (_selectedMaster1Role == value)
                {
                    return;
                }

                _selectedMaster1Role = value;
                OnPropertyChanged();
            }
        }

        public MasterRoleOption? SelectedMaster2Role
        {
            get => _selectedMaster2Role;
            set
            {
                if (_selectedMaster2Role == value)
                {
                    return;
                }

                _selectedMaster2Role = value;
                OnPropertyChanged();
            }
        }

        public RoiShape SelectedMaster1Shape
        {
            get => _selectedMaster1Shape;
            set
            {
                if (_selectedMaster1Shape == value)
                {
                    return;
                }

                _selectedMaster1Shape = value;
                OnPropertyChanged();
            }
        }

        public RoiShape SelectedMaster2Shape
        {
            get => _selectedMaster2Shape;
            set
            {
                if (_selectedMaster2Shape == value)
                {
                    return;
                }

                _selectedMaster2Shape = value;
                OnPropertyChanged();
            }
        }

        public bool IsMaster1Editing
        {
            get => _isMaster1Editing;
            private set
            {
                if (_isMaster1Editing == value)
                {
                    return;
                }

                _isMaster1Editing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Master1EditButtonText));
            }
        }

        public bool IsMaster2Editing
        {
            get => _isMaster2Editing;
            private set
            {
                if (_isMaster2Editing == value)
                {
                    return;
                }

                _isMaster2Editing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Master2EditButtonText));
            }
        }

        public string Master1EditButtonText => IsMaster1Editing ? "Save Master ROI" : "Edit Master ROI";

        public string Master2EditButtonText => IsMaster2Editing ? "Save Master ROI" : "Edit Master ROI";

        public bool IsImageLoaded
        {
            get => _isImageLoaded;
            set
            {
                if (_isImageLoaded == value)
                {
                    return;
                }

                _isImageLoaded = value;
                OnPropertyChanged();
            }
        }

        public RoiModel? Inspection1
        {
            get => _inspection1;
            private set => SetInspectionRoi(ref _inspection1, value, nameof(Inspection1));
        }

        public RoiModel? Inspection2
        {
            get => _inspection2;
            private set => SetInspectionRoi(ref _inspection2, value, nameof(Inspection2));
        }

        public RoiModel? Inspection3
        {
            get => _inspection3;
            private set => SetInspectionRoi(ref _inspection3, value, nameof(Inspection3));
        }

        public RoiModel? Inspection4
        {
            get => _inspection4;
            private set => SetInspectionRoi(ref _inspection4, value, nameof(Inspection4));
        }

        public InspectionRoiConfig? SelectedInspectionRoi
        {
            get => _selectedInspectionRoi;
            set
            {
                if (!ReferenceEquals(_selectedInspectionRoi, value))
                {
                    _selectedInspectionRoi = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedInspectionShape));
                    OnPropertyChanged(nameof(ActiveInspectionRoiModel));
                    OnPropertyChanged(nameof(ActiveInspectionRoiImageRectPx));
                    UpdateSelectedRoiState();
                    OpenDatasetFolderCommand.RaiseCanExecuteChanged();
                    RemoveSelectedPreviewCommand.RaiseCanExecuteChanged();

                    if (value != null)
                    {
                        RoleId = "Inspection";
                        var normalizedKey = NormalizeInspectionKey(value.ModelKey, value.Index);
                        if (!string.Equals(RoiId, normalizedKey, StringComparison.Ordinal))
                        {
                            RoiId = normalizedKey;
                        }
                        if (!string.Equals(value.ModelKey, normalizedKey, StringComparison.Ordinal))
                        {
                            value.ModelKey = normalizedKey;
                        }

                        _log($"[ui] SelectedInspectionRoi -> {value.DisplayName} idx={value.Index} key={value.ModelKey}");
                        try
                        {
                            LocalThreshold = value.CalibratedThreshold ?? value.ThresholdDefault;
                            _log($"[ui] LocalThreshold <- {LocalThreshold:0.###} (SelectedInspectionRoi)");
                        }
                        catch
                        {
                            // tolerate missing properties on older builds
                        }

                        ClearInferenceUi("[ui] selection changed");
                        _ = ActivateCanvasIndexAsync(value.Index);
                        _ = RefreshBackendStateForRoiAsync(value);
                    }
                }
            }
        }

        // Si SelectedInspectionRoi es null => "square"; si no, usa el enum (no anulable) y conviértelo a string.
        public string SelectedInspectionShape =>
            SelectedInspectionRoi != null
                ? SelectedInspectionRoi.Shape.ToString().ToLowerInvariant()
                : "square";

        public RoiModel? ActiveInspectionRoiModel => GetActiveInspectionRoiModel();

        public Int32Rect? ActiveInspectionRoiImageRectPx
        {
            get
            {
                var roi = ActiveInspectionRoiModel;
                return roi != null ? BuildImageRectPx(roi) : null;
            }
        }

        public void SetInspectionRoisCollection(ObservableCollection<InspectionRoiConfig>? rois)
        {
            if (ReferenceEquals(_inspectionRois, rois))
            {
                return;
            }

            if (_inspectionRois != null)
            {
                DetachInspectionRoiHandlers(_inspectionRois);
            }

            _inspectionRois = rois;
            InspectionRois = rois ?? new ObservableCollection<InspectionRoiConfig>();
            OnPropertyChanged(nameof(InspectionRois));

            if (_inspectionRois != null)
            {
                AttachInspectionRoiHandlers(_inspectionRois);
                foreach (var roi in _inspectionRois)
                {
                    InitializeInspectionRoi(roi);
                    if (_initialized && !_isInitializing)
                    {
                        _ = RefreshRoiDatasetStateAsync(roi);
                    }
                }
                if (_inspectionRois.Count > 0)
                {
                    SelectedInspectionRoi = _inspectionRois.FirstOrDefault();
                }
            }
            else
            {
                SelectedInspectionRoi = null;
            }

            ClearBaselines();
            UpdateSelectedRoiState();
            InvokeOnUi(RedrawOverlays);
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            UpdateGlobalBadge();
        }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            lock (_initLock)
            {
                if (_initialized)
                {
                    return Task.CompletedTask;
                }

                if (_initializationTask == null || _initializationTask.IsCompleted)
                {
                    _initializationTask = InitializeCoreAsync(ct);
                }

                return _initializationTask;
            }
        }

        private async Task InitializeCoreAsync(CancellationToken ct)
        {
            if (_initialized)
            {
                return;
            }

            _isInitializing = true;
            GuiLog.Info($"[workflow] InitializeAsync START"); // CODEX: FormattableString compatibility.

            try
            {
                var snapshot = InspectionRois?.ToList() ?? new List<InspectionRoiConfig>();
                foreach (var roi in snapshot)
                {
                    ct.ThrowIfCancellationRequested();

                    if (roi == null)
                    {
                        continue;
                    }

                    GuiLog.Info($"[dataset:init] ROI='{roi.DisplayName}' backend refresh");
                    await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
                }

                _initialized = true;
                GuiLog.Info($"[workflow] InitializeAsync END"); // CODEX: FormattableString compatibility.
            }
            catch (OperationCanceledException)
            {
                GuiLog.Warn($"[workflow] InitializeAsync cancelled"); // CODEX: FormattableString compatibility.
                throw;
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[workflow] InitializeAsync failed", ex); // CODEX: FormattableString compatibility.
                throw;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        public void SetInspectionRoiModels(RoiModel? inspection1, RoiModel? inspection2, RoiModel? inspection3, RoiModel? inspection4)
        {
            Inspection1 = inspection1;
            Inspection2 = inspection2;
            Inspection3 = inspection3;
            Inspection4 = inspection4;
        }

        private void LogLayoutAlignmentSnapshot(MasterLayout layout)
        {
            var m1Center = layout.Master1Pattern?.GetCenter() ?? (double.NaN, double.NaN);
            var m2Center = layout.Master2Pattern?.GetCenter() ?? (double.NaN, double.NaN);
            var m1Angle = layout.Master1Pattern?.AngleDeg ?? double.NaN;
            var m2Angle = layout.Master2Pattern?.AngleDeg ?? double.NaN;

            LogAlign(FormattableString.Invariant(
                $"[LAYOUT] M1_base=({m1Center.Item1:0.###},{m1Center.Item2:0.###}) M2_base=({m2Center.Item1:0.###},{m2Center.Item2:0.###}) m1_base_angle={m1Angle:0.###} m2_base_angle={m2Angle:0.###}"));

            if (layout.InspectionRois == null)
            {
                return;
            }

            foreach (var cfg in layout.InspectionRois.OrderBy(r => r.Index))
            {
                var baseline = cfg.Index switch
                {
                    1 => layout.Inspection1,
                    2 => layout.Inspection2,
                    3 => layout.Inspection3,
                    4 => layout.Inspection4,
                    _ => null
                };

                var center = baseline?.GetCenter() ?? (double.NaN, double.NaN);
                var angle = baseline?.AngleDeg ?? double.NaN;

                LogAlign(FormattableString.Invariant(
                    $"[LAYOUT] roi_index={cfg.Index} id={cfg.Id} model_key={cfg.ModelKey} enabled={cfg.Enabled} anchor_master={(int)cfg.AnchorMaster} baseline_center=({center.Item1:0.###},{center.Item2:0.###}) baseline_angle={angle:0.###}"));
            }
        }

        public void SetMasterLayout(MasterLayout? layout)
        {
            _layout = layout;
            _layoutOriginal = layout?.DeepClone();
            InvalidateMasterPatternCache();

            if (_layoutOriginal != null)
            {
                LogLayoutAlignmentSnapshot(_layoutOriginal);
            }

            if (layout != null)
            {
                SetInspectionRoiModels(
                    layout.Inspection1?.Clone(),
                    layout.Inspection2?.Clone(),
                    layout.Inspection3?.Clone(),
                    layout.Inspection4?.Clone());
            }

            CreateMaster1RoiCommand.RaiseCanExecuteChanged();
            ToggleEditMaster1RoiCommand.RaiseCanExecuteChanged();
            RemoveMaster1RoiCommand.RaiseCanExecuteChanged();

            CreateMaster2RoiCommand.RaiseCanExecuteChanged();
            ToggleEditMaster2RoiCommand.RaiseCanExecuteChanged();
            RemoveMaster2RoiCommand.RaiseCanExecuteChanged();
        }

        public void InitializeBatchSession()
        {
            if (_layoutOriginal == null && _layout != null)
            {
                _layoutOriginal = _layout.DeepClone();
            }
        }

        private void EnsureLayoutOriginalSnapshot()
        {
            if (_layoutOriginal == null && _layout != null)
            {
                _layoutOriginal = _layout.DeepClone();
            }
        }

        private void InitializeBatchBaselineRois()
        {
            for (int idx = 1; idx <= _batchBaselineRois.Length; idx++)
            {
                var roi = _layoutOriginal != null
                    ? GetLayoutBaselineRoi(_layoutOriginal, GetInspectionConfigByIndex(idx), GetInspectionRoiModelByIndex(idx))
                    : null
                          ?? GetInspectionRoiModelByIndex(idx);
                _batchBaselineRois[idx - 1] = roi?.DeepClone();
            }

            try
            {
                var snapshot = string.Join(", ", Enumerable.Range(1, _batchBaselineRois.Length)
                    .Select(i =>
                    {
                        var roi = _batchBaselineRois[i - 1];
                        if (roi == null)
                        {
                            return FormattableString.Invariant($"{i}=null");
                        }

                        double cx = roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus ? roi.CX : roi.X;
                        double cy = roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus ? roi.CY : roi.Y;
                        double r = roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus ? roi.R : Math.Max(roi.Width, roi.Height) * 0.5;
                        double rin = roi.Shape == RoiShape.Annulus ? roi.RInner : 0.0;
                        return FormattableString.Invariant($"{i}=(cx={cx:0.###},cy={cy:0.###},r={r:0.###},rin={rin:0.###})");
                    }));

                TraceBatch($"[batch] baseline-roi snapshot: {snapshot}");
            }
            catch
            {
                // logging must never break batch start
            }
        }

        private void ClearBatchBaselineRois()
        {
            for (int i = 0; i < _batchBaselineRois.Length; i++)
            {
                _batchBaselineRois[i] = null;
            }
        }

        private RoiModel? GetBatchBaselineRoi(int index)
        {
            if (index < 1 || index > _batchBaselineRois.Length)
            {
                return null;
            }

            return _batchBaselineRois[index - 1];
        }

        public void SetInspectionAutoRepositionEnabled(string? roiId, bool enable)
        {
            // Hook for future auto-reposition toggles. No-op by default.
        }

        public void SetInspectionAutoRepositionEnabled(int roiId, bool enable)
            => SetInspectionAutoRepositionEnabled(roiId.ToString(System.Globalization.CultureInfo.InvariantCulture), enable);

        private void InspectionRoisCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (InspectionRoiConfig roi in e.OldItems)
                {
                    DetachInspectionRoiHandler(roi);
                    _lastFitResultsByRoi.Remove(roi);
                    _lastCalibResultsByRoi.Remove(roi);
                }
            }

            if (e.NewItems != null)
            {
                foreach (InspectionRoiConfig roi in e.NewItems)
                {
                    AttachInspectionRoiHandler(roi);
                    InitializeInspectionRoi(roi);
                }
            }

            if (_inspectionRois != null && (SelectedInspectionRoi == null || !_inspectionRois.Contains(SelectedInspectionRoi)))
            {
                SelectedInspectionRoi = _inspectionRois.FirstOrDefault();
            }

            InvokeOnUi(RedrawOverlays);
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            UpdateGlobalBadge();
        }

        private void AttachInspectionRoiHandlers(ObservableCollection<InspectionRoiConfig> rois)
        {
            if (rois == null)
            {
                return;
            }

            rois.CollectionChanged += InspectionRoisCollectionChanged;
            foreach (var roi in rois)
            {
                AttachInspectionRoiHandler(roi);
            }
        }

        private void DetachInspectionRoiHandlers(ObservableCollection<InspectionRoiConfig> rois)
        {
            if (rois == null)
            {
                return;
            }

            rois.CollectionChanged -= InspectionRoisCollectionChanged;
            foreach (var roi in rois)
            {
                DetachInspectionRoiHandler(roi);
            }
        }

        private void AttachInspectionRoiHandler(InspectionRoiConfig roi)
        {
            if (roi == null)
            {
                return;
            }

            roi.PropertyChanged += InspectionRoiPropertyChanged;
        }

        private void DetachInspectionRoiHandler(InspectionRoiConfig roi)
        {
            if (roi == null)
            {
                return;
            }

            roi.PropertyChanged -= InspectionRoiPropertyChanged;
        }

        private void InitializeInspectionRoi(InspectionRoiConfig roi)
        {
            if (roi == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(roi.Id))
            {
                roi.Id = $"Inspection_{roi.Index}";
            }

            if (string.IsNullOrWhiteSpace(roi.DatasetStatus))
            {
                roi.DatasetStatus = "Syncing dataset...";
            }

            roi.OkPreview = new ObservableCollection<DatasetPreviewItem>();
            roi.NgPreview = new ObservableCollection<DatasetPreviewItem>();
        }

        private void SetInspectionRoi(ref RoiModel? field, RoiModel? value, string propertyName)
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;

            OnPropertyChanged(propertyName);
            OnPropertyChanged(nameof(ActiveInspectionRoiModel));
            OnPropertyChanged(nameof(ActiveInspectionRoiImageRectPx));
        }

        private RoiModel? GetInspectionRoiModelByIndex(int index) => index switch
        {
            1 => Inspection1,
            2 => Inspection2,
            3 => Inspection3,
            4 => Inspection4,
            _ => null
        };

        public RoiModel? GetInspectionRoiByIndex(int idx)
        {
            return GetInspectionRoiModelByIndex(idx);
        }

        public Int32Rect? GetInspectionRoiImageRectPx(int idx)
        {
            var roi = GetInspectionRoiModelByIndex(idx);
            if (roi == null)
            {
                return null;
            }

            var roiForRect = GetBatchTransformedRoi(roi);
            return BuildImageRectPx(roiForRect);
        }

        public Rect? GetInspectionRoiCanvasRect(int roiIndex)
        {
            var roiModel = GetInspectionRoiModelByIndex(roiIndex);
            Rect imgRect;
            if (roiModel != null)
            {
                var roiForCanvas = _isBatchRunning ? GetBatchTransformedRoi(roiModel) : roiModel;
                imgRect = roiForCanvas.Shape == RoiShape.Circle || roiForCanvas.Shape == RoiShape.Annulus
                    ? new Rect(roiForCanvas.CX - roiForCanvas.R, roiForCanvas.CY - roiForCanvas.R, roiForCanvas.R * 2.0, roiForCanvas.R * 2.0)
                    : new Rect(roiForCanvas.Left, roiForCanvas.Top, roiForCanvas.Width, roiForCanvas.Height);
            }
            else
            {
                var rectPx = GetInspectionRoiImageRectPx(roiIndex);
                if (rectPx == null)
                {
                    return null;
                }

                imgRect = new Rect(rectPx.Value.X, rectPx.Value.Y, rectPx.Value.Width, rectPx.Value.Height);
            }

            if (TryGetBatchPlacementTransform(out var uniform, out var offsetX, out var offsetY, out _, out _))
            {
                return new Rect(offsetX + imgRect.X * uniform, offsetY + imgRect.Y * uniform, imgRect.Width * uniform, imgRect.Height * uniform);
            }

            var source = BatchImageSource ?? BatchHeatmapSource;
            var fallback = ImgToCanvas(source ?? new DrawingImage(), CanvasRoiActualWidth, CanvasRoiActualHeight);
            return RectImgToCanvas(imgRect, fallback);
        }

        public InspectionRoiConfig? GetInspectionRoiConfig(int idx)
        {
            return GetInspectionConfigByIndex(idx);
        }

        public bool ShouldPlaceBatchPlacement(string? reason)
        {
            if (reason != null && reason.Contains("BatchHeatmapSource", StringComparison.Ordinal) && _batchPlacedForStep == _batchStepId)
            {
                return false;
            }

            if (reason != null && reason.Contains("BatchRowOk", StringComparison.Ordinal))
            {
                _batchPlacedForStep = (int)_batchStepId;
            }

            return true;
        }

        private RoiBaseline? TryGetBaseline(string roiId)
        {
            if (string.IsNullOrWhiteSpace(roiId))
            {
                return null;
            }

            lock (_baselineLock)
            {
                return _baselines.TryGetValue(roiId, out var b) ? b : null;
            }
        }

        private static string FormatPoint(Point p) => $"({p.X:0.###},{p.Y:0.###})";

        private void SetBaseline(RoiBaseline baseline, string reason)
        {
            lock (_baselineLock)
            {
                _baselines[baseline.RoiId] = baseline;
            }

            GuiLog.Info($"[seed] roi='{baseline.RoiId}' baseRect={RectToStr(baseline.BaseRect)} C={FormatPoint(baseline.Center)} R={baseline.R:0.###} Rin={baseline.Rin:0.###} Ang={baseline.AngleDeg:0.###} reason={reason}");
        }

        private void ClearBaselines()
        {
            lock (_baselineLock)
            {
                _baselines.Clear();
            }
        }

        private string ResolveBaselineKey(InspectionRoiConfig? config, RoiModel roi)
        {
            if (config != null && !string.IsNullOrWhiteSpace(config.Id))
            {
                return config.Id;
            }

            if (!string.IsNullOrWhiteSpace(roi.Id))
            {
                return roi.Id;
            }

            return $"Inspection_{config?.Index ?? 0}";
        }

        private RoiBaseline SeedBaselineForRoi(InspectionRoiConfig? config, RoiModel roi, string reason)
        {
            var key = ResolveBaselineKey(config, roi);
            var baseRect = BuildRoiRect(roi);
            var center = roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus
                ? new Point(roi.CX, roi.CY)
                : new Point(roi.X, roi.Y);
            var baseline = new RoiBaseline(key, baseRect, center, roi.R, roi.RInner, roi.AngleDeg);
            SetBaseline(baseline, reason);
            return baseline;
        }

        private RoiBaseline EnsureBaselineForRoi(InspectionRoiConfig? config, RoiModel roi, string reason)
        {
            var key = ResolveBaselineKey(config, roi);
            var cached = TryGetBaseline(key);
            if (cached != null)
            {
                return cached;
            }

            RoiModel? source = roi;
            if (_layoutOriginal != null)
            {
                var stable = GetLayoutBaselineRoi(_layoutOriginal, config, roi);
                if (stable != null)
                {
                    source = stable;
                }
            }

            return SeedBaselineForRoi(config, source ?? roi, reason);
        }

        private RoiModel? GetLayoutBaselineRoi(MasterLayout layout, InspectionRoiConfig? config, RoiModel? roi)
        {
            if (layout == null)
            {
                return null;
            }

            RoiModel? baseline = null;
            if (config != null)
            {
                baseline = config.Index switch
                {
                    1 => layout.Inspection1,
                    2 => layout.Inspection2,
                    3 => layout.Inspection3,
                    4 => layout.Inspection4,
                    _ => null
                };
            }

            if (baseline == null && !string.IsNullOrWhiteSpace(roi?.Id))
            {
                baseline = new[]
                    {
                        layout.Inspection1,
                        layout.Inspection2,
                        layout.Inspection3,
                        layout.Inspection4,
                        layout.Inspection
                    }
                    .FirstOrDefault(r => r != null && string.Equals(r.Id, roi.Id, StringComparison.OrdinalIgnoreCase));
            }

            return baseline;
        }

        private void SeedInspectionBaselines(string reason)
        {
            if (_inspectionRois == null)
            {
                return;
            }

            foreach (var rcfg in _inspectionRois.Where(r => r.Enabled))
            {
                var roi = GetInspectionRoiModelByIndex(rcfg.Index);

                if (roi == null)
                {
                    continue;
                }

                var baseline = _layoutOriginal != null ? GetLayoutBaselineRoi(_layoutOriginal, rcfg, roi) : null;
                SeedBaselineForRoi(rcfg, baseline ?? roi, reason);
            }
        }

        public bool IsBatchAnchorReady => _batchAnchorReadyForStep == _batchStepId;

        private static Rect BuildRoiRect(RoiModel? roi)
        {
            if (roi == null)
            {
                return Rect.Empty;
            }

            return roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus
                ? new Rect(roi.CX - roi.R, roi.CY - roi.R, roi.R * 2.0, roi.R * 2.0)
                : new Rect(roi.Left, roi.Top, roi.Width, roi.Height);
        }

        private static RoiModel BuildBaselineModel(RoiModel roi, RoiBaseline baseline)
        {
            var baselineModel = roi.Clone();
            baselineModel.Width = baseline.BaseRect.Width;
            baselineModel.Height = baseline.BaseRect.Height;
            baselineModel.Left = baseline.BaseRect.Left;
            baselineModel.Top = baseline.BaseRect.Top;
            baselineModel.X = baseline.Center.X;
            baselineModel.Y = baseline.Center.Y;
            baselineModel.CX = baseline.Center.X;
            baselineModel.CY = baseline.Center.Y;
            baselineModel.R = baseline.R;
            baselineModel.RInner = baseline.Rin;
            baselineModel.AngleDeg = baseline.AngleDeg;
            return baselineModel;
        }

        public void TraceBatchHeatmapPlacement(string reason, int roiIndex, Rect? canvasRect)
        {
            try
            {
                if (roiIndex <= 0)
                {
                    return;
                }

                // ROI en coordenadas de imagen (px)
                var rectImg = GetInspectionRoiImageRectPx(roiIndex);

                // Config y modelo del ROI
                var roiConfig = GetInspectionConfigByIndex(roiIndex);
                bool enabled = roiConfig?.Enabled ?? false;
                var roiModel = GetInspectionRoiModelByIndex(roiIndex);

                // Info de batch actual
                string fileName = string.IsNullOrWhiteSpace(CurrentImagePath)
                    ? "<none>"
                    : System.IO.Path.GetFileName(CurrentImagePath);

                int totalRows = _batchRows?.Count ?? 0;

                // Primer bloque: métricas globales de imagen/canvas
                TraceBatch(
                    $"[batch] img px=({BaseImagePixelWidth}x{BaseImagePixelHeight}) " +
                    $"vis=({BaseImageActualWidth:0.##}x{BaseImageActualHeight:0.##}) " +
                    $"canvas=({CanvasRoiActualWidth:0.##}x{CanvasRoiActualHeight:0.##}) " +
                    $"useCanvas={UseCanvasPlacementForBatchHeatmap}");

                // Rectángulos en texto
                string rectImgText = rectImg.HasValue
                    ? $"{rectImg.Value.X:0.##},{rectImg.Value.Y:0.##},{rectImg.Value.Width:0.##},{rectImg.Value.Height:0.##}"
                    : "null";

                string rectCanvasText = canvasRect.HasValue
                    ? $"{canvasRect.Value.Left:0.##},{canvasRect.Value.Top:0.##},{canvasRect.Value.Width:0.##},{canvasRect.Value.Height:0.##}"
                    : "null";

                string xformText = _isBatchRunning
                    ? FormattableString.Invariant($"S={_batchXform.Scale:0.000},R={_batchXform.RotationDeg:0.00},T=({_batchXform.Tx:0.0},{_batchXform.Ty:0.0})")
                    : "identity";

                // Estado del modelo (si existe)
                string modelText = roiModel != null
                    ? FormattableString.Invariant(
                        $"shape={roiModel.Shape} CX={roiModel.CX:0.##} CY={roiModel.CY:0.##} R={roiModel.R:0.##} L={roiModel.Left:0.##} T={roiModel.Top:0.##} W={roiModel.Width:0.##} H={roiModel.Height:0.##} Angle={roiModel.AngleDeg:0.##}")
                    : "null";

                // Segundo bloque: traza completa de la colocación
                TraceBatch(
                    $"[batch] place roiIdx={roiIndex} enabled={enabled} " +
                    $"reason={reason} row={CurrentRowIndex:000}/{totalRows:000} file='{fileName}' " +
                    $"rectImg=({rectImgText}) rectCanvas=({rectCanvasText}) model=({modelText}) xform({xformText})");
            }
            catch (Exception ex)
            {
                // Nunca romper el flujo por culpa de un log
                TraceBatch("[batch] TraceBatchHeatmapPlacement failed: " + ex.Message);
            }
        }

        private static string RectToStr(Rect r) => r.IsEmpty ? "(empty)" : $"({r.X:0},{r.Y:0},{r.Width:0},{r.Height:0})";

        private static string Sanitize(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }

            return s;
        }

        private static bool NearlyEqual(double a, double b, double eps = 0.5) => Math.Abs(a - b) <= eps;

        private static bool NearlyEqualRects(Rect a, Rect b, double eps = 0.5)
        {
            return NearlyEqual(a.X, b.X, eps) && NearlyEqual(a.Y, b.Y, eps)
                                         && NearlyEqual(a.Width, b.Width, eps) && NearlyEqual(a.Height, b.Height, eps);
        }

        private static Rect InflateRect(Rect r, double dx, double dy)
        {
            r.Inflate(dx, dy);
            return r;
        }

        private async Task PlaceBatchFinalAsync(string reason, CancellationToken ct)
        {
            var imgName = System.IO.Path.GetFileName(CurrentImagePath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(_currentBatchFile) && !string.Equals(imgName, _currentBatchFile, StringComparison.OrdinalIgnoreCase))
            {
                GuiLog.Warn($"[batch-ui] stale place ignored: step={_batchStepId} reason={reason} file='{imgName}' current='{_currentBatchFile}'");
                return;
            }

            if (!_isBatchRunning)
            {
                return;
            }

            if (!_batchCanvasMeasured)
            {
                GuiLog.Warn($"[batch-ui] place skipped: canvas not measured step={_batchStepId} file='{imgName}' reason={reason}");
                return;
            }

            if (!_batchAnchorM1Ready || !_batchAnchorM2Ready)
            {
                GuiLog.Warn($"[batch-ui] place skipped: anchors not ready step={_batchStepId} file='{imgName}' reason={reason}");
                return;
            }

            if (!TryBuildPlacementInput(out var placementInput, logError: false))
            {
                GuiLog.Warn($"[batch-ui] place skipped: anchors unavailable step={_batchStepId} file='{imgName}' reason={reason}");
                return;
            }

            int token = _batchPlacementToken;
            if (_batchPlacementTokenPlaced == token)
            {
                TraceBatch($"[batch-ui] place ignored: already placed token={token} reason={reason}");
                return;
            }

            if (_inspectionRois != null)
            {
                RepositionInspectionRois(placementInput);

                if (_inspectionRois.Count >= 2)
                {
                    var first = GetInspectionRoiModelByIndex(1);
                    var second = GetInspectionRoiModelByIndex(2);
                    if (first != null && second != null)
                    {
                        var r1Img = EnsureBaselineForRoi(GetInspectionConfigByIndex(1), first, "guard").BaseRect;
                        var r2Img = EnsureBaselineForRoi(GetInspectionConfigByIndex(2), second, "guard").BaseRect;
                        var r1Cv = BuildRoiRect(first);
                        var r2Cv = BuildRoiRect(second);
                        if (NearlyEqualRects(r1Img, r2Img) || NearlyEqualRects(r1Cv, r2Cv))
                        {
                            GuiLog.Warn($"[batch-ui] guard: idx=2 would share RECTimg/canvas with idx=1; reason={reason} step={_batchStepId} file='{imgName}'");
                        }
                    }
                }
            }

            InvokeOnUi(RedrawOverlays);

            _batchPlacementTokenPlaced = token;

            if (BatchRowOk.HasValue)
            {
                NotifyBatchCaption(imgName, BatchRowOk.Value);
            }

            await CaptureCanvasIfNeededAsync(imgName, "afterFinalPlacement").ConfigureAwait(false);
        }

        private bool ShouldCaptureCanvas(string reason, out bool outOfBounds)
        {
            outOfBounds = false;
            if (!_batchCanvasMeasured)
            {
                return false;
            }

            if (_inspectionRois != null)
            {
                foreach (var rcfg in _inspectionRois)
                {
                    var roi = GetInspectionModelByIndex(rcfg.Index);
                    if (roi == null)
                    {
                        continue;
                    }

                    var r = BuildRoiRect(roi);
                    if (r.X < 0 || r.Y < 0 || r.Right > _baseImagePixelWidth || r.Bottom > _baseImagePixelHeight)
                    {
                        outOfBounds = true;
                        break;
                    }
                }
            }

            return outOfBounds || string.Equals(reason, "anchorNotReady", StringComparison.Ordinal);
        }

        public async Task CaptureCanvasIfNeededAsync(string imgName, string reason)
        {
            await _captureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (!_batchCanvasMeasured)
                        {
                            return;
                        }

                        var target = OverlayCanvas;
                        if (target == null)
                        {
                            return;
                        }

                        if (!ShouldCaptureCanvas(reason, out _))
                        {
                            return;
                        }

                        int W = (int)Math.Max(1, target.ActualWidth);
                        int H = (int)Math.Max(1, target.ActualHeight);

                        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(target);

                        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "canvas_captures");
                        System.IO.Directory.CreateDirectory(dir);
                        var path = System.IO.Path.Combine(dir, $"Canvas_{_batchStepId:0000}_{Sanitize(imgName)}_{Sanitize(reason)}.png");

                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
                        enc.Save(fs);

                        GuiLog.Info($"[batch-ui] canvas-captured file='{path}'");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error($"[batch-ui] canvas-capture failed: {ex.Message}", ex);
                    }
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[batch-ui] canvas-capture exception img='{imgName}' reason='{reason}'", ex);
            }
            finally
            {
                _captureGate.Release();
            }
        }

        private bool TryGetBatchPlacementTransform(out double uniformScale, out double offsetX, out double offsetY, out double legacyScaleX, out double legacyScaleY)
        {
            uniformScale = 0;
            offsetX = 0;
            offsetY = 0;
            legacyScaleX = 0;
            legacyScaleY = 0;

            if (BaseImagePixelWidth <= 0 || BaseImagePixelHeight <= 0
                || BaseImageActualWidth <= 0 || BaseImageActualHeight <= 0
                || CanvasRoiActualWidth <= 0 || CanvasRoiActualHeight <= 0)
            {
                return false;
            }

            legacyScaleX = BaseImageActualWidth / BaseImagePixelWidth;
            legacyScaleY = BaseImageActualHeight / BaseImagePixelHeight;

            if (legacyScaleX <= 0 || legacyScaleY <= 0)
            {
                return false;
            }

            uniformScale = Math.Min(legacyScaleX, legacyScaleY);
            if (uniformScale <= 0)
            {
                return false;
            }

            double drawnW = BaseImagePixelWidth * uniformScale;
            double drawnH = BaseImagePixelHeight * uniformScale;

            double offInsideX = Math.Max(0.0, (BaseImageActualWidth - drawnW) * 0.5);
            double offInsideY = Math.Max(0.0, (BaseImageActualHeight - drawnH) * 0.5);
            double offOuterX = Math.Max(0.0, (CanvasRoiActualWidth - BaseImageActualWidth) * 0.5);
            double offOuterY = Math.Max(0.0, (CanvasRoiActualHeight - BaseImageActualHeight) * 0.5);

            offsetX = offOuterX + offInsideX;
            offsetY = offOuterY + offInsideY;
            return true;
        }

        // CODEX: image -> canvas transform (Uniform + center)
        private static (double scale, double dx, double dy) ImgToCanvas(ImageSource img, double canvasW, double canvasH)
        {
            if (img is not BitmapSource bmp || canvasW <= 0 || canvasH <= 0) return (1, 0, 0);
            var sx = canvasW / bmp.PixelWidth;
            var sy = canvasH / bmp.PixelHeight;
            var s = Math.Min(sx, sy);
            var drawW = bmp.PixelWidth * s;
            var drawH = bmp.PixelHeight * s;
            var dx = (canvasW - drawW) * 0.5;
            var dy = (canvasH - drawH) * 0.5;
            return (s, dx, dy);
        }

        // CODEX: apply transform to a rect in image pixels
        private static Rect RectImgToCanvas(in Rect rImg, in (double s, double dx, double dy) t)
        {
            var (s, dx, dy) = t;
            return new Rect(dx + rImg.X * s, dy + rImg.Y * s, rImg.Width * s, rImg.Height * s);
        }

        private RoiModel? GetActiveInspectionRoiModel()
        {
            if (SelectedInspectionRoi != null)
            {
                var match = GetInspectionRoiModelByIndex(SelectedInspectionRoi.Index);
                if (match != null)
                {
                    return match;
                }
            }

            for (int i = 1; i <= 4; i++)
            {
                var candidate = GetInspectionRoiModelByIndex(i);
                if (candidate != null)
                {
                    return candidate;
                }
            }

            return null;
        }

        private RoiModel GetBatchTransformedRoi(RoiModel source)
        {
            if (!_isBatchRunning || _batchXform.StepId != BatchStepId)
            {
                return source;
            }

            var clone = source.Clone();
            var config = FindInspectionConfigForRoi(source);
            var baseline = EnsureBaselineForRoi(config, source, "export");

            if (!TryBuildPlacementInput(out var placementInput, logError: false))
            {
                return clone;
            }

            var baselineModel = BuildBaselineModel(clone, baseline);
            var anchor = ResolveAnchorMaster(config, source, "batch-export");
            var anchorMap = new Dictionary<string, MasterAnchorChoice>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(baselineModel.Id))
            {
                anchorMap[baselineModel.Id] = anchor;
            }

            var input = placementInput with
            {
                AnchorByRoiId = anchorMap
            };

            var output = RoiPlacementEngine.Place(input, Array.Empty<RoiModel>(), new List<RoiModel> { baselineModel });
            var placed = output.InspectionsPlaced.FirstOrDefault() ?? clone;
            return placed;
        }

        private static Int32Rect? BuildImageRectPx(RoiModel roi)
        {
            if (roi == null)
            {
                return null;
            }

            double left;
            double top;
            double width;
            double height;

            switch (roi.Shape)
            {
                case RoiShape.Circle:
                case RoiShape.Annulus:
                    if (roi.R <= 0)
                    {
                        return null;
                    }

                    width = roi.R * 2.0;
                    height = width;
                    left = roi.CX - roi.R;
                    top = roi.CY - roi.R;
                    break;

                default:
                    width = roi.Width;
                    height = roi.Height;
                    left = roi.Left;
                    top = roi.Top;
                    break;
            }

            if (double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height)
                || double.IsInfinity(left) || double.IsInfinity(top) || double.IsInfinity(width) || double.IsInfinity(height))
            {
                return null;
            }

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            double right = left + width;
            double bottom = top + height;

            if (roi.BaseImgW is double baseW && baseW > 0)
            {
                left = Math.Clamp(left, 0.0, baseW);
                right = Math.Clamp(right, 0.0, baseW);
            }

            if (roi.BaseImgH is double baseH && baseH > 0)
            {
                top = Math.Clamp(top, 0.0, baseH);
                bottom = Math.Clamp(bottom, 0.0, baseH);
            }

            double finalWidth = right - left;
            double finalHeight = bottom - top;

            if (finalWidth <= 0 || finalHeight <= 0)
            {
                return null;
            }

            int x = Math.Max(0, (int)Math.Round(left));
            int y = Math.Max(0, (int)Math.Round(top));
            int w = Math.Max(0, (int)Math.Round(finalWidth));
            int h = Math.Max(0, (int)Math.Round(finalHeight));

            if (roi.BaseImgW is double baseWInt && baseWInt > 0)
            {
                int maxX = Math.Max(0, (int)Math.Round(baseWInt));
                if (x > maxX)
                {
                    return null;
                }

                if (x + w > maxX)
                {
                    w = Math.Max(0, maxX - x);
                }
            }

            if (roi.BaseImgH is double baseHInt && baseHInt > 0)
            {
                int maxY = Math.Max(0, (int)Math.Round(baseHInt));
                if (y > maxY)
                {
                    return null;
                }

                if (y + h > maxY)
                {
                    h = Math.Max(0, maxY - y);
                }
            }

            if (w <= 0 || h <= 0)
            {
                return null;
            }

            return new Int32Rect(x, y, w, h);
        }

        private void InspectionRoiPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InspectionRoiConfig.AnchorMaster) && sender is InspectionRoiConfig anchorChanged)
            {
                LogAlign(FormattableString.Invariant(
                    $"[UI] roi_index={anchorChanged.Index} anchor_master_changed_to={(int)anchorChanged.AnchorMaster}"));
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.Enabled))
            {
                UpdateSelectedRoiState();
                InvokeOnUi(RedrawOverlays);
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.CalibratedThreshold) && sender is InspectionRoiConfig changedCal && changedCal.CalibratedThreshold == null)
            {
                _lastCalibResultsByRoi.Remove(changedCal);
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.LastResultOk) || e.PropertyName == nameof(InspectionRoiConfig.Enabled))
            {
                UpdateGlobalBadge();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.DatasetPath) && sender is InspectionRoiConfig roiChanged)
            {
                _roiDatasetCache.Remove(roiChanged);
                roiChanged.DatasetStatus = "Syncing dataset...";
                _log($"[dataset:path] changed roi={roiChanged.ModelKey} path='{roiChanged.DatasetPath}'");
                _ = RefreshDatasetPreviewsForRoiAsync(roiChanged);
                _ = RefreshRoiDatasetStateAsync(roiChanged);

                CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
                OpenDatasetFolderCommand.RaiseCanExecuteChanged();
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.ModelKey) && sender is InspectionRoiConfig roiWithNewKey)
            {
                _ = RefreshBackendStateForRoiAsync(roiWithNewKey);
            }

            if (e.PropertyName == nameof(InspectionRoiConfig.DatasetOkCount)
                || e.PropertyName == nameof(InspectionRoiConfig.DatasetKoCount)
                || e.PropertyName == nameof(InspectionRoiConfig.IsDatasetLoading))
            {
                TrainSelectedRoiCommand.RaiseCanExecuteChanged();
                CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            }

            if (ReferenceEquals(sender, SelectedInspectionRoi))
            {
                if (e.PropertyName == nameof(InspectionRoiConfig.SelectedOkPreviewItem)
                    || e.PropertyName == nameof(InspectionRoiConfig.SelectedNgPreviewItem))
                {
                    RemoveSelectedPreviewCommand.RaiseCanExecuteChanged();
                }

                OnPropertyChanged(nameof(SelectedInspectionRoi));
                OnPropertyChanged(nameof(ActiveInspectionRoiModel));
                OnPropertyChanged(nameof(ActiveInspectionRoiImageRectPx));
                if (e.PropertyName == nameof(InspectionRoiConfig.Shape))
                {
                    OnPropertyChanged(nameof(SelectedInspectionShape));
                }
            }
        }

        public DatasetSample? SelectedOkSample
        {
            get => _selectedOkSample;
            set
            {
                if (!Equals(value, _selectedOkSample))
                {
                    _selectedOkSample = value;
                    OnPropertyChanged();
                    RemoveSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }
        private DatasetSample? _selectedOkSample;

        public DatasetSample? SelectedNgSample
        {
            get => _selectedNgSample;
            set
            {
                if (!Equals(value, _selectedNgSample))
                {
                    _selectedNgSample = value;
                    OnPropertyChanged();
                    RemoveSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }
        private DatasetSample? _selectedNgSample;

        public AsyncCommand AddOkFromCurrentRoiCommand { get; }
        public AsyncCommand AddNgFromCurrentRoiCommand { get; }
        public AsyncCommand RemoveSelectedCommand { get; }
        public AsyncCommand RemoveSelectedPreviewCommand { get; }
        public AsyncCommand DeleteSelectedCommand => RemoveSelectedCommand;
        public AsyncCommand OpenDatasetFolderCommand { get; }
        public AsyncCommand TrainFitCommand { get; }
        public AsyncCommand CalibrateCommand { get; }
        public AsyncCommand InferFromCurrentRoiCommand { get; }
        public AsyncCommand RefreshDatasetCommand { get; }
        public AsyncCommand RefreshHealthCommand { get; }
        public AsyncCommand BrowseDatasetCommand { get; }
        public AsyncCommand TrainSelectedRoiCommand { get; }
        public AsyncCommand CalibrateSelectedRoiCommand { get; }
        public AsyncCommand EvaluateSelectedRoiCommand { get; }
        public AsyncCommand EvaluateAllRoisCommand { get; }
        public AsyncCommand InferEnabledRoisCommand { get; }
        public AsyncCommand AddRoiToDatasetOkCommand { get; }
        public AsyncCommand AddRoiToDatasetNgCommand { get; }
        public AsyncCommand BrowseBatchFolderCommand { get; }
        public AsyncCommand StartBatchCommand { get; }
        public AsyncCommand PauseBatchCommand { get; }
        public AsyncCommand StopBatchCommand { get; }
        public AsyncCommand CreateMaster1RoiCommand { get; }
        public AsyncCommand ToggleEditMaster1RoiCommand { get; }
        public AsyncCommand RemoveMaster1RoiCommand { get; }

        public AsyncCommand CreateMaster2RoiCommand { get; }
        public AsyncCommand ToggleEditMaster2RoiCommand { get; }
        public AsyncCommand RemoveMaster2RoiCommand { get; }
        public ICommand AddRoiToOkCommand { get; }
        public ICommand AddRoiToNgCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    RaiseBusyChanged();
                }
            }
        }

        public string RoleId
        {
            get => _roleId;
            set
            {
                if (!string.Equals(_roleId, value, StringComparison.Ordinal))
                {
                    _roleId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RoiId
        {
            get => _roiId;
            set
            {
                if (!string.Equals(_roiId, value, StringComparison.Ordinal))
                {
                    _roiId = value;
                    OnPropertyChanged();
                }
            }
        }

        public double MmPerPx
        {
            get => _mmPerPx;
            set
            {
                if (Math.Abs(_mmPerPx - value) > 1e-6)
                {
                    _mmPerPx = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BackendBaseUrl
        {
            get => _backendBaseUrl;
            set
            {
                if (!string.Equals(_backendBaseUrl, value, StringComparison.Ordinal))
                {
                    _backendBaseUrl = value;
                    OnPropertyChanged();
                    _client.BaseUrl = value;
                }
            }
        }

        public string FitSummary
        {
            get => _fitSummary;
            private set
            {
                if (!string.Equals(_fitSummary, value, StringComparison.Ordinal))
                {
                    _fitSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CalibrationSummary
        {
            get => _calibrationSummary;
            private set
            {
                if (!string.Equals(_calibrationSummary, value, StringComparison.Ordinal))
                {
                    _calibrationSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public double? InferenceScore
        {
            get => _inferenceScore;
            private set
            {
                if (_inferenceScore != value)
                {
                    _inferenceScore = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                }
            }
        }

        public double? InferenceThreshold
        {
            get => _inferenceThreshold;
            private set
            {
                if (_inferenceThreshold != value)
                {
                    _inferenceThreshold = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                }
            }
        }

        public double LocalThreshold
        {
            get => _localThreshold;
            set
            {
                if (Math.Abs(_localThreshold - value) > 1e-6)
                {
                    _localThreshold = value;
                    OnPropertyChanged();
                    UpdateInferenceSummary();
                    if (_lastExport != null && _lastHeatmapBytes != null)
                    {
                        _ = _showHeatmapAsync(_lastExport, _lastHeatmapBytes, HeatmapOpacity);
                    }
                }
            }
        }

        public string InferenceSummary
        {
            get => _inferenceSummary;
            private set
            {
                if (!string.Equals(_inferenceSummary, value, StringComparison.Ordinal))
                {
                    _inferenceSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<Region> Regions { get; } = new();

        public double HeatmapOpacity
        {
            get => _heatmapOpacity;
            set
            {
                if (Math.Abs(_heatmapOpacity - value) > 1e-3)
                {
                    _heatmapOpacity = value;
                    OnPropertyChanged();
                    if (_lastExport != null && _lastHeatmapBytes != null)
                    {
                        _ = _showHeatmapAsync(_lastExport, _lastHeatmapBytes, HeatmapOpacity);
                    }
                }
            }
        }

        public string HealthSummary
        {
            get => _healthSummary;
            private set
            {
                if (!string.Equals(_healthSummary, value, StringComparison.Ordinal))
                {
                    _healthSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool? IsBackendOnline
        {
            get => _isBackendOnline;
            private set => SetProperty(ref _isBackendOnline, value);
        }

        private AsyncCommand CreateCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            return new AsyncCommand(async param =>
            {
                await RunExclusiveAsync(() => execute(param));
            }, canExecute ?? (_ => !IsBusy));
        }

        private bool CanCalibrateSelectedRoi()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return false;
            }

            if (roi.IsDatasetLoading)
            {
                return false;
            }

            return roi.DatasetOkCount >= 10 && roi.DatasetKoCount >= 1;
        }

        private async Task RunExclusiveAsync(Func<Task> action)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void RaiseBusyChanged()
        {
            AddOkFromCurrentRoiCommand.RaiseCanExecuteChanged();
            AddNgFromCurrentRoiCommand.RaiseCanExecuteChanged();
            RemoveSelectedCommand.RaiseCanExecuteChanged();
            RemoveSelectedPreviewCommand.RaiseCanExecuteChanged();
            OpenDatasetFolderCommand.RaiseCanExecuteChanged();
            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
            InferFromCurrentRoiCommand.RaiseCanExecuteChanged();
            RefreshDatasetCommand.RaiseCanExecuteChanged();
            RefreshHealthCommand.RaiseCanExecuteChanged();
            BrowseDatasetCommand.RaiseCanExecuteChanged();
            TrainSelectedRoiCommand.RaiseCanExecuteChanged();
            CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            CreateMaster1RoiCommand.RaiseCanExecuteChanged();
            ToggleEditMaster1RoiCommand.RaiseCanExecuteChanged();
            RemoveMaster1RoiCommand.RaiseCanExecuteChanged();

            CreateMaster2RoiCommand.RaiseCanExecuteChanged();
            ToggleEditMaster2RoiCommand.RaiseCanExecuteChanged();
            RemoveMaster2RoiCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
            if (AddRoiToOkCommand is AsyncCommand asyncAddOk)
            {
                asyncAddOk.RaiseCanExecuteChanged();
            }
            if (AddRoiToNgCommand is AsyncCommand asyncAddNg)
            {
                asyncAddNg.RaiseCanExecuteChanged();
            }
            BrowseBatchFolderCommand?.RaiseCanExecuteChanged();
            UpdateBatchCommandStates();
        }

        public void SetMasterEditState(bool isMaster1Editing, bool isMaster2Editing)
        {
            IsMaster1Editing = isMaster1Editing;
            IsMaster2Editing = isMaster2Editing;
        }

        private bool CanEditMasterRoi()
        {
            var handlerAllows = _canEditMasterRoi?.Invoke();
            if (handlerAllows.HasValue)
            {
                return !IsBusy && handlerAllows.Value;
            }

            return !IsBusy && _layout != null;
        }

        private async Task CreateMasterRoiAsync(MasterRoleOption? roleOption, RoiShape shape)
        {
            if (roleOption == null)
            {
                _log("[ui] CreateMasterRoiAsync ignored: no role selected");
                return;
            }

            if (_createMasterRoiAsync == null)
            {
                _log("[ui] CreateMasterRoiAsync ignored: handler missing");
                return;
            }

            await _createMasterRoiAsync(roleOption.Role, shape).ConfigureAwait(false);
        }

        private async Task ToggleEditSaveMasterRoiAsync(MasterRoleOption? roleOption)
        {
            if (roleOption == null)
            {
                _log("[ui] ToggleEditSaveMasterRoiAsync ignored: no role selected");
                return;
            }

            bool isEditing;
            var isMaster1 = roleOption.Role is RoiRole.Master1Pattern or RoiRole.Master1Search;
            if (_toggleEditSaveMasterRoiAsync == null)
            {
                isEditing = isMaster1 ? !IsMaster1Editing : !IsMaster2Editing;
            }
            else
            {
                isEditing = await _toggleEditSaveMasterRoiAsync(roleOption.Role).ConfigureAwait(false);
            }

            ApplyMasterEditState(roleOption.Role, isEditing);
        }

        private async Task RemoveMasterRoiAsync(MasterRoleOption? roleOption)
        {
            if (roleOption == null)
            {
                _log("[ui] RemoveMasterRoiAsync ignored: no role selected");
                return;
            }

            if (_removeMasterRoiAsync == null)
            {
                _log("[ui] RemoveMasterRoiAsync ignored: handler missing");
                return;
            }

            await _removeMasterRoiAsync(roleOption.Role).ConfigureAwait(false);
            ApplyMasterEditState(roleOption.Role, isEditing: false);
        }

        private void ApplyMasterEditState(RoiRole role, bool isEditing)
        {
            switch (role)
            {
                case RoiRole.Master1Pattern:
                case RoiRole.Master1Search:
                    IsMaster1Editing = isEditing;
                    break;
                case RoiRole.Master2Pattern:
                case RoiRole.Master2Search:
                    IsMaster2Editing = isEditing;
                    break;
            }
        }

        private void UpdateSelectedRoiState()
        {
            BrowseDatasetCommand.RaiseCanExecuteChanged();
            TrainSelectedRoiCommand.RaiseCanExecuteChanged();
            CalibrateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateSelectedRoiCommand.RaiseCanExecuteChanged();
            EvaluateAllRoisCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetOkCommand.RaiseCanExecuteChanged();
            AddRoiToDatasetNgCommand.RaiseCanExecuteChanged();
            if (AddRoiToOkCommand is AsyncCommand asyncAddOk)
            {
                asyncAddOk.RaiseCanExecuteChanged();
            }
            if (AddRoiToNgCommand is AsyncCommand asyncAddNg)
            {
                asyncAddNg.RaiseCanExecuteChanged();
            }
        }

        private async Task AddSampleAsync(bool isNg)
        {
            EnsureRoleRoi();
            _log($"[dataset] add {(isNg ? "NG" : "OK")} sample requested");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[dataset] export cancelled");
                return;
            }

            var fileName = $"sample_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
            var label = isNg ? "ng" : "ok";
            var meta = new SampleMetadata
            {
                role_id = RoleId,
                roi_id = RoiId,
                label = label,
                filename = fileName,
                mm_per_px = MmPerPx,
                shape_json = export.ShapeJson,
                created_at_utc = DateTime.UtcNow.ToString("o"),
                source_path = _getSourceImagePath()
            };

            await _client.UploadDatasetSampleAsync(RoleId, RoiId, isNg, export.PngBytes, fileName, meta).ConfigureAwait(false);

            _log($"[dataset] uploaded {(isNg ? "NG" : "OK")} sample -> {fileName}");
            await RefreshDatasetAsync().ConfigureAwait(false);
        }

        private static string SanitizePathComponent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "default";
            }

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
        }

        private string ResolveDatasetCachePath(string roleId, string roiId, string label, string filename)
        {
            var recipe = string.IsNullOrWhiteSpace(_client.RecipeId) ? "default" : _client.RecipeId!.Trim();
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrakeDiscInspector",
                "cache",
                "datasets",
                SanitizePathComponent(recipe));
            var dir = Path.Combine(root, $"{SanitizePathComponent(roleId)}_{SanitizePathComponent(roiId)}", label);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, Path.GetFileName(filename));
        }

        private async Task<DatasetSample?> CacheDatasetSampleAsync(
            string roleId,
            string roiId,
            bool isNg,
            string filename,
            bool hasMeta,
            CancellationToken ct)
        {
            try
            {
                var label = isNg ? "ng" : "ok";
                var cachePath = ResolveDatasetCachePath(roleId, roiId, label, filename);
                if (!File.Exists(cachePath))
                {
                    var bytes = await _client.DownloadDatasetFileAsync(roleId, roiId, isNg, filename, ct).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                }

                var metaPath = Path.ChangeExtension(cachePath, ".json");
                SampleMetadata meta = new SampleMetadata
                {
                    role_id = roleId,
                    roi_id = roiId,
                    label = label,
                    filename = filename,
                    mm_per_px = MmPerPx,
                    created_at_utc = DateTime.UtcNow.ToString("o")
                };

                if (hasMeta)
                {
                    try
                    {
                        meta = await _client.GetDatasetMetaAsync(roleId, roiId, isNg, filename, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log($"[dataset] meta download failed for '{filename}': {ex.Message}");
                    }
                }

                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                };
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(meta, options);
                await File.WriteAllBytesAsync(metaPath, jsonBytes, ct).ConfigureAwait(false);

                return new DatasetSample(cachePath, metaPath, isNg, meta);
            }
            catch (Exception ex)
            {
                _log($"[dataset] cache failed for '{filename}': {ex.Message}");
                return null;
            }
        }


        private async Task AddRoiToDatasetAsync(InspectionRoiConfig roi, bool isOk)
        {
            if (roi == null)
            {
                await ShowMessageAsync("No ROI selected.", "Dataset");
                return;
            }

            _activateInspectionIndex?.Invoke(roi.Index);
            GuiLog.Info($"AddToDataset (config) roi='{roi.DisplayName}' label={(isOk ? "OK" : "NG")} source='{_getSourceImagePath()}'");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                await ShowMessageAsync("No image loaded.", "Dataset");
                GuiLog.Warn($"AddToDataset aborted: no export for roi='{roi.DisplayName}'");
                return;
            }

            try
            {
                var fileName = $"sample_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
                var label = isOk ? "ok" : "ng";
                var meta = new SampleMetadata
                {
                    role_id = RoleId,
                    roi_id = roi.ModelKey,
                    label = label,
                    filename = fileName,
                    mm_per_px = MmPerPx,
                    shape_json = export.ShapeJson,
                    created_at_utc = DateTime.UtcNow.ToString("o"),
                    source_path = _getSourceImagePath()
                };

                await _client.UploadDatasetSampleAsync(RoleId, roi.ModelKey, !isOk, export.PngBytes, fileName, meta).ConfigureAwait(false);
                GuiLog.Info($"AddToDataset uploaded roi='{roi.DisplayName}' -> '{fileName}'");

                await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
                await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);

                await ShowDatasetSavedAsync(roi.DisplayName, fileName, isOk).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error($"AddToDataset upload failed roi='{roi.DisplayName}'", ex);
                await ShowMessageAsync(
                    $"Could not upload dataset image. Revisa la conexión y los logs.",
                    "Dataset",
                    MessageBoxImage.Error);
            }
        }

        private async Task AddRoiToDatasetAsync(RoiModel roi, bool positive)
        {
            if (roi == null)
            {
                await ShowMessageAsync("ROI inválido.", "Dataset");
                return;
            }

            ActivateInspectionIndexForRoi(roi);

            var config = FindInspectionConfigForRoi(roi);
            if (config != null)
            {
                await AddRoiToDatasetAsync(config, positive).ConfigureAwait(false);
                return;
            }

            GuiLog.Info($"AddToDataset (direct) roi='{roi.Label ?? roi.Id}' target={(positive ? "OK" : "NG")} source='{_getSourceImagePath()}'");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                await ShowMessageAsync("No image loaded.", "Dataset");
                GuiLog.Warn($"AddToDataset aborted: no export for legacy roi"); // CODEX: FormattableString compatibility.
                return;
            }

            string role = roi.Role.ToString();
            string roiId = string.IsNullOrWhiteSpace(roi.Id) ? roi.Role.ToString() : roi.Id;

            try
            {
                var fileName = $"sample_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
                var label = positive ? "ok" : "ng";
                var meta = new SampleMetadata
                {
                    role_id = role,
                    roi_id = roiId,
                    label = label,
                    filename = fileName,
                    mm_per_px = MmPerPx,
                    shape_json = export.ShapeJson,
                    created_at_utc = DateTime.UtcNow.ToString("o"),
                    source_path = _getSourceImagePath()
                };

                await _client.UploadDatasetSampleAsync(role, roiId, !positive, export.PngBytes, fileName, meta).ConfigureAwait(false);

                await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
                GuiLog.Info($"AddToDataset (direct) uploaded {(positive ? "OK" : "NG")} -> '{fileName}'");
                await ShowDatasetSavedAsync(roi.Label ?? roi.Role.ToString(), fileName, positive).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error($"AddToDataset legacy failed roi='{roi.Label ?? roi.Id}'", ex);
                await ShowMessageAsync(
                    "No se pudo subir el ROI al dataset. Revisa los logs para más detalles.",
                    "Dataset",
                    MessageBoxImage.Error);
            }
        }

        private void ActivateInspectionIndexForRoi(RoiModel roi)
        {
            var index = TryResolveInspectionIndex(roi);
            if (index.HasValue)
            {
                _activateInspectionIndex?.Invoke(index.Value);
            }
        }

        private static int? TryResolveInspectionIndex(RoiModel roi)
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
                var labelParts = roi.Label.Split(' ');
                if (labelParts.Length > 1 && int.TryParse(labelParts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLabel))
                {
                    return parsedLabel;
                }
            }

            return null;
        }

        private async Task RefreshRoiDatasetStateAsync(RoiModel roi)
        {
            if (roi == null)
            {
                return;
            }

            try
            {
                var config = FindInspectionConfigForRoi(roi);
                if (config != null)
                {
                    await RefreshRoiDatasetStateAsync(config).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log($"[dataset] refresh state error for ROI '{roi.Label ?? roi.Id}': {ex.Message}");
            }
        }

        private InspectionRoiConfig? FindInspectionConfigForRoi(RoiModel roi)
        {
            if (roi == null || _inspectionRois == null || _inspectionRois.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                var label = roi.Label.Trim();
                var byLabel = _inspectionRois.FirstOrDefault(r =>
                    string.Equals(r.Name, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.DisplayName, label, StringComparison.OrdinalIgnoreCase));
                if (byLabel != null)
                {
                    return byLabel;
                }
            }

            var roiId = roi.Id?.Trim();
            if (!string.IsNullOrWhiteSpace(roiId))
            {
                var byKey = _inspectionRois.FirstOrDefault(r => string.Equals(r.ModelKey, roiId, StringComparison.OrdinalIgnoreCase));
                if (byKey != null)
                {
                    return byKey;
                }

                var byId = _inspectionRois.FirstOrDefault(r => string.Equals(r.Id, roiId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    return byId;
                }
            }

            if (!string.IsNullOrWhiteSpace(roiId)
                && int.TryParse(roiId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            {
                var byIndex = _inspectionRois.FirstOrDefault(r => r.Index == numericId);
                if (byIndex != null)
                {
                    return byIndex;
                }
            }

            return SelectedInspectionRoi ?? _inspectionRois.FirstOrDefault();
        }

        private async Task RemoveSelectedAsync()
        {
            var toRemove = new List<DatasetSample>();
            if (SelectedOkSample != null)
            {
                toRemove.Add(SelectedOkSample);
            }
            if (SelectedNgSample != null)
            {
                toRemove.Add(SelectedNgSample);
            }

            if (toRemove.Count == 0)
            {
                return;
            }

            foreach (var sample in toRemove)
            {
                try
                {
                    await _client.DeleteDatasetFileAsync(RoleId, RoiId, sample.IsNg, sample.Metadata.filename).ConfigureAwait(false);
                    _log($"[dataset] removed sample {sample.Metadata.filename}");
                }
                catch (Exception ex)
                {
                    _log($"[dataset] failed to delete remote sample {sample.Metadata.filename}: {ex.Message}");
                }
                _datasetManager.DeleteSample(sample);
            }

            await RefreshDatasetAsync().ConfigureAwait(false);
        }

        private bool CanRemoveSelectedPreview()
        {
            var roi = SelectedInspectionRoi;
            return !IsBusy
                   && roi != null
                   && (roi.SelectedOkPreviewItem != null || roi.SelectedNgPreviewItem != null);
        }

        private async Task RemoveSelectedPreviewAsync()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return;
            }

            var selectedOk = roi.SelectedOkPreviewItem;
            var selectedNg = roi.SelectedNgPreviewItem;
            if (selectedOk == null && selectedNg == null)
            {
                return;
            }

            if (selectedOk != null)
            {
                var filename = Path.GetFileName(selectedOk.Path);
                if (string.IsNullOrWhiteSpace(filename))
                {
                    ShowSnackbar("No se pudo determinar el archivo OK seleccionado.");
                    return;
                }

                try
                {
                    await _client.DeleteDatasetFileAsync(RoleId, roi.ModelKey, isNg: false, filename).ConfigureAwait(false);
                    _log($"[dataset] removed preview OK sample {filename}");
                    InvokeOnUi(() => roi.SelectedOkPreviewItem = null);
                }
                catch (Exception ex)
                {
                    _log($"[dataset] failed to delete preview OK sample {filename}: {ex.Message}");
                    ShowSnackbar($"Error al borrar OK: {ex.Message}");
                    return;
                }
            }

            if (selectedNg != null)
            {
                var filename = Path.GetFileName(selectedNg.Path);
                if (string.IsNullOrWhiteSpace(filename))
                {
                    ShowSnackbar("No se pudo determinar el archivo NG seleccionado.");
                    return;
                }

                try
                {
                    await _client.DeleteDatasetFileAsync(RoleId, roi.ModelKey, isNg: true, filename).ConfigureAwait(false);
                    _log($"[dataset] removed preview NG sample {filename}");
                    InvokeOnUi(() => roi.SelectedNgPreviewItem = null);
                }
                catch (Exception ex)
                {
                    _log($"[dataset] failed to delete preview NG sample {filename}: {ex.Message}");
                    ShowSnackbar($"Error al borrar NG: {ex.Message}");
                    return;
                }
            }

            await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);
            await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
        }

        private async Task OpenDatasetFolderAsync()
        {
            var datasetPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrakeDiscInspector",
                "cache",
                "datasets");

            try
            {
                Directory.CreateDirectory(datasetPath);
            }
            catch (Exception ex)
            {
                _log($"[dataset] failed to ensure directory '{datasetPath}': {ex.Message}");
            }

            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = datasetPath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    _log($"[dataset] failed to open folder '{datasetPath}': {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        private async Task RefreshDatasetAsync()
        {
            EnsureRoleRoi();
            _log("[dataset] refreshing lists");
            try
            {
                var list = await _client.GetDatasetListAsync(RoleId, RoiId).ConfigureAwait(false);
                var okSamples = new List<DatasetSample>();
                foreach (var file in list.ok.files)
                {
                    var sample = await CacheDatasetSampleAsync(RoleId, RoiId, isNg: false, file, list.ok.meta.ContainsKey(file) && list.ok.meta[file], CancellationToken.None)
                        .ConfigureAwait(false);
                    if (sample != null)
                    {
                        okSamples.Add(sample);
                    }
                }

                var ngSamples = new List<DatasetSample>();
                foreach (var file in list.ng.files)
                {
                    var sample = await CacheDatasetSampleAsync(RoleId, RoiId, isNg: true, file, list.ng.meta.ContainsKey(file) && list.ng.meta[file], CancellationToken.None)
                        .ConfigureAwait(false);
                    if (sample != null)
                    {
                        ngSamples.Add(sample);
                    }
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OkSamples.Clear();
                    foreach (var sample in okSamples)
                    {
                        OkSamples.Add(sample);
                    }

                    NgSamples.Clear();
                    foreach (var sample in ngSamples)
                    {
                        NgSamples.Add(sample);
                    }
                });
            }
            catch (Exception ex)
            {
                _log($"[dataset] refresh failed: {ex.Message}");
            }

            TrainFitCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
        }

        private async Task TrainAsync()
        {
            EnsureRoleRoi();
            if (OkSamples.Count < 10)
            {
                await ShowMessageAsync("Se requieren al menos 10 OK para entrenar.", "Train");
                return;
            }
            _log("[fit] training from backend dataset");
            _showBusyDialog?.Invoke("Training...");
            _updateBusyProgress?.Invoke(null);
            try
            {
                var result = await _client.FitOkFromDatasetAsync(RoleId, RoiId, MmPerPx).ConfigureAwait(false);
                FitSummary = $"Embeddings={result.n_embeddings} Coreset={result.coreset_size} TokenShape=[{string.Join(',', result.token_shape ?? Array.Empty<int>())}]";
                _log("[fit] completed " + FitSummary);
            }
            catch (HttpRequestException ex)
            {
                if (IsAlreadyTrainedError(ex))
                {
                    FitSummary = "Model already trained";
                    _log($"[fit] backend reported already trained: {ex.Message}");
                    return;
                }

                FitSummary = "Train failed";
                await ShowMessageAsync($"Training failed: {ex.Message}", caption: "Train error");
            }
            finally
            {
                _updateBusyProgress?.Invoke(null);
                _hideBusyDialog?.Invoke();
            }
        }

        private async Task CalibrateAsync()
        {
            EnsureRoleRoi();
            if (OkSamples.Count < 10)
            {
                await ShowMessageAsync("Se requieren al menos 10 OK para calibrar.", "Calibrate");
                return;
            }
            if (NgSamples.Count < 1)
            {
                await ShowMessageAsync("No NG samples available for calibration.", "Calibrate");
                return;
            }
            _showBusyDialog?.Invoke("Calibrating...");
            _updateBusyProgress?.Invoke(null);

            try
            {
                _log("[calibrate] backend dataset calibration");
                var calib = await _client.CalibrateDatasetAsync(RoleId, RoiId, MmPerPx).ConfigureAwait(false);
                CalibrationSummary = $"Threshold={calib.threshold:0.###} OK={calib.n_ok} NG={calib.n_ng} Percentile={calib.score_percentile}";
                _log("[calibrate] " + CalibrationSummary);
                LocalThreshold = calib.threshold;
                _updateBusyProgress?.Invoke(100);
            }
            finally
            {
                _updateBusyProgress?.Invoke(null);
                _hideBusyDialog?.Invoke();
            }
        }

        private async Task InferCurrentAsync()
        {
            EnsureRoleRoi();
            _log("[infer] exporting current ROI");
            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[infer] export cancelled");
                return;
            }

            var inferFileName = $"roi_{DateTime.UtcNow:yyyyMMddHHmmssfff}.png";
            _log($"[infer] POST role={RoleId} roi={RoiId} bytes={export.PngBytes.Length} imgHash={export.ImageHash} cropHash={export.CropHash} rect=({export.CropRect.X},{export.CropRect.Y},{export.CropRect.Width},{export.CropRect.Height})");

            var result = await _client.InferAsync(RoleId, RoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson).ConfigureAwait(false);
            _lastExport = export;
            _lastInferResult = result;
            InferenceScore = result.score;
            InferenceThreshold = result.threshold;

            // ANTES:
            // LocalThreshold = result.threshold > 0 ? result.threshold : LocalThreshold;

            // AHORA (elige una de las dos):
            // Opción 1:
            if (result.threshold.HasValue && result.threshold.Value > 0)
            {
                LocalThreshold = result.threshold.Value;
            }
            // Opción 2 (C# 9+):
            // LocalThreshold = result.threshold is > 0 var t ? t : LocalThreshold;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Regions.Clear();
                if (result.regions != null)
                {
                    foreach (var region in result.regions)
                    {
                        Regions.Add(region);
                    }
                }
            });


            if (!string.IsNullOrWhiteSpace(result.heatmap_png_base64))
            {
                _lastHeatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                await _showHeatmapAsync(export, _lastHeatmapBytes, HeatmapOpacity).ConfigureAwait(false);
            }
            else
            {
                _lastHeatmapBytes = null;
                _clearHeatmap();
            }
        }

        private async Task RefreshHealthAsync()
        {
            if (!await _healthRefreshGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                var info = await _client.GetHealthAsync().ConfigureAwait(false);
                IsBackendOnline = info != null;
                if (info == null)
                {
                    HealthSummary = "Backend offline";
                    return;
                }

                HealthSummary = $"{info.status ?? "ok"} — {info.device} — {info.model} ({info.version})";
            }
            catch
            {
                IsBackendOnline = false;
                HealthSummary = "Backend offline";
            }
            finally
            {
                _healthRefreshGate.Release();
            }
        }

        private async Task BrowseDatasetAsync()
        {
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return;
            }
            await ShowMessageAsync(
                "El dataset se gestiona en el backend. Usa agregar muestras y refrescar para sincronizar.",
                "Dataset",
                MessageBoxImage.Information);
        }



        private BitmapSource? ExtractRoiBitmapWithShapeMask(RoiExportResult export)
        {
            return LoadBitmapFromBytes(export.PngBytes);
        }

        private static BitmapSource? LoadBitmapFromBytes(byte[] data)

        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static string RoiFolderName(string displayName)
            => displayName.Replace(" ", "_");

        private static string MapRoiIdToFolder(string roiId)
        {
            var m = Regex.Match(roiId ?? string.Empty, @"^inspection-(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return $"Inspection_{m.Groups[1].Value}";
            }

            return string.IsNullOrWhiteSpace(roiId) ? "UnknownRoi" : roiId;
        }

        private static string EncodeBackendComponent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "default";
            }

            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                               .TrimEnd('=')
                               .Replace('+', '-')
                               .Replace('/', '_');

            return string.IsNullOrWhiteSpace(encoded) ? "default" : encoded;
        }

        private static string SanitizeComponent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "default";
            }

            var sb = new StringBuilder(value.Length);
            foreach (var c in value.Trim())
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            return sb.Length == 0 ? "default" : sb.ToString();
        }

        // Python ModelStore encodes role/roi with URL-safe Base64; include legacy/sanitized fallbacks.
        private static IEnumerable<string> GetBackendModelBaseNames(string? roleId, string? roiId)
        {
            var role = roleId ?? string.Empty;
            var roi = roiId ?? string.Empty;

            yield return $"{EncodeBackendComponent(role)}__{EncodeBackendComponent(roi)}";
            yield return $"{SanitizeComponent(role)}_{SanitizeComponent(roi)}";
            yield return $"{role}__{roi}";
        }

        private static string DetermineNegLabelDir(string datasetRoot)
        {
            var ng = Path.Combine(datasetRoot, "ng");
            var ko = Path.Combine(datasetRoot, "ko");
            if (Directory.Exists(ng)) return "ng";
            if (Directory.Exists(ko)) return "ko";
            return "ng";
        }

        private static IEnumerable<string> EnumerateDatasetFiles(string rootDir, string labelDir, string roiDisplayName, int take = 24)
        {
            var normalizedRoot = DatasetPathHelper.NormalizeDatasetPath(rootDir);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
            {
                yield break;
            }

            rootDir = normalizedRoot;
            var roiFolder = RoiFolderName(roiDisplayName);
            var subDir = Path.Combine(rootDir, labelDir, roiFolder);
            string targetDir;
            if (Directory.Exists(subDir))
            {
                targetDir = subDir;
            }
            else
            {
                targetDir = Path.Combine(rootDir, labelDir);
            }

            if (!Directory.Exists(targetDir))
            {
                Debug.WriteLine($"[thumbs] no dir: {targetDir}");
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(targetDir, "*.*", SearchOption.AllDirectories)
                                         .Where(f => BatchImageExtensions.Contains(Path.GetExtension(f)))
                                         .OrderByDescending(File.GetCreationTimeUtc)
                                         .Take(take))
            {
                yield return file;
            }
        }

        private static BitmapSource? LoadBitmapSource(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame == null)
            {
                return null;
            }

            frame.Freeze();
            return frame;
        }

        public async Task RefreshDatasetPreviewsForRoiAsync(InspectionRoiConfig roi, int take = 24)
        {
            if (roi == null)
            {
                return;
            }

            var okItems = new ObservableCollection<DatasetPreviewItem>();
            var ngItems = new ObservableCollection<DatasetPreviewItem>();
            try
            {
                var list = await _client.GetDatasetListAsync(RoleId, roi.ModelKey).ConfigureAwait(false);
                var okFiles = list.ok.files;
                var ngFiles = list.ng.files;

                foreach (var file in okFiles.Skip(Math.Max(0, okFiles.Count - take)))
                {
                    var cachePath = ResolveDatasetCachePath(RoleId, roi.ModelKey, "ok", file);
                    if (!File.Exists(cachePath))
                    {
                        var bytes = await _client.DownloadDatasetFileAsync(RoleId, roi.ModelKey, isNg: false, file, CancellationToken.None).ConfigureAwait(false);
                        await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                    }
                    var bmp = LoadBitmapSource(cachePath);
                    if (bmp != null)
                    {
                        okItems.Add(new DatasetPreviewItem { Path = cachePath, Thumbnail = bmp });
                    }
                }

                foreach (var file in ngFiles.Skip(Math.Max(0, ngFiles.Count - take)))
                {
                    var cachePath = ResolveDatasetCachePath(RoleId, roi.ModelKey, "ng", file);
                    if (!File.Exists(cachePath))
                    {
                        var bytes = await _client.DownloadDatasetFileAsync(RoleId, roi.ModelKey, isNg: true, file, CancellationToken.None).ConfigureAwait(false);
                        await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                    }
                    var bmp = LoadBitmapSource(cachePath);
                    if (bmp != null)
                    {
                        ngItems.Add(new DatasetPreviewItem { Path = cachePath, Thumbnail = bmp });
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[dataset] preview refresh failed: {ex.Message}");
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                roi.OkPreview = okItems;
                roi.NgPreview = ngItems;
                Debug.WriteLine($"[thumbs] {roi.DisplayName} ok={okItems.Count} ng={ngItems.Count}");
            });
        }

        private static void SavePng(string path, BitmapSource bmp)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }

        private static async Task ShowDatasetSavedAsync(string? roiName, string path, bool isOk)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var caption = isOk ? "Add to OK" : "Add to NG";
                var prefix = string.IsNullOrWhiteSpace(roiName) ? string.Empty : $"{roiName}: ";
                var label = isOk ? "OK" : "NG";
                var message = prefix + "guardado en " + label + ":" + Environment.NewLine + path;
                MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private async Task<RoiDatasetAnalysis> EnsureDatasetAnalysisAsync(InspectionRoiConfig roi, bool forceRefresh = false)
        {
            if (!forceRefresh && _roiDatasetCache.TryGetValue(roi, out var cached))
            {
                if (string.Equals(cached.DatasetPath, roi.DatasetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return cached;
                }
            }

            return await RefreshRoiDatasetStateAsync(roi).ConfigureAwait(false);
        }

        private async Task<RoiDatasetAnalysis> RefreshRoiDatasetStateAsync(InspectionRoiConfig roi)
        {
            roi.IsDatasetLoading = true;
            roi.DatasetReady = false;
            roi.DatasetStatus = "Syncing dataset...";

            RoiDatasetAnalysis analysis;
            try
            {
                var list = await _client.GetDatasetListAsync(RoleId, roi.ModelKey).ConfigureAwait(false);
                var okCount = list.ok.count;
                var ngCount = list.ng.count;
                var ready = okCount > 0;
                var status = ready ? "Dataset Ready ✅" : "Dataset missing OK samples";
                analysis = new RoiDatasetAnalysis(string.Empty, new List<DatasetEntry>(), okCount, ngCount, ready, status, new List<(string, bool)>());

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    roi.IsDatasetLoading = false;
                    roi.DatasetOkCount = okCount;
                    roi.DatasetKoCount = ngCount;
                    roi.DatasetReady = ready;
                    roi.DatasetStatus = status;
                });
            }
            catch (Exception ex)
            {
                analysis = RoiDatasetAnalysis.Empty($"Dataset error: {ex.Message}");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    roi.IsDatasetLoading = false;
                    roi.DatasetOkCount = 0;
                    roi.DatasetKoCount = 0;
                    roi.DatasetReady = false;
                    roi.DatasetStatus = analysis.StatusMessage;
                });
            }

            await RefreshDatasetPreviewsForRoiAsync(roi).ConfigureAwait(false);
            _roiDatasetCache[roi] = analysis;

            return analysis;
        }

        private static RoiDatasetAnalysis AnalyzeDatasetPath(string? datasetPath)
        {
            datasetPath = DatasetPathHelper.NormalizeDatasetPath(datasetPath);
            if (string.IsNullOrWhiteSpace(datasetPath))
            {
                return RoiDatasetAnalysis.Empty("Select a dataset");
            }

            if (Directory.Exists(datasetPath))
            {
                return AnalyzeFolderDataset(datasetPath);
            }

            if (File.Exists(datasetPath))
            {
                if (string.Equals(Path.GetExtension(datasetPath), ".csv", StringComparison.OrdinalIgnoreCase))
                {
                    return AnalyzeCsvDataset(datasetPath);
                }

                return RoiDatasetAnalysis.Empty("Dataset file must be a CSV with filename,label columns");
            }

            return RoiDatasetAnalysis.Empty("Dataset path not found");
        }

        private static string[] GetImageFiles(string folder)
        {
            if (!Directory.Exists(folder))
                return Array.Empty<string>();

            var patterns = new[] { "*.bmp", "*.BMP", "*.png", "*.PNG", "*.jpg", "*.JPG", "*.jpeg", "*.JPEG" };
            var files = patterns.SelectMany(p => Directory.GetFiles(folder, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            GuiLog.Info($"[dataset:scan] folder='{folder}' files={files.Length}");
            foreach (var f in files)
                GuiLog.Info($"[dataset:file] '{f}'");

            return files;
        }

        private static RoiDatasetAnalysis AnalyzeFolderDataset(string folderPath)
        {
            try
            {
                var okDir = Path.Combine(folderPath, "ok");
                if (!Directory.Exists(okDir))
                {
                    GuiLog.Info($"[dataset] folder '{folderPath}' missing OK dir at '{okDir}'");
                    return RoiDatasetAnalysis.Empty("Folder must contain /ok and /ko subfolders");
                }

                var negLabel = DetermineNegLabelDir(folderPath);
                var negDir = Path.Combine(folderPath, negLabel);
                if (!Directory.Exists(negDir))
                {
                    GuiLog.Info($"[dataset] folder '{folderPath}' missing NG dir at '{negDir}'");
                    return RoiDatasetAnalysis.Empty("Folder must contain /ok and /ko subfolders");
                }

                var okFiles = GetImageFiles(okDir);
                var koFiles = GetImageFiles(negDir);

                GuiLog.Info($"[dataset] folder '{folderPath}' contains {okFiles.Length} OK images and {koFiles.Length} {negLabel.ToUpper()} images");

                var entries = okFiles.Select(path => new DatasetEntry(path, true))
                    .Concat(koFiles.Select(path => new DatasetEntry(path, false)))
                    .ToList();

                var preview = new List<(string Path, bool IsOk)>();
                preview.AddRange(okFiles.Take(3).Select(p => (p, true)));
                preview.AddRange(koFiles.Take(3).Select(p => (p, false)));

                bool ready = okFiles.Length > 0 && koFiles.Length > 0;
                string status = ready ? (negLabel == "ng" ? "Dataset Ready ✅ (using /ng)" : "Dataset Ready ✅")
                    : (okFiles.Length == 0 ? "Dataset missing OK samples" : "Dataset missing KO samples");

                return new RoiDatasetAnalysis(folderPath, entries, okFiles.Length, koFiles.Length, ready, status, preview);
            }
            catch (Exception ex)
            {
                return RoiDatasetAnalysis.Empty($"Dataset error: {ex.Message}");
            }
        }

        private static RoiDatasetAnalysis AnalyzeCsvDataset(string csvPath)
        {
            try
            {
                var entries = new List<DatasetEntry>();
                var preview = new List<(string Path, bool IsOk)>();
                int okCount = 0, koCount = 0;
                int okPreview = 0, koPreview = 0;

                using var reader = new StreamReader(csvPath);
                string? header = reader.ReadLine();
                if (header == null)
                {
                    return RoiDatasetAnalysis.Empty("CSV is empty");
                }

                var headers = header.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None)
                    .Select(h => h.Trim().ToLowerInvariant())
                    .ToArray();

                int filenameIndex = Array.FindIndex(headers, h => h is "filename" or "file" or "path");
                int labelIndex = Array.FindIndex(headers, h => h is "label" or "class" or "target");

                if (filenameIndex < 0 || labelIndex < 0)
                {
                    return RoiDatasetAnalysis.Empty("CSV must contain 'filename' and 'label' columns");
                }

                string? line;
                var baseDir = Path.GetDirectoryName(csvPath) ?? string.Empty;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parts = line.Split(new[] { ',', ';', '\t' }, StringSplitOptions.None);
                    if (parts.Length <= Math.Max(filenameIndex, labelIndex))
                    {
                        continue;
                    }

                    var rawPath = parts[filenameIndex].Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        continue;
                    }

                    var resolvedPath = Path.IsPathRooted(rawPath)
                        ? rawPath
                        : Path.GetFullPath(Path.Combine(baseDir, rawPath));

                    if (!File.Exists(resolvedPath))
                    {
                        continue;
                    }

                    var labelRaw = parts[labelIndex].Trim().Trim('"');
                    var parsedLabel = TryParseDatasetLabel(labelRaw);
                    if (parsedLabel == null)
                    {
                        continue;
                    }

                    bool isOk = parsedLabel.Value;
                    entries.Add(new DatasetEntry(resolvedPath, isOk));
                    if (isOk)
                    {
                        okCount++;
                        if (okPreview < 3)
                        {
                            preview.Add((resolvedPath, true));
                            okPreview++;
                        }
                    }
                    else
                    {
                        koCount++;
                        if (koPreview < 3)
                        {
                            preview.Add((resolvedPath, false));
                            koPreview++;
                        }
                    }
                }

                bool ready = okCount > 0 && koCount > 0;
                string status = ready ? "Dataset Ready ✅" : "CSV needs both OK and KO samples";
                return new RoiDatasetAnalysis(csvPath, entries, okCount, koCount, ready, status, preview);
            }
            catch (Exception ex)
            {
                return RoiDatasetAnalysis.Empty($"CSV error: {ex.Message}");
            }
        }

        private static bool? TryParseDatasetLabel(string labelRaw)
        {
            if (string.IsNullOrWhiteSpace(labelRaw))
            {
                return null;
            }

            var normalized = labelRaw.Trim().Trim('"').ToLowerInvariant();
            return normalized switch
            {
                "ok" or "good" or "pass" or "1" or "true" => true,
                "ko" or "ng" or "nok" or "fail" or "0" or "false" => false,
                _ => null
            };
        }

        private async Task<bool> TrainSelectedRoiAsync(CancellationToken ct = default)
        {
            EnsureRoleRoi();
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                return false;
            }
            if (roi.DatasetOkCount < 10)
            {
                await ShowMessageAsync("Se requieren al menos 10 OK para entrenar.", "Train");
                return false;
            }
            GuiLog.Info($"[train] START roi='{roi.DisplayName}' (backend dataset)");

            _showBusyDialog?.Invoke("Training...");
            _updateBusyProgress?.Invoke(null);

            try
            {
                try
                {
                    var result = await _client.FitOkFromDatasetAsync(RoleId, roi.ModelKey, MmPerPx, roi.TrainMemoryFit, ct).ConfigureAwait(false);
                    var memoryHint = roi.TrainMemoryFit ? " (memory-fit)" : string.Empty;
                    FitSummary = $"Embeddings={result.n_embeddings} Coreset={result.coreset_size} TokenShape=[{string.Join(',', result.token_shape ?? Array.Empty<int>())}]" + memoryHint;
                    roi.BackendMemoryFitted = true;
                    _lastFitResultsByRoi[roi] = result;
                    await SaveModelManifestAsync(roi, result, null, ct).ConfigureAwait(false);
                    CopyModelArtifactsToRecipe(roi);
                }
                catch (HttpRequestException ex)
                {
                    if (IsAlreadyTrainedError(ex))
                    {
                        FitSummary = "Model already trained";
                        _log($"[train] backend reported already trained: {ex.Message}");
                        roi.BackendMemoryFitted = true;
                        return true;
                    }

                    FitSummary = "Train failed";
                    await ShowMessageAsync($"Training failed: {ex.Message}", caption: "Train error");
                    return false;
                }

                return true;
            }
            finally
            {
                _hideBusyDialog?.Invoke();
            }
        }

        private static bool IsAlreadyTrainedError(HttpRequestException ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return false;
            }

            var message = ex.Message;
            return message.Contains("already trained", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("ya entrenad", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("memoria existente", StringComparison.OrdinalIgnoreCase);
        }

        private async Task CalibrateSelectedRoiAsync(CancellationToken ct = default)
        {
            EnsureRoleRoi();
            var roi = SelectedInspectionRoi;
            if (roi == null)
            {
                await ShowMessageAsync("Select an Inspection ROI first.", "Calibrate");
                return;
            }
            if (roi.DatasetOkCount < 10)
            {
                await ShowMessageAsync("Se requieren al menos 10 OK para calibrar.", "Calibrate");
                return;
            }
            if (roi.DatasetKoCount < 1)
            {
                await ShowMessageAsync("No NG samples available for calibration.", "Calibrate");
                return;
            }

            if (!await EnsureTrainedBeforeCalibrateAsync(roi, ct).ConfigureAwait(false))
            {
                return;
            }

            _showBusyDialog?.Invoke("Calibrating...");
            _updateBusyProgress?.Invoke(null);

            try
            {
                var calib = await _client.CalibrateDatasetAsync(RoleId, roi.ModelKey, MmPerPx, ct).ConfigureAwait(false);
                CalibrationSummary = $"Threshold={calib.threshold:0.###} OK={calib.n_ok} NG={calib.n_ng} Percentile={calib.score_percentile:0.###}";
                roi.CalibratedThreshold = calib.threshold;
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                UpdateGlobalBadge();

                var calibResult = new CalibResult
                {
                    threshold = calib.threshold,
                    ok_mean = 0.0,
                    ng_mean = 0.0,
                    score_percentile = calib.score_percentile,
                    area_mm2_thr = calib.area_mm2_thr
                };
                _lastCalibResultsByRoi[roi] = calibResult;

                await SaveModelManifestAsync(roi, GetLastFitResult(roi), calibResult, ct).ConfigureAwait(false);
                CopyModelArtifactsToRecipe(roi);
            }
            finally
            {
                _hideBusyDialog?.Invoke();
            }
        }

        public void ResetModelStates()
        {
            _lastFitResultsByRoi.Clear();
            _lastCalibResultsByRoi.Clear();
            _backendStateAvailableByRoi.Clear();
            FitSummary = string.Empty;
            CalibrationSummary = string.Empty;
            InferenceSummary = string.Empty;
            _lastExport = null;
            _lastInferResult = null;
            _lastHeatmapBytes = null;
            ClearInferenceUi("[model] reset via canvas clear");

            if (_inspectionRois != null)
            {
                foreach (var roi in _inspectionRois)
                {
                    roi.BackendMemoryFitted = false;
                    roi.BackendCalibPresent = false;
                    roi.CalibratedThreshold = null;
                    roi.LastScore = null;
                    roi.LastResultOk = null;
                    roi.LastEvaluatedAt = null;
                }
            }

            UpdateGlobalBadge();
        }

        public async Task<bool> LoadModelManifestAsync(InspectionRoiConfig roi, string filePath)
        {
            if (roi == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                await ShowMessageAsync("Model file not found.", "Load model", MessageBoxImage.Warning);
                return false;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var manifest = JsonSerializer.Deserialize<InspectionModelManifest>(json, ManifestJsonOptions);
                if (manifest == null)
                {
                    throw new InvalidOperationException("Empty model manifest.");
                }

                if (manifest.roi_index > 0 && manifest.roi_index != roi.Index)
                {
                    await ShowMessageAsync(
                        $"El modelo seleccionado corresponde a Inspection {manifest.roi_index}.",
                        "Load model",
                        MessageBoxImage.Warning);
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(manifest.model_key)
                        && !string.Equals(roi.ModelKey, manifest.model_key, StringComparison.OrdinalIgnoreCase))
                    {
                        roi.ModelKey = manifest.model_key;
                    }

                    roi.CalibratedThreshold = manifest.calibration?.threshold;
                    roi.LastScore = null;
                    roi.LastResultOk = null;
                    roi.LastEvaluatedAt = null;
                });

                ClearInferenceUi("[model] load manifest");

                var manifestFit = manifest.fit;
                var needsFit = manifestFit == null;

                _lastFitResultsByRoi.Remove(roi);
                FitSummary = string.Empty;

                if (needsFit)
                {
                    if (await EnsureFitEndpointAsync().ConfigureAwait(false))
                    {
                        var okCount = OkSamples?.Count ?? 0;
                        if (okCount >= 10)
                        {
                            try
                            {
                                _log($"[fit] auto-fit tras LoadModel → {okCount} OK samples (dataset), roi={roi.ModelKey}");
                                var fit = await _client.FitOkFromDatasetAsync(RoleId, roi.ModelKey, MmPerPx, roi.TrainMemoryFit)
                                                       .ConfigureAwait(false);

                                _lastFitResultsByRoi[roi] = fit;
                                FitSummary = $"Embeddings={fit.n_embeddings} Coreset={fit.coreset_size} TokenShape=[{string.Join(',', fit.token_shape ?? Array.Empty<int>())}]";
                                await Application.Current.Dispatcher.InvokeAsync(() => roi.BackendMemoryFitted = true);
                            }
                            catch (Exception ex)
                            {
                                _log($"[fit] auto-fit tras LoadModel falló: {ex.Message}");
                            }
                        }
                        else
                        {
                            _log("[fit] auto-fit omitido: no hay suficientes OK samples para este ROI.");
                        }
                    }
                    else
                    {
                        _log("[fit] auto-fit omitido: backend sin endpoint /fit_ok disponible.");
                    }
                }
                else
                {
                    _lastFitResultsByRoi[roi] = manifestFit;
                    FitSummary = $"Embeddings={manifestFit.n_embeddings} Coreset={manifestFit.coreset_size} TokenShape=[{string.Join(',', manifestFit.token_shape ?? Array.Empty<int>())}]";
                }

                if (manifest.calibration != null)
                {
                    _lastCalibResultsByRoi[roi] = manifest.calibration;
                    CalibrationSummary = $"Threshold={(manifest.calibration.threshold ?? double.NaN):0.###} OKµ={manifest.calibration.ok_mean:0.###} NGµ={manifest.calibration.ng_mean:0.###} Percentile={manifest.calibration.score_percentile:0.###}";

                    if (manifest.calibration.threshold is double thr && thr > 0)
                    {
                        LocalThreshold = thr;
                    }
                }
                else
                {
                    _lastCalibResultsByRoi.Remove(roi);
                    CalibrationSummary = string.Empty;
                }

                SelectedInspectionRoi = roi;
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                UpdateGlobalBadge();

                _log($"[model] manifest cargado '{filePath}' para ROI '{roi.Name}' (idx={roi.Index})");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[model] load manifest failed: {ex.Message}");
                await ShowMessageAsync(
                    $"No se pudo cargar el modelo.\n{ex.Message}",
                    "Load model",
                    MessageBoxImage.Error);
                return false;
            }
        }

        private FitOkResult? GetLastFitResult(InspectionRoiConfig roi)
            => roi != null && _lastFitResultsByRoi.TryGetValue(roi, out var value) ? value : null;

        private string? GetModelDirectoryForRoi(InspectionRoiConfig roi)
        {
            if (roi == null)
            {
                return null;
            }

            var datasetPath = DatasetPathHelper.NormalizeDatasetPath(roi.DatasetPath);
            if (string.IsNullOrWhiteSpace(datasetPath))
            {
                return null;
            }

            var modelDir = Path.Combine(datasetPath, "Model");
            try
            {
                Directory.CreateDirectory(modelDir);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[model] failed to ensure model dir '{modelDir}': {ex.Message}");
            }

            return modelDir;
        }

        private async Task SaveModelManifestAsync(InspectionRoiConfig roi, FitOkResult? fit, CalibResult? calib, CancellationToken ct)
        {
            if (roi == null)
            {
                return;
            }

            if (fit == null && calib == null)
            {
                return;
            }

            if (_resolveModelDirectory == null)
            {
                return;
            }

            string? targetDirectory;
            try
            {
                targetDirectory = _resolveModelDirectory(roi);
            }
            catch (Exception ex)
            {
                _log($"[model] error resolving directory: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(targetDirectory);
                ObsoleteFileHelper.MoveExistingFilesToObsolete(targetDirectory, "model_*.json");

                var manifest = new InspectionModelManifest
                {
                    manifest_version = "1.0",
                    role_id = RoleId,
                    model_key = roi.ModelKey,
                    roi_index = roi.Index,
                    roi_name = roi.Name,
                    mm_per_px = MmPerPx,
                    trained_at_utc = fit != null ? DateTimeOffset.UtcNow : null,
                    calibrated_at_utc = calib != null ? DateTimeOffset.UtcNow : null,
                    fit = fit ?? GetLastFitResult(roi),
                    calibration = calib ?? (_lastCalibResultsByRoi.TryGetValue(roi, out var lastCalib) ? lastCalib : null)
                };

                if (manifest.fit == null && manifest.calibration == null)
                {
                    return;
                }

                string fileName = $"model_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                string manifestPath = Path.Combine(targetDirectory, fileName);
                string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
                await File.WriteAllTextAsync(manifestPath, json, ct).ConfigureAwait(false);

                try
                {
                    var latestPath = Path.Combine(targetDirectory, "latest_model.json");
                    File.Copy(manifestPath, latestPath, overwrite: true);
                }
                catch (Exception copyEx)
                {
                    _log($"[model] no se pudo actualizar latest_model.json: {copyEx.Message}");
                }

                _log($"[model] manifest guardado en '{manifestPath}'");
            }
            catch (Exception ex)
            {
                _log($"[model] error al guardar manifest: {ex.Message}");
            }
        }

        // Backend ModelStore (backend/app.py + storage.py) persists under BDI_MODELS_DIR / BRAKEDISC_MODELS_DIR
        // defaulting to a "models" folder relative to the backend working directory.
        private static string ResolveBackendModelsRoot()
        {
            var env = Environment.GetEnvironmentVariable("BDI_MODELS_DIR")
                      ?? Environment.GetEnvironmentVariable("BRAKEDISC_MODELS_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                try
                {
                    return Path.GetFullPath(env);
                }
                catch
                {
                    return env;
                }
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        }

        private void CopyModelArtifactsToRecipe(InspectionRoiConfig roi)
        {
            if (roi == null)
            {
                return;
            }

            var modelDir = _resolveModelDirectory?.Invoke(roi) ?? GetModelDirectoryForRoi(roi);
            if (string.IsNullOrWhiteSpace(modelDir))
            {
                _log?.Invoke($"[model] cannot resolve model directory for ROI '{roi.DisplayName}'");
                return;
            }

            var modelsRoot = ResolveBackendModelsRoot();
            foreach (var baseName in GetBackendModelBaseNames(RoleId, roi.ModelKey))
            {
                var copiedMemory = TryCopyModel(
                    Path.Combine(modelsRoot, baseName + ".npz"),
                    Path.Combine(modelDir, baseName + ".npz"));

                var copiedCalib = TryCopyModel(
                    Path.Combine(modelsRoot, baseName + "_calib.json"),
                    Path.Combine(modelDir, baseName + "_calib.json"));

                if (copiedMemory || copiedCalib)
                {
                    // Prefer the first naming scheme that produces artifacts on disk.
                    break;
                }
            }
        }

        private static bool TryCopyModel(string src, string dst)
        {
            try
            {
                if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                GuiLog.Warn($"[model-copy] failed '{src}' → '{dst}': {ex.Message}");
            }

            return false;
        }

        private async Task<bool> EnsureTrainedBeforeCalibrateAsync(InspectionRoiConfig? roi, CancellationToken ct)
        {
            if (roi == null)
            {
                await ShowMessageAsync("No ROI selected.", "Calibrate");
                return false;
            }

            try
            {
                return await TrainSelectedRoiAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync($"Training failed before calibration.\n{ex.Message}", "Calibrate");
                return false;
            }
        }

        private async Task<double?> InferScoreForCalibrationAsync(InspectionRoiConfig roi, string samplePath, CancellationToken ct)
        {
            bool retried = false;
            while (true)
            {
                try
                {
                    var infer = await _client.InferAsync(RoleId, roi.ModelKey, MmPerPx, samplePath, null, ct).ConfigureAwait(false);
                    return infer.score;
                }
                catch (HttpRequestException ex) when (!retried && ex.Message.Contains("Memoria no encontrada", StringComparison.OrdinalIgnoreCase))
                {
                    retried = true;
                    var retrained = await TrainSelectedRoiAsync(ct).ConfigureAwait(false);
                    if (!retrained)
                    {
                        await ShowMessageAsync("Training failed while retrying calibration inference.", "Calibrate");
                        return null;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
                {
                    await ShowMessageAsync($"Inference failed for '{Path.GetFileName(samplePath)}'.\n{ex.Message}", "Calibrate");
                    return null;
                }
            }
        }

        private async Task EvaluateSelectedRoiAsync()
        {
            var roiCfg = SelectedInspectionRoi;
            if (roiCfg == null)
            {
                return;
            }

            await ActivateCanvasIndexAsync(roiCfg.Index).ConfigureAwait(false);

            await EvaluateRoiAsync(roiCfg, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InferEnabledRoisAsync()
        {
            if (_inspectionRois == null)
            {
                return;
            }

            foreach (var roi in _inspectionRois.Where(r => r.Enabled))
            {
                await EvaluateRoiAsync(roi, CancellationToken.None).ConfigureAwait(false);
            }

            UpdateGlobalBadge();
        }

        private async Task EvaluateAllRoisAsync()
        {
            await InferEnabledRoisAsync().ConfigureAwait(false);
        }

        private async Task EvaluateRoiAsync(InspectionRoiConfig? roi, CancellationToken ct)
        {
            EnsureRoleRoi();
            if (roi == null || !roi.Enabled)
            {
                return;
            }

            if (!ValidateInferenceInputs(roi, out var validationError))
            {
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    await ShowMessageAsync(validationError, caption: "Cannot evaluate");
                }
                return;
            }

            await ActivateCanvasIndexAsync(roi.Index).ConfigureAwait(false);
            _log($"[eval] start -> name='{roi.Name}' idx={roi.Index} modelKey='{roi.ModelKey}'");

            var export = await _exportRoiAsync().ConfigureAwait(false);
            if (export == null)
            {
                _log("[eval] export cancelled");
                return;
            }

            try
            {
                var exRoi = export.RoiImage;
                var crop = export.CropRect;
                _log($"[eval] export -> slotIdx={roi.Index} roiId='{exRoi?.Id}' shape={exRoi?.Shape} " +
                     $"crop=({crop.X},{crop.Y},{crop.Width},{crop.Height})");
            }
            catch
            {
                // logging best-effort
            }

            if (export.PngBytes == null || export.PngBytes.Length == 0)
            {
                _log("[eval] export produced empty payload");
                await ShowMessageAsync("ROI crop is empty. Save the ROI layout and try again.", caption: "Inference aborted");
                return;
            }

            var inferFileName = $"roi_{DateTime.UtcNow:yyyyMMddHHmmssfff}.png";
            var resolvedRoiId = NormalizeInspectionKey(
                string.Equals(RoleId, "Inspection", StringComparison.OrdinalIgnoreCase) ? RoiId : roi.ModelKey,
                roi.Index);

            if (!string.IsNullOrWhiteSpace(roi.ModelKey) &&
                !string.Equals(roi.ModelKey, resolvedRoiId, StringComparison.Ordinal))
            {
                roi.ModelKey = resolvedRoiId;
            }

            if (!string.Equals(RoiId, resolvedRoiId, StringComparison.Ordinal))
            {
                RoiId = resolvedRoiId;
            }

            bool attemptedAutoFit = false;

            async Task ApplyInferResultAsync(InferResult result)
            {
                _lastExport = export;
                _lastInferResult = result;
                InferenceScore = result.score;
                InferenceThreshold = result.threshold;

                if (result.threshold is double thresholdValue && thresholdValue > 0)
                {
                    LocalThreshold = thresholdValue;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Regions.Clear();
                    if (result.regions != null)
                    {
                        foreach (var region in result.regions)
                        {
                            Regions.Add(region);
                        }
                    }
                });

                if (!string.IsNullOrWhiteSpace(result.heatmap_png_base64))
                {
                    _lastHeatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                    await _showHeatmapAsync(export, _lastHeatmapBytes, HeatmapOpacity).ConfigureAwait(false);
                }
                else
                {
                    _lastHeatmapBytes = null;
                    _clearHeatmap();
                }

                roi.LastScore = result.score;
                var decisionThreshold = roi.CalibratedThreshold ?? roi.ThresholdDefault;
                bool isNg = result.score > decisionThreshold;
                roi.LastResultOk = !isNg;
                roi.LastEvaluatedAt = DateTime.UtcNow;
                _log($"[eval] done idx={roi.Index} key='{roi.ModelKey}' score={result.score:0.###} thr={decisionThreshold:0.###} => {(isNg ? "NG" : "OK")}");
                OnPropertyChanged(nameof(SelectedInspectionRoi));
                UpdateGlobalBadge();
            }

            async Task ResetAfterFailureAsync(string message, string caption, string? extraLog = null)
            {
                _lastExport = export;
                _lastInferResult = null;
                _lastHeatmapBytes = null;
                InferenceScore = null;
                InferenceThreshold = null;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Regions.Clear();
                });
                _clearHeatmap();
                roi.LastScore = null;
                roi.LastResultOk = null;
                roi.LastEvaluatedAt = DateTime.UtcNow;
                InferenceSummary = $"Inference failed: {message}";
                if (!string.IsNullOrWhiteSpace(extraLog))
                {
                    _log(extraLog);
                }
                _log($"[eval] FAILED idx={roi.Index} key='{roi.ModelKey}' -> {message}");
                await ShowMessageAsync(message, caption);
                UpdateGlobalBadge();
            }

            try
            {
                await RefreshBackendStateForRoiAsync(roi, ct).ConfigureAwait(false);
                var stateAvailable = _backendStateAvailableByRoi.TryGetValue(roi, out var available) && available;
                if (stateAvailable && !roi.BackendMemoryFitted)
                {
                    if (OkSamples.Count < 10)
                    {
                        await ShowMessageAsync("Faltan OK samples (mínimo 10) para entrenar desde dataset.", "Evaluate ROI").ConfigureAwait(false);
                        return;
                    }

                    attemptedAutoFit = true;
                    _log($"[eval] auto-fit: state not fitted, ok_samples={OkSamples.Count} -> running fit_ok from dataset");
                    try
                    {
                        await _client.FitOkFromDatasetAsync(RoleId, resolvedRoiId, MmPerPx, memoryFit: false, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await ResetAfterFailureAsync("Error en fit_ok. Revisa los OK samples.", "Evaluate ROI", $"[fit_ok] error: {ex.Message}").ConfigureAwait(false);
                        return;
                    }
                }

                var result = await _client.InferAsync(RoleId, resolvedRoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson, ct).ConfigureAwait(false);
                await ApplyInferResultAsync(result).ConfigureAwait(false);
            }
            catch (BackendMemoryNotFittedException)
            {
                if (attemptedAutoFit)
                {
                    _log("[eval] auto-fit: already attempted, still not fitted");
                    await ResetAfterFailureAsync("Error: el backend sigue sin modelo entrenado despues del auto-entrenamiento. Revisa logs y dataset.", "Evaluate ROI").ConfigureAwait(false);
                    return;
                }

                try
                {
                    if (OkSamples.Count < 10)
                    {
                        await ResetAfterFailureAsync("Faltan OK samples (mínimo 10) para entrenar desde dataset.", "Evaluate ROI").ConfigureAwait(false);
                        return;
                    }

                    await _client.FitOkFromDatasetAsync(RoleId, resolvedRoiId, MmPerPx, memoryFit: false, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await ResetAfterFailureAsync("Error en fit_ok. Revisa los OK samples.", "Evaluate ROI", $"[fit_ok] error: {ex.Message}").ConfigureAwait(false);
                    return;
                }

                try
                {
                    var retryResult = await _client.InferAsync(RoleId, resolvedRoiId, MmPerPx, export.PngBytes, inferFileName, export.ShapeJson, ct).ConfigureAwait(false);
                    await ApplyInferResultAsync(retryResult).ConfigureAwait(false);
                }
                catch (BackendBadRequestException brex)
                {
                    var detail = string.IsNullOrWhiteSpace(brex.Detail) ? brex.Message : brex.Detail!;
                    await ResetAfterFailureAsync($"❌ Backend 400: {detail}", "Evaluate ROI", $"[/infer 400] {detail}").ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    var message = ExtractBackendError(ex);
                    await ResetAfterFailureAsync(message, "Inference error").ConfigureAwait(false);
                }

                return;
            }
            catch (BackendBadRequestException brex)
            {
                var detail = string.IsNullOrWhiteSpace(brex.Detail) ? brex.Message : brex.Detail!;
                await ResetAfterFailureAsync($"❌ Backend 400: {detail}", "Evaluate ROI", $"[/infer 400] {detail}").ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var message = ExtractBackendError(ex);
                await ResetAfterFailureAsync(message, "Inference error").ConfigureAwait(false);
            }
        }

        private async Task ActivateCanvasIndexAsync(int index)
        {
            try
            {
                _log($"[ui] ActivateCanvasIndexAsync({index})");
                _activateInspectionIndex?.Invoke(index);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task.ConfigureAwait(false);
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[ui] ActivateCanvasIndexAsync error: {ex.Message}");
            }
        }

        private void ClearInferenceUi(string reason)
        {
            try
            {
                _log($"{reason} -> clearing inference/heatmap");
            }
            catch
            {
                // ignore logging failures
            }

            _lastExport = null;
            _lastInferResult = null;
            _lastHeatmapBytes = null;
            InferenceScore = null;
            InferenceThreshold = null;
            InferenceSummary = string.Empty;

            try
            {
                _clearHeatmap();
            }
            catch
            {
                // ignore
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Regions.Clear();
            }
            else if (dispatcher.CheckAccess())
            {
                Regions.Clear();
            }
            else
            {
                dispatcher.InvokeAsync(() => Regions.Clear());
            }
        }

        public void OnInspectionTabChanged(int index)
        {
            try
            {
                var roiCfg = _inspectionRois?.FirstOrDefault(r => r.Index == index);
                if (roiCfg != null)
                {
                    _log($"[ui] OnInspectionTabChanged -> {roiCfg.DisplayName} idx={index}");
                    SelectedInspectionRoi = roiCfg;
                }
            }
            catch (Exception ex)
            {
                _log($"[ui] OnInspectionTabChanged error: {ex.Message}");
            }
        }

        private async Task RefreshBackendStateForRoiAsync(InspectionRoiConfig roi, CancellationToken ct = default)
        {
            if (roi == null)
            {
                return;
            }

            var roleId = string.IsNullOrWhiteSpace(RoleId) ? "Inspection" : RoleId;
            var roiId = string.Equals(roleId, "Inspection", StringComparison.OrdinalIgnoreCase)
                ? NormalizeInspectionKey(roi.ModelKey, roi.Index)
                : (!string.IsNullOrWhiteSpace(RoiId) ? RoiId : roi.ModelKey);

            if (string.IsNullOrWhiteSpace(roiId))
            {
                roiId = roi.ModelKey;
            }

            BackendStateInfo? state = null;
            try
            {
                state = await _client.GetStateAsync(roleId, roiId, modelKey: roi.ModelKey, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[state] error role='{roleId}' roi='{roiId}': {ex.Message}");
            }

            _backendStateAvailableByRoi[roi] = state != null;
            if (state == null)
            {
                _log($"[state] unavailable role='{roleId}' roi='{roiId}'");
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                roi.BackendMemoryFitted = state?.memory_fitted ?? false;
                roi.BackendCalibPresent = state?.calib_present ?? false;
            }
            else
            {
                await dispatcher.InvokeAsync(() =>
                {
                    roi.BackendMemoryFitted = state?.memory_fitted ?? false;
                    roi.BackendCalibPresent = state?.calib_present ?? false;
                });
            }
        }

        private bool ValidateInferenceInputs(InspectionRoiConfig roi, out string? message)
        {
            if (_inspectionRois == null || !_inspectionRois.Contains(roi))
            {
                message = "Save the ROI layout before evaluating.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(roi.ModelKey))
            {
                message = "ModelKey is required for inference.";
                return false;
            }

            if (string.Equals(RoleId, "Inspection", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(RoiId) ||
                    !Regex.IsMatch(RoiId, @"^inspection-[1-4]$", RegexOptions.IgnoreCase))
                {
                    message = "Solo son válidos ROI de inspección: inspection-1..inspection-4.";
                    return false;
                }
            }

            var sourceImage = _getSourceImagePath();
            if (string.IsNullOrWhiteSpace(sourceImage) || !File.Exists(sourceImage))
            {
                message = "Load an inspection image before evaluating.";
                return false;
            }

            message = null;
            return true;
        }

        private static string ExtractBackendError(HttpRequestException ex)
        {
            var message = ex.Message;
            int colonIndex = message.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < message.Length - 1)
            {
                var tail = message[(colonIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    return tail;
                }
            }

            return message;
        }

        private void StopBatch()
        {
            try
            {
                _batchCts?.Cancel();
            }
            catch
            {
            }

            _pauseGate.Set();
            _isBatchRunning = false;
            SetBatchStatusSafe("Stopped");
            UpdateBatchCommandStates();
            BatchRowOk = null;
            CurrentRowIndex = 0;
            CurrentImagePath = null;
            ClearBatchBaselineRois();
        }

        private async Task ApplyBatchPauseAsync(CancellationToken ct)
        {
            var seconds = _batchPausePerRoiSeconds;
            if (seconds <= 0.0)
            {
                return;
            }

            var totalMs = (int)Math.Round(seconds * 1000.0);
            if (totalMs <= 0)
            {
                return;
            }

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < totalMs)
            {
                ct.ThrowIfCancellationRequested();

                _pauseGate?.Wait(ct);

                var remaining = totalMs - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }

                var step = Math.Min(remaining, 50);
                await Task.Delay(step, ct).ConfigureAwait(false);
            }
        }

        private async Task<BatchImageContext?> LoadBatchImageContextAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[batch] read failed: {ex.Message}");
                return null;
            }

            Mat src = null!;
            Mat gray = null!;
            bool success = false;
            try
            {
                src = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
                if (src.Empty())
                {
                    _log($"[batch] decode failed (src) for '{Path.GetFileName(path)}'");
                    return null;
                }

                gray = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
                if (gray.Empty())
                {
                    _log($"[batch] decode failed (gray) for '{Path.GetFileName(path)}'");
                    return null;
                }

                success = true;
                return new BatchImageContext(path, bytes, src, gray);
            }
            catch (Exception ex)
            {
                _log($"[batch] decode failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (!success)
                {
                    src?.Dispose();
                    gray?.Dispose();
                }
            }
        }

        private async Task RunBatchAsync(CancellationToken ct)
        {
            EnsureRoleRoi();

            InitializeBatchSession();
            InitializeBatchBaselineRois();

            var rows = GetBatchRowsSnapshot();
            int total = rows.Count;

            if (string.IsNullOrWhiteSpace(BatchFolder) || !Directory.Exists(BatchFolder) || total == 0)
            {
                UpdateBatchProgress(0, total);
                SetBatchStatusSafe(total == 0 ? "No images to process." : "Folder not found.");
                _isBatchRunning = false;
                _pauseGate.Set();
                _batchCts?.Dispose();
                _batchCts = null;
                IsBusy = false;
                UpdateBatchCommandStates();
                return;
            }

            var ensuredModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int processed = 0;
            int rowIndex = 0;

            UpdateBatchProgress(0, total);
            SetBatchStatusSafe("Running...");

            BatchStarted?.Invoke(this, EventArgs.Empty);

            try
            {
                _annotatedOutputDir = null;

                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    _pauseGate.Wait(ct);

                    // CODEX: reset anchors para la nueva imagen
                    _currentBatchCt = ct;

                    try
                    {
                        rowIndex++;
                        row.IndexOneBased = rowIndex;
                        InvokeOnUi(() => BeginBatchStep(rowIndex, row.FullPath));
                        processed = rowIndex;
                        SetBatchStatusSafe($"[{processed}/{total}] {row.FileName}");

                        if (!File.Exists(row.FullPath))
                        {
                            _log($"[batch] file missing: '{row.FullPath}'");
                            for (int roiIndex = 1; roiIndex <= 4; roiIndex++)
                            {
                                UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                            }
                            BatchRowOk = false;
                            UpdateBatchProgress(processed, total);
                            continue;
                        }

                        using var ctx = await LoadBatchImageContextAsync(row.FullPath, ct).ConfigureAwait(false);
                        if (ctx == null)
                        {
                            _log($"[batch] skip: unable to decode '{row.FileName}'");
                            for (int roiIndex = 1; roiIndex <= 4; roiIndex++)
                            {
                                UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                            }
                            BatchRowOk = false;
                            UpdateBatchProgress(processed, total);
                            continue;
                        }

                        SetBatchBaseImage(ctx.Bytes, row.FullPath);

                        await RepositionInspectionRoisForImageAsync(ctx.Gray, row.FullPath, ct).ConfigureAwait(false);

                        for (int roiIndex = 1; roiIndex <= 4; roiIndex++)
                        {
                            ct.ThrowIfCancellationRequested();
                            _pauseGate.Wait(ct);

                            var config = GetInspectionConfigByIndex(roiIndex);
                            if (config == null)
                            {
                                SetBatchCellStatus(row, roiIndex - 1, null);
                                await ApplyBatchPauseAsync(ct).ConfigureAwait(false);
                                continue;
                            }

                            _log($"[batch] processing file='{row.FileName}' roiIdx={config.Index} enabled={config.Enabled}");

                            if (!config.Enabled)
                            {
                                SetBatchCellStatus(row, config.Index - 1, null);
                                InvokeOnUi(ClearBatchHeatmap);
                                _clearHeatmap();
                                _log($"[batch] skip disabled roi idx={config.Index} '{config.DisplayName}'");
                                await ApplyBatchPauseAsync(ct).ConfigureAwait(false);
                                continue;
                            }

                            if (!_batchAnchorsOk && !_allowBatchInferWithoutAnchors)
                            {
                                var fileName = row.FileName;
                                var message = FormattableString.Invariant($"[batch] skip ROI={config.Index} for file='{fileName}' because master fit failed (fit_ok=false)");
                                _log(message);
                                SetBatchCellStatus(row, config.Index - 1, null);
                                await ApplyBatchPauseAsync(ct).ConfigureAwait(false);
                                continue;
                            }

                            if (config.Index >= 3 && !(_batchAnchorM1Ready && _batchAnchorM2Ready))
                            {
                                var fileName = row.FileName;
                                var message = FormattableString.Invariant($"[batch] skip ROI={config.Index} for file='{fileName}' because master fit failed (anchors not ready)");
                                TraceBatch("[batch] wait: anchors not ready -> skipping ROI 3/4");
                                _log?.Invoke(message);
                                SetBatchCellStatus(row, config.Index - 1, null);
                                await ApplyBatchPauseAsync(ct).ConfigureAwait(false);
                                continue;
                            }

                            try
                            {
                                var export = await ExportRoiFromMatAsync(config, ctx.Src, row.FullPath, ct).ConfigureAwait(false);

                                var resolvedRoiId = NormalizeInspectionKey(config.ModelKey, config.Index);
                                if (!string.Equals(config.ModelKey, resolvedRoiId, StringComparison.Ordinal))
                                {
                                    config.ModelKey = resolvedRoiId;
                                }

                                if (ensuredModels.Add(resolvedRoiId))
                                {
                                    try
                                    {
                                        await _client.EnsureFittedAsync(RoleId, resolvedRoiId, MmPerPx).ConfigureAwait(false);
                                    }
                                    catch (BackendMemoryNotFittedException ex)
                                    {
                                        _log($"[batch] ensure_fitted missing roi='{resolvedRoiId}': {ex.Message}");
                                        UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                                        continue;
                                    }
                                }

                                UpdateBatchHeatmapIndex(config.Index);

                                var result = await _client.InferAsync(RoleId, resolvedRoiId, MmPerPx, export.Bytes, export.FileName, export.ShapeJson).ConfigureAwait(false);

                                UpdateHeatmapFromResult(ToGlobalInferResult(result), config.Index);

                                InvokeOnUi(() =>
                                {
                                    var canvasRect = GetInspectionRoiCanvasRect(config.Index);
                                    TraceBatchHeatmapPlacement("per-roi", config.Index, canvasRect);
                                });

                                double decisionThreshold = result.threshold
                                    ?? config.CalibratedThreshold
                                    ?? config.ThresholdDefault;

                                if (decisionThreshold <= 0 && LocalThreshold > 0)
                                {
                                    decisionThreshold = LocalThreshold;
                                }

                                bool? roiOk = null;
                                if (decisionThreshold > 0)
                                {
                                    roiOk = result.score <= decisionThreshold;
                                }

                                SetBatchCellStatus(row, config.Index - 1, roiOk);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _log($"[batch] ROI{roiIndex} failed for '{row.FileName}': {ex.Message}");
                            UpdateBatchRowStatus(row, roiIndex, BatchCellStatus.Nok);
                        }

                        await ApplyBatchPauseAsync(ct).ConfigureAwait(false);
                    }

                    if (_batchPlacedForStep != _batchStepId)
                    {
                        if (_batchAnchorReadyForStep != _batchStepId)
                        {
                            GuiLog.Warn($"[batch-ui] row-end without anchors step={_batchStepId} file='{Path.GetFileName(CurrentImagePath ?? string.Empty)}'");
                            await CaptureCanvasIfNeededAsync(Path.GetFileName(CurrentImagePath ?? string.Empty), "rowEnd_noAnchors").ConfigureAwait(false);
                        }
                        else
                        {
                            await PlaceBatchFinalAsync("VM.BatchRowOk", ct).ConfigureAwait(false);
                            _batchPlacedForStep = (int)_batchStepId;
                        }
                    }

                    BatchRowOk = ComputeBatchRowOk(row);
                    GuiLog.Info($"[batch] row result file='{Path.GetFileName(CurrentImagePath ?? string.Empty)}' => {(BatchRowOk == true ? "OK" : "NG")}");

                    if (BatchRowOk.HasValue)
                    {
                        NotifyBatchCaption(Path.GetFileName(CurrentImagePath ?? string.Empty), BatchRowOk.Value);
                    }

                    await OnRowCompletedAsync(row, ct).ConfigureAwait(false);
                    UpdateBatchProgress(processed, total);
                }
                finally
                {
                    EndBatchStep();
                }
            }

            _log($"[batch] completed processed={processed}/{total}");
            SetBatchStatusSafe("Completed");
            }
            catch (OperationCanceledException)
            {
                if (!string.Equals(BatchStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
                {
                    SetBatchStatusSafe("Cancelled");
                }
            }
            catch (Exception ex)
            {
                _log($"[batch] unexpected error: {ex.Message}");
                SetBatchStatusSafe($"Error: {ex.Message}");
            }
            finally
            {
                BatchEnded?.Invoke(this, EventArgs.Empty);
                _isBatchRunning = false;
                _pauseGate.Set();
                _batchCts?.Dispose();
                _batchCts = null;
                IsBusy = false;
                UpdateBatchCommandStates();
                BatchRowOk = null;
                CurrentRowIndex = 0;
                CurrentImagePath = null;
                ClearBatchBaselineRois();
            }
        }

        private void UpdateBatchHeatmapIndex(int roiIndex)
        {
            InvokeOnUi(() => ApplyBatchHeatmapSelection(Math.Max(1, Math.Min(4, roiIndex))));
        }

        private bool TryBuildPlacementInput(out RoiPlacementInput input, bool logError)
        {
            input = default;

            if (_layoutOriginal?.Master1Pattern == null || _layoutOriginal.Master2Pattern == null)
            {
                return false;
            }

            if (_m1Detection == null || _m2Detection == null || !_m1Detection.IsOk || !_m2Detection.IsOk || !_batchAnchorsOk)
            {
                _trace?.Invoke("[anchors] ERROR: masters not detected or score below threshold. No ROI repositioned.");
                if (logError)
                {
                    ShowSnackbar("Master anchors not detected. ROIs not repositioned.");
                }

                return false;
            }

            var m1DetectedCenter = _m1Detection.Center!.Value;
            var m2DetectedCenter = _m2Detection.Center!.Value;
            var detM1 = new ImgPoint(m1DetectedCenter.X, m1DetectedCenter.Y);
            var detM2 = new ImgPoint(m2DetectedCenter.X, m2DetectedCenter.Y);
            var imageKey = Path.GetFileName(CurrentImagePath ?? string.Empty);

            return TryBuildPlacementInputFromDetections(detM1, detM2, imageKey, out input);
        }

        public bool TryBuildPlacementInputFromDetections(ImgPoint detM1, ImgPoint detM2, string? imageKey, out RoiPlacementInput input)
        {
            input = default;
            if (_layoutOriginal?.Master1Pattern == null || _layoutOriginal.Master2Pattern == null)
            {
                return false;
            }

            var (m1bx, m1by) = _layoutOriginal.Master1Pattern.GetCenter();
            var (m2bx, m2by) = _layoutOriginal.Master2Pattern.GetCenter();
            var m1BaselineCenter = new ImgPoint(m1bx, m1by);
            var m2BaselineCenter = new ImgPoint(m2bx, m2by);

            var analyzeOpts = _layoutOriginal.Analyze ?? new AnalyzeOptions();
            input = new RoiPlacementInput(
                m1BaselineCenter,
                m2BaselineCenter,
                detM1,
                detM2,
                analyzeOpts.DisableRot,
                analyzeOpts.ScaleLock,
                ScaleMode.None,
                true,
                BuildAnchorMap())
            {
                ImageKey = imageKey
            };

            return true;
        }

        public bool TryGetLayoutOriginalMasterCenters(out ImgPoint baseM1, out ImgPoint baseM2)
        {
            baseM1 = default;
            baseM2 = default;

            if (_layoutOriginal?.Master1Pattern == null || _layoutOriginal.Master2Pattern == null)
            {
                return false;
            }

            var (m1bx, m1by) = _layoutOriginal.Master1Pattern.GetCenter();
            var (m2bx, m2by) = _layoutOriginal.Master2Pattern.GetCenter();
            baseM1 = new ImgPoint(m1bx, m1by);
            baseM2 = new ImgPoint(m2bx, m2by);
            return true;
        }

        public bool TryApplyPlacementToLayout(MasterLayout layout, ImgPoint detM1, ImgPoint detM2, string? imageKey, out RoiPlacementOutput output)
        {
            output = default;
            if (!TryBuildPlacementInputFromDetections(detM1, detM2, imageKey, out var input))
            {
                return false;
            }

            if (_layoutOriginal?.Master1Pattern == null || _layoutOriginal.Master2Pattern == null)
            {
                return false;
            }

            var targets = BuildPlacementTargetsFromLayoutOriginal(includeLegacyInspection: true);
            if (targets.Count == 0)
            {
                return false;
            }

            var baselineMasters = new List<RoiModel>
            {
                _layoutOriginal.Master1Pattern.Clone(),
                _layoutOriginal.Master2Pattern.Clone()
            };
            var baselineInspections = targets.Select(t => t.Baseline).ToList();
            output = RoiPlacementEngine.Place(input, baselineMasters, baselineInspections);

            ApplyPlacementToLayout(layout, targets, output);
            LogBatchPlacement(output, input);
            return true;
        }

        private sealed record LayoutPlacementTarget(int SlotIndex, RoiModel Baseline);

        private IReadOnlyList<LayoutPlacementTarget> BuildPlacementTargetsFromLayoutOriginal(bool includeLegacyInspection)
        {
            if (_layoutOriginal == null)
            {
                return Array.Empty<LayoutPlacementTarget>();
            }

            var targets = new List<LayoutPlacementTarget>();
            var configs = _inspectionRois ?? _layoutOriginal.InspectionRois;

            if (configs != null && configs.Count > 0)
            {
                foreach (var cfg in configs.OrderBy(c => c.Index))
                {
                    var baseline = GetLayoutBaselineRoi(_layoutOriginal, cfg, null);
                    if (baseline != null)
                    {
                        targets.Add(new LayoutPlacementTarget(cfg.Index, baseline.Clone()));
                    }
                }
            }
            else
            {
                var fallback = new[]
                {
                    (1, _layoutOriginal.Inspection1),
                    (2, _layoutOriginal.Inspection2),
                    (3, _layoutOriginal.Inspection3),
                    (4, _layoutOriginal.Inspection4)
                };

                foreach (var (idx, roi) in fallback)
                {
                    if (roi != null)
                    {
                        targets.Add(new LayoutPlacementTarget(idx, roi.Clone()));
                    }
                }
            }

            if (includeLegacyInspection && _layoutOriginal.Inspection != null)
            {
                targets.Add(new LayoutPlacementTarget(0, _layoutOriginal.Inspection.Clone()));
            }

            return targets;
        }

        private static void ApplyPlacementToLayout(MasterLayout layout, IReadOnlyList<LayoutPlacementTarget> targets, RoiPlacementOutput output)
        {
            foreach (var master in output.MastersPlaced)
            {
                switch (master.Role)
                {
                    case RoiRole.Master1Pattern:
                        layout.Master1Pattern = master;
                        break;
                    case RoiRole.Master2Pattern:
                        layout.Master2Pattern = master;
                        break;
                }
            }

            for (int i = 0; i < targets.Count && i < output.InspectionsPlaced.Count; i++)
            {
                var placed = output.InspectionsPlaced[i];
                switch (targets[i].SlotIndex)
                {
                    case 1:
                        layout.Inspection1 = placed;
                        break;
                    case 2:
                        layout.Inspection2 = placed;
                        break;
                    case 3:
                        layout.Inspection3 = placed;
                        break;
                    case 4:
                        layout.Inspection4 = placed;
                        break;
                    case 0:
                        layout.Inspection = placed;
                        break;
                }
            }
        }

        private void ApplyPlacementToViewModel(IReadOnlyList<LayoutPlacementTarget> targets, RoiPlacementOutput output)
        {
            for (int i = 0; i < targets.Count && i < output.InspectionsPlaced.Count; i++)
            {
                var placed = output.InspectionsPlaced[i];
                switch (targets[i].SlotIndex)
                {
                    case 1:
                        Inspection1 = placed;
                        break;
                    case 2:
                        Inspection2 = placed;
                        break;
                    case 3:
                        Inspection3 = placed;
                        break;
                    case 4:
                        Inspection4 = placed;
                        break;
                }
            }
        }

        private Task RepositionInspectionRoisForImageAsync(Mat imageGray, string imagePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || imageGray == null || imageGray.Empty())
            {
                return Task.CompletedTask;
            }

            return RepositionInspectionRoisForImageCoreAsync(imageGray, imagePath, ct);
        }

        private void RepositionInspectionRois(RoiPlacementInput input)
        {
            if (_inspectionRois == null)
            {
                return;
            }

            var placementTargets = BuildPlacementTargetsFromLayoutOriginal(includeLegacyInspection: false);
            if (placementTargets.Count == 0)
            {
                return;
            }

            var baselineList = placementTargets.Select(p => p.Baseline).ToList();
            var output = RoiPlacementEngine.Place(input, Array.Empty<RoiModel>(), baselineList);

            ApplyPlacementToViewModel(placementTargets, output);

            LogBatchPlacement(output, input);
        }

        private MasterAnchorChoice ResolveAnchorMaster(InspectionRoiConfig? config, RoiModel roi, string caller)
        {
            if (config != null)
            {
                return config.AnchorMaster;
            }

            MasterAnchorChoice? resolved = null;
            var roiIndex = TryParseInspectionIndex(roi.Id) ?? TryParseInspectionIndex(roi.Label);

            if (roiIndex.HasValue)
            {
                resolved = GetInspectionConfigByIndex(roiIndex.Value)?.AnchorMaster
                           ?? _layoutOriginal?.InspectionRois.FirstOrDefault(r => r.Index == roiIndex.Value)?.AnchorMaster;
            }

            resolved ??= _layoutOriginal?.InspectionRois.FirstOrDefault(r =>
                string.Equals(r.Id, roi.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.ModelKey, roi.Id, StringComparison.OrdinalIgnoreCase))?.AnchorMaster;

            var anchor = resolved ?? MasterAnchorChoice.Mid;

            if (!resolved.HasValue)
            {
                LogAlign(FormattableString.Invariant(
                    $"[WARN] roi_id={roi.Id ?? "<null>"} caller={caller} cfg_anchor=<null> anchor_fallback={(int)anchor} reason=no_config"));
            }

            return anchor;
        }

        private IReadOnlyDictionary<string, MasterAnchorChoice> BuildAnchorMap()
        {
            var map = new Dictionary<string, MasterAnchorChoice>(StringComparer.OrdinalIgnoreCase);
            var configs = _inspectionRois ?? _layoutOriginal?.InspectionRois;
            if (configs == null)
            {
                return map;
            }

            foreach (var cfg in configs)
            {
                if (cfg == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(cfg.Id))
                {
                    map[cfg.Id] = cfg.AnchorMaster;
                }

                if (!string.IsNullOrWhiteSpace(cfg.ModelKey))
                {
                    map[cfg.ModelKey] = cfg.AnchorMaster;
                }
            }

            return map;
        }

        private void LogBatchPlacement(RoiPlacementOutput output, RoiPlacementInput input)
        {
            if (output == null)
            {
                return;
            }

            var key = Path.GetFileName(CurrentImagePath ?? string.Empty);
            LogAlign(FormattableString.Invariant(
                $"[PLACE][SUMMARY] key='{key}' M1_base=({input.BaseM1.X:0.###},{input.BaseM1.Y:0.###}) M1_det=({input.DetM1.X:0.###},{input.DetM1.Y:0.###}) M2_base=({input.BaseM2.X:0.###},{input.BaseM2.Y:0.###}) M2_det=({input.DetM2.X:0.###},{input.DetM2.Y:0.###}) rawScale={output.Debug.Scale:0.####} rawAngleDeg={output.Debug.AngleDeltaDeg:0.###} disableRot={input.DisableRot} scaleLock={input.ScaleLock} distBase={output.Debug.DistBase:0.###} distDet={output.Debug.DistDet:0.###}"));

            if (output.Debug.RoiDetails == null)
            {
                return;
            }

            foreach (var detail in output.Debug.RoiDetails)
            {
                LogAlign(FormattableString.Invariant(
                    $"[PLACE][ROI] id={detail.RoiId} anchor={detail.Anchor} base=({detail.BaselineCenter.X:0.###},{detail.BaselineCenter.Y:0.###}) new=({detail.NewCenter.X:0.###},{detail.NewCenter.Y:0.###}) dx={detail.Delta.X:0.###} dy={detail.Delta.Y:0.###} sizeBase=({detail.BaseWidth:0.###},{detail.BaseHeight:0.###},{detail.BaseR:0.###},{detail.BaseRInner:0.###}) sizeNew=({detail.NewWidth:0.###},{detail.NewHeight:0.###},{detail.NewR:0.###},{detail.NewRInner:0.###}) angleBase={detail.AngleBase:0.###} angleNew={detail.AngleNew:0.###}"));
            }
        }

        private static int? TryParseInspectionIndex(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var trimmed = key.Trim();
            if (trimmed.StartsWith("Inspection_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Inspection", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split('_', StringSplitOptions.RemoveEmptyEntries);
                var last = parts.LastOrDefault();
                if (last != null && int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
            {
                return direct;
            }

            return null;
        }

        private async Task RepositionInspectionRoisForImageCoreAsync(Mat imageGray, string imagePath, CancellationToken ct)
        {
            _batchAnchorsOk = await TryUpdateBatchAnchorsForImageAsync(imageGray, imagePath, ct).ConfigureAwait(false);
            await EnsureBatchRepositionAsync(imagePath, ct, "row-start").ConfigureAwait(false);
        }

        private async Task _repositionInspectionRoisAsync(string imagePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || _layoutOriginal == null)
            {
                TraceBatch("[match] SKIP: missing image/layout");
                _batchAnchorsOk = false;
                return;
            }

            if (_layoutOriginal.Master1Pattern == null || _layoutOriginal.Master2Pattern == null
                || _layoutOriginal.Master1Search == null || _layoutOriginal.Master2Search == null)
            {
                TraceBatch("[match] SKIP: missing master patterns/search");
                _batchAnchorsOk = false;
                return;
            }

            using var mat = new Mat(imagePath, ImreadModes.Grayscale);
            if (mat.Empty())
            {
                TraceBatch($"[match] SKIP: image '{Path.GetFileName(imagePath)}' could not be loaded");
                _batchAnchorsOk = false;
                return;
            }

            await TryUpdateBatchAnchorsForImageAsync(mat, imagePath, ct).ConfigureAwait(false);
        }

        private Task<bool> TryUpdateBatchAnchorsForImageAsync(Mat imageGray, string imagePath, CancellationToken ct)
        {
            _batchAnchorsOk = false;
            _batchAnchorM1Ready = false;
            _batchAnchorM2Ready = false;
            _batchAnchorM1Score = 0;
            _batchAnchorM2Score = 0;
            _batchDetectedM1 = null;
            _batchDetectedM2 = null;
            _m1Detection = new MasterDetection();
            _m2Detection = new MasterDetection();

            var fileName = Path.GetFileName(imagePath) ?? string.Empty;
            TraceBatch($"[batch] match: start file='{fileName}'");
            GuiLog.Info($"[batch] match: start file='{fileName}'");

            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                GuiLog.Warn("[batch] match: missing image path");
                return Task.FromResult(false);
            }

            if (_layoutOriginal == null)
            {
                GuiLog.Warn("[batch] match: no layout loaded; anchors cannot be computed");
                return Task.FromResult(false);
            }

            if (_layoutOriginal.Master1Pattern == null || _layoutOriginal.Master2Pattern == null
                || _layoutOriginal.Master1Search == null || _layoutOriginal.Master2Search == null)
            {
                GuiLog.Warn("[batch] match: master ROIs are missing (pattern/search)");
                return Task.FromResult(false);
            }

            var analyze = _layoutOriginal.Analyze ?? new AnalyzeOptions();
            var featureM1 = GetBatchFeatureM1();
            var featureM2 = GetBatchFeatureM2();
            var thrM1 = GetBatchThrM1();
            var thrM2 = GetBatchThrM2();

            _trace?.Invoke(FormattableString.Invariant($"[MASTER] M1 feature={featureM1} thr={thrM1}"));
            _trace?.Invoke(FormattableString.Invariant($"[MASTER] M2 feature={featureM2} thr={thrM2}"));

            if (imageGray == null || imageGray.Empty())
            {
                GuiLog.Warn($"[batch] match: image '{fileName}' could not be loaded for anchors");
                return Task.FromResult(false);
            }

            var pattern1 = GetOrLoadPatternCached(_layoutOriginal.Master1PatternImagePath, "M1");
            var pattern2 = GetOrLoadPatternCached(_layoutOriginal.Master2PatternImagePath, "M2");

            if (pattern1 == null || pattern2 == null)
            {
                if ((pattern1 == null && !_warnedMissingM1PatternPath)
                    || (pattern2 == null && !_warnedMissingM2PatternPath))
                {
                    GuiLog.Warn("[batch] match: missing reference patterns for M1/M2");
                }
                return Task.FromResult(false);
            }

            var m1Result = LocalMatcher.MatchInSearchROIWithDetailsGray(
                imageGray,
                _layoutOriginal.Master1Pattern,
                _layoutOriginal.Master1Search,
                featureM1,
                thrM1,
                analyze.RotRange,
                analyze.ScaleMin,
                analyze.ScaleMax,
                pattern1,
                _trace);

            var m2Result = LocalMatcher.MatchInSearchROIWithDetailsGray(
                imageGray,
                _layoutOriginal.Master2Pattern,
                _layoutOriginal.Master2Search,
                featureM2,
                thrM2,
                analyze.RotRange,
                analyze.ScaleMin,
                analyze.ScaleMax,
                pattern2,
                _trace);

            _m1Detection = new MasterDetection
            {
                Center = m1Result.Center,
                AngleDeg = m1Result.AngleDeg,
                Score = m1Result.Score
            };

            _m2Detection = new MasterDetection
            {
                Center = m2Result.Center,
                AngleDeg = m2Result.AngleDeg,
                Score = m2Result.Score
            };

            _trace?.Invoke(FormattableString.Invariant($"[MASTER] M1 center={_m1Detection.Center} angle={_m1Detection.AngleDeg:F1} score={_m1Detection.Score}"));
            _trace?.Invoke(FormattableString.Invariant($"[MASTER] M2 center={_m2Detection.Center} angle={_m2Detection.AngleDeg:F1} score={_m2Detection.Score}"));

            if (_m1Detection.Center == null || _m2Detection.Center == null)
            {
                TraceBatch(FormattableString.Invariant(
                    $"[batch] match: failed M1={_m1Detection.IsOk} M2={_m2Detection.IsOk} scores(M1={_m1Detection.Score:0.00}, M2={_m2Detection.Score:0.00})"));
                GuiLog.Warn(FormattableString.Invariant(
                    $"[batch] match: anchors not found for file='{fileName}' (M1={_m1Detection.IsOk} M2={_m2Detection.IsOk})"));
                _log?.Invoke("[batch] Anclajes no detectados (Master1 o Master2). Se omite reposicionamiento de ROIs.");
                return Task.FromResult(false);
            }

            TraceBatch(FormattableString.Invariant(
                $"[batch] match: M1=({_m1Detection.Center.Value.X:0.0},{_m1Detection.Center.Value.Y:0.0}) score={_m1Detection.Score:0.00}  M2=({_m2Detection.Center.Value.X:0.0},{_m2Detection.Center.Value.Y:0.0}) score={_m2Detection.Score:0.00}"));

            var m1Base = _layoutOriginal.Master1Pattern.GetCenter();
            var m2Base = _layoutOriginal.Master2Pattern.GetCenter();

            RegisterBatchAnchors(
                new Point(m1Base.cx, m1Base.cy),
                new Point(m2Base.cx, m2Base.cy),
                new Point(_m1Detection.Center.Value.X, _m1Detection.Center.Value.Y),
                new Point(_m2Detection.Center.Value.X, _m2Detection.Center.Value.Y),
                _m1Detection.Score,
                _m2Detection.Score);

            _batchAnchorsOk = AnchorsMeetThreshold();
            GuiLog.Info(FormattableString.Invariant(
                $"[batch] match: M1 score={_m1Detection.Score:0.0} M2 score={_m2Detection.Score:0.0} anchorsOk={_batchAnchorsOk}"));
            return Task.FromResult(_batchAnchorsOk);
        }

        private void InvalidateMasterPatternCache()
        {
            lock (_masterPatternCacheLock)
            {
                _cachedM1Pattern?.Dispose();
                _cachedM2Pattern?.Dispose();
                _cachedM1Pattern = null;
                _cachedM2Pattern = null;
                _cachedM1PatternPath = null;
                _cachedM2PatternPath = null;
                _cachedM1PatternLastWriteUtc = null;
                _cachedM1PatternSize = null;
                _cachedM2PatternLastWriteUtc = null;
                _cachedM2PatternSize = null;
                _warnedMissingM1PatternPath = false;
                _warnedMissingM2PatternPath = false;
            }
        }

        private Mat? GetOrLoadPatternCached(string? path, string tag)
        {
            bool isM1 = string.Equals(tag, "M1", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path))
            {
                WarnMissingPatternOnce(tag, "missing pattern path");
                return null;
            }

            var effectivePath = ResolvePatternPath(path, tag);
            lock (_masterPatternCacheLock)
            {
                DateTime lastWriteUtc;
                long size;

                try
                {
                    var fileInfo = new FileInfo(effectivePath);
                    if (!fileInfo.Exists)
                    {
                        WarnMissingPatternOnce(tag, $"pattern not found at '{effectivePath}'");
                        return null;
                    }

                    lastWriteUtc = fileInfo.LastWriteTimeUtc;
                    size = fileInfo.Length;
                }
                catch (Exception ex)
                {
                    WarnMissingPatternOnce(tag, $"pattern not found at '{effectivePath}': {ex.Message}");
                    return null;
                }

                var cachedPath = isM1 ? _cachedM1PatternPath : _cachedM2PatternPath;
                var cachedMat = isM1 ? _cachedM1Pattern : _cachedM2Pattern;
                var cachedLastWriteUtc = isM1 ? _cachedM1PatternLastWriteUtc : _cachedM2PatternLastWriteUtc;
                var cachedSize = isM1 ? _cachedM1PatternSize : _cachedM2PatternSize;

                if (cachedMat != null && !cachedMat.Empty() &&
                    string.Equals(cachedPath, effectivePath, StringComparison.OrdinalIgnoreCase) &&
                    cachedLastWriteUtc.HasValue && cachedLastWriteUtc.Value == lastWriteUtc &&
                    cachedSize.HasValue && cachedSize.Value == size)
                {
                    return cachedMat;
                }

                if (cachedMat != null)
                {
                    cachedMat.Dispose();
                    if (isM1)
                    {
                        _cachedM1Pattern = null;
                        _cachedM1PatternLastWriteUtc = null;
                        _cachedM1PatternSize = null;
                    }
                    else
                    {
                        _cachedM2Pattern = null;
                        _cachedM2PatternLastWriteUtc = null;
                        _cachedM2PatternSize = null;
                    }
                }

                if (string.Equals(cachedPath, effectivePath, StringComparison.OrdinalIgnoreCase))
                {
                    TraceBatch(FormattableString.Invariant(
                        $"[master-cache] reload {(isM1 ? "M1" : "M2")} changed: '{effectivePath}'"));
                }

                try
                {
                    var mat = Cv2.ImRead(effectivePath, ImreadModes.Grayscale);
                    if (mat.Empty())
                    {
                        mat.Dispose();
                        WarnMissingPatternOnce(tag, $"pattern empty at '{effectivePath}'");
                        return null;
                    }

                    if (isM1)
                    {
                        _cachedM1Pattern = mat;
                        _cachedM1PatternPath = effectivePath;
                        _cachedM1PatternLastWriteUtc = lastWriteUtc;
                        _cachedM1PatternSize = size;
                        _warnedMissingM1PatternPath = false;
                    }
                    else
                    {
                        _cachedM2Pattern = mat;
                        _cachedM2PatternPath = effectivePath;
                        _cachedM2PatternLastWriteUtc = lastWriteUtc;
                        _cachedM2PatternSize = size;
                        _warnedMissingM2PatternPath = false;
                    }

                    return mat;
                }
                catch (Exception ex)
                {
                    WarnMissingPatternOnce(tag, $"failed to load pattern: {ex.Message}");
                    return null;
                }
            }
        }

        private string ResolvePatternPath(string path, string tag)
        {
            var effectivePath = path;

            if (!string.IsNullOrWhiteSpace(_currentLayoutName))
            {
                const string lastSegment = "\\Recipes\\last\\";
                var recipeSegment = "\\Recipes\\" + _currentLayoutName + "\\";

                if (effectivePath.IndexOf(lastSegment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var candidate = effectivePath.Replace(lastSegment, recipeSegment, StringComparison.OrdinalIgnoreCase);
                    if (File.Exists(candidate))
                    {
                        TraceBatch(FormattableString.Invariant(
                            $"[match] Redirecting {tag} pattern template from 'last' to '{_currentLayoutName}': '{candidate}'"));
                        effectivePath = candidate;
                    }
                }
            }

            return effectivePath;
        }

        private void WarnMissingPatternOnce(string tag, string message)
        {
            bool isM1 = string.Equals(tag, "M1", StringComparison.OrdinalIgnoreCase);
            if (isM1 && _warnedMissingM1PatternPath)
            {
                return;
            }

            if (!isM1 && _warnedMissingM2PatternPath)
            {
                return;
            }

            TraceBatch(FormattableString.Invariant($"[match] {tag} {message}"));

            if (isM1)
            {
                _warnedMissingM1PatternPath = true;
            }
            else
            {
                _warnedMissingM2PatternPath = true;
            }
        }

        private async Task EnsureBatchRepositionAsync(string imagePath, CancellationToken ct, string reason)
        {
            if (_batchRepositionGuard)
            {
                TraceBatch($"[batch-repos] SKIP: guard active reason={reason}");
                return;
            }

            try
            {
                _batchRepositionGuard = true;
                var fileName = string.IsNullOrWhiteSpace(imagePath)
                    ? "<none>"
                    : Path.GetFileName(imagePath);

                var stepId = BatchStepId;

                TraceBatch($"[batch-repos] BEGIN row={CurrentRowIndex} file='{fileName}' reason={reason} useCanvas={UseCanvasPlacementForBatchHeatmap}");

                TraceBatchInspectionRoisSnapshot("BEFORE");

                if (_layoutOriginal != null)
                {
                    if (_batchAnchorReadyForStep != _batchStepId)
                    {
                        await _repositionInspectionRoisAsync(imagePath, ct).ConfigureAwait(false);
                    }
                    if (!TryBuildPlacementInput(out var placementInput, logError: true))
                    {
                        TraceBatch("[batch-repos] skip: anchors unavailable for reposition");
                        return;
                    }

                    RepositionInspectionRois(placementInput);
                    TraceBatch(FormattableString.Invariant($"[batch-repos] reposition layout DONE stepId={stepId}"));
                }
                else if (_repositionInspectionRoisAsyncExternal != null)
                {
                    await _repositionInspectionRoisAsyncExternal(imagePath, stepId, ct).ConfigureAwait(false);
                    TraceBatch(FormattableString.Invariant($"[batch-repos] reposition delegate DONE stepId={stepId}"));
                }
                else
                {
                    TraceBatch("[batch-repos] SKIP: no reposition delegate available");
                }

                ClearBaselines();
                SeedInspectionBaselines($"batch-repos:{reason}");
                InvokeOnUi(RedrawOverlays);

                TraceBatchInspectionRoisSnapshot("AFTER");

                TraceBatch($"[batch-repos] END row={CurrentRowIndex} file='{fileName}'");
            }
            catch (OperationCanceledException)
            {
                TraceBatch("[batch-repos] CANCELLED");
                throw;
            }
            catch (Exception ex)
            {
                TraceBatch($"[batch-repos] FAIL: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _batchRepositionGuard = false;
            }
        }

        private bool CanExecuteStart()
        {
            if (_isBatchRunning)
            {
                return !_pauseGate.IsSet;
            }

            return CanStartBatch && !IsBusy;
        }

        private void UpdateBatchCommandStates()
        {
            StartBatchCommand?.RaiseCanExecuteChanged();
            PauseBatchCommand?.RaiseCanExecuteChanged();
            StopBatchCommand?.RaiseCanExecuteChanged();
        }

        private void UpdateBatchProgress(int processed, int total)
        {
            string summary = total > 0
                ? $"{total} images found · {processed}/{total} processed"
                : "No images found";
            SetBatchSummarySafe(summary);
        }

        private void SetBatchStatusSafe(string message)
        {
            InvokeOnUi(() => BatchStatus = message);
        }

        private void SetBatchSummarySafe(string summary)
        {
            InvokeOnUi(() => BatchSummary = summary);
        }

        private void UpdateCanStart()
        {
            var folder = _batchFolder;
            bool canStart = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && _batchRows.Count > 0;
            CanStartBatch = canStart;
        }

        private async Task DoBrowseBatchFolderAsync()
        {
            string? dir = null;

            try
            {
                InvokeOnUi(() =>
                {
                    using var dlg = new Forms.FolderBrowserDialog
                    {
                        Description = "Select image folder",
                        UseDescriptionForTitle = true
                    };
                    var result = dlg.ShowDialog();
                    if (result == Forms.DialogResult.OK)
                    {
                        dir = dlg.SelectedPath;
                    }
                });
            }
            catch (Exception ex)
            {
                _log($"[batch] browse failed: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return;
            }

            BatchFolder = dir;
            await LoadBatchListFromFolderAsync(dir, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task LoadBatchListFromFolderAsync(string dir, CancellationToken ct)
        {
            List<BatchRow> rows;
            try
            {
                rows = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    _log($"[batch] dir={dir} exists={Directory.Exists(dir)}");
                    var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                        .Where(p => BatchImageExtensions.Contains(Path.GetExtension(p)))
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    _log($"[batch] {files.Count} image files found in '{dir}' (including subfolders)");

                    for (int i = 0; i < Math.Min(12, files.Count); i++)
                    {
                        _log($"[batch] file[{i}] {files[i]}");
                    }

                    return files.Select(f => new BatchRow(f)).ToList();
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[batch] load folder failed: {ex.Message}");
                InvokeOnUi(() =>
                {
                    _batchRows.Clear();
                    BatchSummary = $"error: {ex.Message}";
                    BatchStatus = string.Empty;
                    BatchImageSource = null;
                    ClearBatchHeatmap();
                    UpdateCanStart();
                });
                return;
            }

            InvokeOnUi(() =>
            {
                _batchRows.Clear();
                int index = 0;
                foreach (var row in rows)
                {
                    index++;
                    row.IndexOneBased = index;
                    row.ROI1 = BatchCellStatus.Unknown;
                    row.ROI2 = BatchCellStatus.Unknown;
                    row.ROI3 = BatchCellStatus.Unknown;
                    row.ROI4 = BatchCellStatus.Unknown;
                    _batchRows.Add(row);
                }

                BatchSummary = rows.Count == 0 ? $"no images in {dir}" : $"{rows.Count} images found in {dir}";
                BatchStatus = string.Empty;
                BatchImageSource = null;
                ClearBatchHeatmap();
                UpdateCanStart();
                OnPropertyChanged(nameof(BatchRows));
            });
        }

        private List<BatchRow> GetBatchRowsSnapshot()
        {
            List<BatchRow>? snapshot = null;
            InvokeOnUi(() => snapshot = _batchRows.ToList());
            return snapshot ?? new List<BatchRow>();
        }

        private static bool? ComputeBatchRowOk(BatchRow row)
        {
            var statuses = new[]
            {
                row.ROI1,
                row.ROI2,
                row.ROI3,
                row.ROI4
            };

            if (statuses.All(s => s == BatchCellStatus.Unknown))
            {
                return null;
            }

            if (statuses.Any(s => s == BatchCellStatus.Nok))
            {
                return false;
            }

            return true;
        }

        private void SetBatchCellStatus(BatchRow row, int roiIndex, bool? isOk)
        {
            var status =
                isOk == null
                    ? BatchCellStatus.Unknown
                    : (isOk.Value ? BatchCellStatus.Ok : BatchCellStatus.Nok);

            InvokeOnUi(() =>
            {
                switch (roiIndex)
                {
                    case 0:
                        row.ROI1 = status;
                        break;
                    case 1:
                        row.ROI2 = status;
                        break;
                    case 2:
                        row.ROI3 = status;
                        break;
                    case 3:
                        row.ROI4 = status;
                        break;
                }

                BatchRowOk = ComputeBatchRowOk(row);
            });
        }

        private InspectionRoiConfig? GetInspectionConfigByIndex(int index)
            => _inspectionRois?.FirstOrDefault(r => r.Index == index);

        private RoiModel? GetInspectionModelByIndex(int index)
            => index switch
            {
                1 => Inspection1,
                2 => Inspection2,
                3 => Inspection3,
                4 => Inspection4,
                _ => null
            };

        private void UpdateBatchRowStatus(BatchRow row, int roiIndex, BatchCellStatus status)
        {
            if (row == null)
            {
                return;
            }

            bool? isOk = status switch
            {
                BatchCellStatus.Ok => true,
                BatchCellStatus.Nok => false,
                _ => (bool?)null
            };

            SetBatchCellStatus(row, roiIndex - 1, isOk);
        }

        private bool IsRowNg(BatchRow row)
        {
            if (row == null)
            {
                return false;
            }

            return row.ROI1 == BatchCellStatus.Nok
                || row.ROI2 == BatchCellStatus.Nok
                || row.ROI3 == BatchCellStatus.Nok
                || row.ROI4 == BatchCellStatus.Nok;
        }

        private async Task OnRowCompletedAsync(BatchRow row, CancellationToken ct)
        {
            if (row == null)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(row.FullPath) || !File.Exists(row.FullPath))
                {
                    return;
                }

                bool isNg = IsRowNg(row);
                string fileName = Path.GetFileName(row.FullPath);
                string status = isNg ? "NG" : "OK";
                string label = $"{fileName} — {status}";

                string? baseDir = null;
                if (!string.IsNullOrWhiteSpace(BatchFolder) && Directory.Exists(BatchFolder))
                {
                    baseDir = BatchFolder;
                }
                else
                {
                    baseDir = Path.GetDirectoryName(row.FullPath);
                }

                if (string.IsNullOrWhiteSpace(baseDir))
                {
                    _log("[batch] annotate skipped: unresolved output directory");
                    return;
                }

                _log("[batch] annotate skipped: persistence disabled");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[batch] annotate failed: {ex.Message}");
            }
        }

        private static async Task RunOnStaThreadAsync(Action action, CancellationToken cancellationToken)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);

            CancellationTokenRegistration registration = default;
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() =>
                {
                    tcs.TrySetCanceled(cancellationToken);
                });
            }

            thread.Start();

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        private static BitmapSource NormalizeDpiTo96(BitmapSource src)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }

            if (Math.Abs(src.DpiX - 96.0) < 0.01 && Math.Abs(src.DpiY - 96.0) < 0.01)
            {
                return src;
            }

            var converted = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            if (converted.CanFreeze)
            {
                converted.Freeze();
            }

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = (width * converted.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var writable = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            writable.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            writable.Freeze();
            return writable;
        }

        private static BitmapSource DecodeImageTo96Dpi(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return NormalizeDpiTo96(bmp);
        }

        private static WriteableBitmap ApplyGreenPalette(BitmapSource grayLike, int cutoffPercent)
        {
            if (grayLike == null)
            {
                throw new ArgumentNullException(nameof(grayLike));
            }

            var gray = grayLike.Format == PixelFormats.Gray8
                ? grayLike
                : new FormatConvertedBitmap(grayLike, PixelFormats.Gray8, null, 0);

            if (gray.CanFreeze)
            {
                gray.Freeze();
            }

            int width = gray.PixelWidth;
            int height = gray.PixelHeight;
            int strideGray = (width * gray.Format.BitsPerPixel + 7) / 8;
            var grayBuffer = new byte[strideGray * height];
            gray.CopyPixels(grayBuffer, strideGray, 0);

            var output = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            int strideOut = (width * output.Format.BitsPerPixel + 7) / 8;
            var outBuffer = new byte[strideOut * height];

            int cutoffByte = Math.Clamp((int)(cutoffPercent * 255 / 100.0), 0, 255);
            int denom = Math.Max(1, 255 - cutoffByte);

            for (int y = 0; y < height; y++)
            {
                int grayRow = y * strideGray;
                int outRow = y * strideOut;
                for (int x = 0; x < width; x++)
                {
                    byte value = grayBuffer[grayRow + x];
                    int offset = outRow + x * 4;

                    if (value == 0)
                    {
                        outBuffer[offset + 3] = 0;
                        continue;
                    }

                    if (value >= cutoffByte)
                    {
                        double t = (value - cutoffByte) / (double)denom;
                        t = Math.Clamp(t, 0.0, 1.0);
                        t = Math.Pow(t, 0.85);
                        byte alpha = (byte)Math.Round(255.0 * t);
                        outBuffer[offset + 0] = 113;
                        outBuffer[offset + 1] = 204;
                        outBuffer[offset + 2] = 46;
                        outBuffer[offset + 3] = alpha;
                    }
                    else
                    {
                        outBuffer[offset + 3] = 0;
                    }
                }
            }

            output.WritePixels(new Int32Rect(0, 0, width, height), outBuffer, strideOut, 0);
            output.Freeze();
            return output;
        }

        private static void VmLog(string msg)
        {
            Debug.WriteLine(msg);
            GuiLog.Info(msg);
        }

        private static void LogImg(string tag, ImageSource? src)
        {
            try
            {
                if (src is BitmapSource b)
                {
                    VmLog($"[heatmap:{tag}] type={b.GetType().Name} px={b.PixelWidth}x{b.PixelHeight} dpi=({b.DpiX:0.##},{b.DpiY:0.##}) fmt={b.Format} frozen={b.IsFrozen}");
                }
                else
                {
                    VmLog($"[heatmap:{tag}] src={(src == null ? "NULL" : src.GetType().Name)}");
                }
            }
            catch
            {
            }
        }

        private void LogHeatmapState(string tag)
        {
            try
            {
                var bytes = _lastHeatmapPngBytes?.Length ?? 0;
                VmLog($"[heatmap:{tag}] cutoff={HeatmapCutoffPercent}% bytes={bytes}");
                if (_lastHeatmapBitmap != null)
                {
                    VmLog($"[heatmap:{tag}] wb px={_lastHeatmapBitmap.PixelWidth}x{_lastHeatmapBitmap.PixelHeight} dpi=({_lastHeatmapBitmap.DpiX:0.##},{_lastHeatmapBitmap.DpiY:0.##})");
                }
            }
            catch
            {
            }
        }

        private void SetBatchBaseImage(byte[] imageBytes, string imagePathForLog)
        {
            InvokeOnUi(() =>
            {
                try
                {
                    var bmp = DecodeImageTo96Dpi(imageBytes);
                    BatchImageSource = bmp;
                    LogImg("base:set", BatchImageSource);
                }
                catch (Exception ex)
                {
                    _log($"[batch] image load failed: {ex.Message} ({Path.GetFileName(imagePathForLog)})");
                    BatchImageSource = null;
                }

                ClearBatchHeatmap();
                ClearBaselines();
            });
        }

        private void SetBatchBaseImage(string path)
        {
            InvokeOnUi(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    var base96 = NormalizeDpiTo96(bmp);
                    BatchImageSource = base96;
                    LogImg("base:set", BatchImageSource);
                }
                catch (Exception ex)
                {
                    _log($"[batch] image load failed: {ex.Message}");
                    BatchImageSource = null;
                }

                ClearBatchHeatmap();
                ClearBaselines();
            });
        }

        private static string ComputeHashTag(byte[] data)
        {
            try
            {
                var hash = SHA256.HashData(data);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant()[..8];
            }
            catch
            {
                return "(hash-err)";
            }
        }

        private static (int Width, int Height) GetImageSizeSafe(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var frame = decoder.Frames[0];
                    return (frame.PixelWidth, frame.PixelHeight);
                }
            }
            catch
            {
            }

            return (0, 0);
        }

        private void ApplyBatchHeatmapSelection(int roiIndex)
        {
            BatchHeatmapRoiIndex = roiIndex;

            if (_batchHeatmapPngByRoi.TryGetValue(BatchHeatmapRoiIndex, out var bytes) && bytes.Length > 0)
            {
                _lastHeatmapPngBytes = bytes;
                UpdateHeatmapThreshold();

                var (w, h) = GetImageSizeSafe(bytes);
                GuiLog.Info($"[batch-hm] set-ui roi={BatchHeatmapRoiIndex} bytes={bytes.Length} hash={ComputeHashTag(bytes)} dims={w}x{h} source={(BatchHeatmapSource != null)}");
            }
            else
            {
                _lastHeatmapPngBytes = null;
                BatchHeatmapSource = null;
                OnPropertyChanged(nameof(BatchHeatmapSource));
                GuiLog.Info($"[batch-hm] set-ui roi={BatchHeatmapRoiIndex} bytes=0 hash=∅ dims=0x0 source=false");
            }
        }

        public void SetBatchHeatmapForRoi(byte[]? heatmapPngBytes, int roiIndex)
        {
            var clampedIndex = Math.Max(1, Math.Min(4, roiIndex));

            if (heatmapPngBytes != null && heatmapPngBytes.Length > 0)
            {
                _batchHeatmapPngByRoi[clampedIndex] = heatmapPngBytes;
            }
            else
            {
                _batchHeatmapPngByRoi.Remove(clampedIndex);
            }

            try
            {
                ApplyBatchHeatmapSelection(clampedIndex);

                if (_isBatchRunning)
                {
                    if (roiIndex == 1)
                    {
                        _batchAnchorM1Ready = true;
                    }
                    else if (roiIndex == 2)
                    {
                        _batchAnchorM2Ready = true;
                    }

                    TraceBatch($"[batch] anchors state M1={_batchAnchorM1Ready} M2={_batchAnchorM2Ready} after roiIdx={roiIndex}");
                }
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[batch-hm:VM] SetBatchHeatmapForRoi failed", ex); // CODEX: FormattableString compatibility.
            }
        }

        private static GlobalInferResult ToGlobalInferResult(WorkflowInferResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return new GlobalInferResult
            {
                score = result.score,
                threshold = result.threshold,
                heatmap_png_base64 = result.heatmap_png_base64,
                regions = result.regions?.Select(r => new BrakeDiscInspector_GUI_ROI.InferRegion
                {
                    x = r.x,
                    y = r.y,
                    w = r.w,
                    h = r.h,
                    area_px = r.area_px,
                    area_mm2 = r.area_mm2,
                }).ToArray(),
            };
        }

        private void UpdateHeatmapFromResult(GlobalInferResult result, int roiIndex)
        {
            if (result == null)
            {
                InvokeOnUi(() => SetBatchHeatmapForRoi(null, roiIndex));
                return;
            }

            try
            {
                bool hasThreshold = result.threshold.HasValue && result.threshold.Value > 0;
                bool isNg = hasThreshold && result.score > result.threshold.Value;
                if (hasThreshold && !isNg)
                {
                    InvokeOnUi(() => SetBatchHeatmapForRoi(null, roiIndex));
                    return;
                }

                if (string.IsNullOrWhiteSpace(result.heatmap_png_base64))
                {
                    InvokeOnUi(() => SetBatchHeatmapForRoi(null, roiIndex));
                    return;
                }

                var heatmapBytes = Convert.FromBase64String(result.heatmap_png_base64);
                var (w, h) = GetImageSizeSafe(heatmapBytes);
                GuiLog.Info($"[heatmap] recv file='{System.IO.Path.GetFileName(CurrentImagePath ?? string.Empty)}' roi={roiIndex} bytes={heatmapBytes.Length} hash={ComputeHashTag(heatmapBytes)} dims={w}x{h}");

                InvokeOnUi(() =>
                {
                    try
                    {
                        SetBatchHeatmapForRoi(heatmapBytes, roiIndex);
                        LogImg("hm:set-after-update", BatchHeatmapSource);
                        LogHeatmapState("hm:after-update");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error($"[heatmap] failed to decode batch heatmap", ex); // CODEX: FormattableString compatibility.
                        SetBatchHeatmapForRoi(null, roiIndex);
                    }
                });
            }
            catch (FormatException ex)
            {
                _log($"[batch] invalid heatmap payload: {ex.Message}");
                InvokeOnUi(() => SetBatchHeatmapForRoi(null, roiIndex));
            }
        }

        private void UpdateHeatmapThreshold()
        {
            HeatmapInfo = $"Cutoff: {HeatmapCutoffPercent}%";

            _batchHeatmapPngByRoi.TryGetValue(BatchHeatmapRoiIndex, out _lastHeatmapPngBytes);

            if (_lastHeatmapPngBytes == null || _lastHeatmapPngBytes.Length == 0)
            {
                BatchHeatmapSource = null;
                OnPropertyChanged(nameof(BatchHeatmapSource));
                return;
            }

            try
            {
                var decoded = DecodeImageTo96Dpi(_lastHeatmapPngBytes!);
                _lastHeatmapBitmap = ApplyGreenPalette(decoded, HeatmapCutoffPercent);
                BatchHeatmapSource = _lastHeatmapBitmap;
                OnPropertyChanged(nameof(BatchHeatmapSource));
                LogImg("hm:decoded96", decoded);
                LogImg("hm:colored96", _lastHeatmapBitmap);
                LogHeatmapState("hm:threshold");
            }
            catch (Exception ex)
            {
                GuiLog.Error($"[heatmap] UpdateHeatmapThreshold failed", ex); // CODEX: FormattableString compatibility.
            }
        }

        private void ClearBatchHeatmap()
        {
            BatchHeatmapSource = null;
            BatchHeatmapRoiIndex = 0;
            _lastHeatmapBitmap = null;
            _lastHeatmapPngBytes = null;
            _batchHeatmapPngByRoi.Clear();
        }

        private Task<(byte[] Bytes, string FileName, string ShapeJson)> ExportRoiFromMatAsync(
            InspectionRoiConfig roiConfig,
            Mat src,
            string imagePath,
            CancellationToken ct)
        {
            if (roiConfig == null)
            {
                throw new ArgumentNullException(nameof(roiConfig));
            }

            ct.ThrowIfCancellationRequested();

            var roiModel = GetInspectionModelByIndex(roiConfig.Index);
            if (roiModel == null)
            {
                throw new InvalidOperationException($"ROI model missing for index {roiConfig.Index}.");
            }

            var roiForExport = GetBatchTransformedRoi(roiModel);

            if (!BackendAPI.TryPrepareCanonicalRoi(src, roiForExport, out var payload, out var fileName, _log) || payload == null)
            {
                throw new InvalidOperationException($"ROI export failed for '{imagePath}'.");
            }

            var shapeJson = payload.ShapeJson ?? string.Empty;
            return Task.FromResult((payload.PngBytes, fileName, shapeJson));
        }

        private static void InvokeOnUi(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }

        private void NotifyBatchCaption(string fileName, bool isOk)
        {
            try
            {
                OverlayBatchCaption?.Invoke(fileName, isOk);
                TraceBatch(FormattableString.Invariant($"[batch-ui] caption msg='{fileName}' isError={!isOk} snackbarSkipped=true"));
            }
            catch
            {
            }
        }

        private void ShowSnackbar(string message)
        {
            if (_showSnackbar == null)
            {
                return;
            }

            InvokeOnUi(() => _showSnackbar?.Invoke(message));
        }

        private void UpdateInferenceSummary()
        {
            if (InferenceScore == null)
            {
                InferenceSummary = string.Empty;
                return;
            }

            double thr = LocalThreshold > 0 ? LocalThreshold : (InferenceThreshold ?? 0);
            var sb = new StringBuilder();
            sb.AppendFormat("Score={0:0.###}", InferenceScore.Value);
            if (InferenceThreshold.HasValue)
            {
                sb.AppendFormat(" BackendThr={0:0.###}", InferenceThreshold.Value);
            }
            if (thr > 0)
            {
                sb.AppendFormat(" LocalThr={0:0.###}", thr);
                sb.Append(InferenceScore.Value > thr ? " → NG" : " → OK");
            }
            InferenceSummary = sb.ToString();
        }

        private static string NormalizeInspectionKey(string? key, int index)
        {
            var clamped = index;
            if (clamped < 1) clamped = 1;
            if (clamped > 4) clamped = 4;

            if (!string.IsNullOrWhiteSpace(key))
            {
                var match = Regex.Match(key, @"inspection[\s_\-]?([1-4])", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return $"inspection-{match.Groups[1].Value}";
                }
            }

            return $"inspection-{clamped}";
        }

        private static string? NormalizeInspectionKeyFromMetadata(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var match = Regex.Match(key, @"inspection[\s_\-]?([1-4])", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return $"inspection-{match.Groups[1].Value}";
            }

            return null;
        }

        private void EnsureRoleRoi()
        {
            if (string.IsNullOrWhiteSpace(RoleId))
                RoleId = "DefaultRole";
            if (string.IsNullOrWhiteSpace(RoiId))
                RoiId = "DefaultRoi";
        }

        private async Task<bool> EnsureFitEndpointAsync()
        {
            if (_hasFitEndpoint.HasValue)
            {
                return _hasFitEndpoint.Value;
            }

            _hasFitEndpoint = await _client.SupportsEndpointAsync("fit_ok").ConfigureAwait(false);
            return _hasFitEndpoint.Value;
        }

        private async Task<bool> EnsureCalibrateEndpointAsync()
        {
            if (_hasCalibrateEndpoint.HasValue)
            {
                return _hasCalibrateEndpoint.Value;
            }

            _hasCalibrateEndpoint = await _client.SupportsEndpointAsync("calibrate_ng").ConfigureAwait(false);
            return _hasCalibrateEndpoint.Value;
        }

        private void UpdateGlobalBadge()
        {
            try
            {
                var state = CalcGlobalDiskOk();
                if (Application.Current?.Dispatcher == null)
                {
                    _updateGlobalBadge(state);
                    return;
                }

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    _updateGlobalBadge(state);
                }
                else
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => _updateGlobalBadge(state)));
                }
            }
            catch
            {
                // ignore badge errors
            }
        }

        private bool? CalcGlobalDiskOk()
        {
            if (_inspectionRois == null)
            {
                return null;
            }

            var enabled = _inspectionRois.Where(r => r.Enabled).ToList();
            if (enabled.Count == 0)
            {
                return null;
            }

            if (enabled.Any(r => r.LastResultOk == false))
            {
                return false;
            }

            if (enabled.All(r => r.LastResultOk == true))
            {
                return true;
            }

            return null;
        }

        private static async Task ShowMessageAsync(
            string message,
            string caption = "BrakeDiscInspector",
            MessageBoxImage icon = MessageBoxImage.Information)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
            });
        }

        private static double ComputeYoudenThreshold(IReadOnlyCollection<double> okScores, IReadOnlyCollection<double> ngScores, double defaultValue)
        {
            if (okScores.Count == 0)
            {
                return defaultValue;
            }

            if (ngScores.Count == 0)
            {
                return okScores.Average();
            }

            var all = okScores.Select(s => (Score: s, IsOk: true))
                .Concat(ngScores.Select(s => (Score: s, IsOk: false)))
                .OrderBy(pair => pair.Score)
                .ToArray();

            int totalOk = okScores.Count;
            int totalNg = ngScores.Count;
            int okAbove = totalOk;
            int ngAbove = totalNg;

            double bestJ = double.NegativeInfinity;
            double bestThr = defaultValue;

            for (int i = 0; i < all.Length; i++)
            {
                var current = all[i];
                if (current.IsOk)
                {
                    okAbove--;
                }
                else
                {
                    ngAbove--;
                }

                double thr = current.Score;
                double tpr = totalOk > 0 ? (double)okAbove / totalOk : 0;
                double fpr = totalNg > 0 ? (double)ngAbove / totalNg : 0;
                double j = tpr - fpr;
                if (j > bestJ)
                {
                    bestJ = j;
                    bestThr = thr;
                }
            }

            return bestThr;
        }

        private sealed record RoiDatasetAnalysis(
            string DatasetPath,
            List<DatasetEntry> Entries,
            int OkCount,
            int KoCount,
            bool IsValid,
            string StatusMessage,
            List<(string Path, bool IsOk)> PreviewItems)
        {
            public static RoiDatasetAnalysis Empty(string status)
                => new(string.Empty, new List<DatasetEntry>(), 0, 0, false, status, new List<(string, bool)>());
        }

        private sealed class InspectionModelManifest
        {
            public string? manifest_version { get; set; }
            public string? role_id { get; set; }
            public string? model_key { get; set; }
            public int roi_index { get; set; }
            public string? roi_name { get; set; }
            public double? mm_per_px { get; set; }
            public DateTimeOffset? trained_at_utc { get; set; }
            public DateTimeOffset? calibrated_at_utc { get; set; }
            public FitOkResult? fit { get; set; }
            public CalibResult? calibration { get; set; }
        }

        private sealed record DatasetEntry(string Path, bool IsOk);

        private bool HasAnyEnabledInspectionRoi()
        {
            return _inspectionRois != null && _inspectionRois.Any(r => r.Enabled);
        }

        public event EventHandler? OverlayVisibilityChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void LogAlign(string message)
        {
            var payload = "[ALIGN]" + message;
            GuiLog.Info(payload);
            _trace?.Invoke(payload);
        }

        public void TraceBatch(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                _log(message);
            }
            catch
            {
                // never throw from tracing
            }
        }

        public void TraceManual(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                _log(message);
            }
            catch
            {
            }
        }

        private void LogBatchPlacement(string reason, int roiIdx, (double s, double dx, double dy) t, Rect rImg, Rect rCv)
        {
            _log?.Invoke($"[batch-ui] step={_batchStepId} file='{CurrentImagePath}' reason={reason} idx={roiIdx} " +
                        $"imgRoi=({rImg.X:0},{rImg.Y:0},{rImg.Width:0},{rImg.Height:0}) " +
                        $"t=(s={t.s:F6},dx={t.dx:0.##},dy={t.dy:0.##}) " +
                        $"cvRoi=({rCv.X:0},{rCv.Y:0},{rCv.Width:0},{rCv.Height:0}) " +
                        $"anchorReady={(_batchAnchorReadyForStep == _batchStepId)}");
        }

        private void LogManualPlacement(string reason, int roiIdx, (double s, double dx, double dy) t, Rect rImg, Rect rCv)
        {
            _log?.Invoke($"[manual-ui] file='{CurrentManualImagePath}' reason={reason} idx={roiIdx} " +
                        $"imgRoi=({rImg.X:0},{rImg.Y:0},{rImg.Width:0},{rImg.Height:0}) " +
                        $"t=(s={t.s:F6},dx={t.dx:0.##},dy={t.dy:0.##}) " +
                        $"cvRoi=({rCv.X:0},{rCv.Y:0},{rCv.Width:0},{rCv.Height:0})");
        }

        public void BeginManualInspection(string imagePath)
        {
            CurrentManualImagePath = imagePath;
            GuiLog.Info($"[manual-ui] load file='{CurrentManualImagePath}'");
            TraceManual($"[manual] open file='{System.IO.Path.GetFileName(imagePath)}'");
        }

        public Task RepositionMastersAsync(string imagePath)
        {
            EnsureLayoutOriginalSnapshot();
            return _repositionInspectionRoisAsync(imagePath, CancellationToken.None);
        }

        public async Task RepositionInspectionRoisAsync(string imagePath)
        {
            EnsureLayoutOriginalSnapshot();
            await _repositionInspectionRoisAsync(imagePath, CancellationToken.None)
                .ConfigureAwait(false);

            if (!TryBuildPlacementInput(out var placementInput, logError: false))
            {
                return;
            }

            InvokeOnUi(() =>
            {
                RepositionInspectionRois(placementInput);
                RedrawOverlays();
            });
        }

        public void TraceBatchInspectionRoisSnapshot(string label)
        {
            try
            {
                for (int idx = 1; idx <= 4; idx++)
                {
                    var roiModel = GetInspectionRoiModelByIndex(idx);
                    var roiConfig = GetInspectionConfigByIndex(idx);

                    if (roiModel == null && (roiConfig?.Enabled != true))
                    {
                        continue;
                    }

                    string enabled = (roiConfig?.Enabled ?? false) ? "true" : "false";

                    if (roiModel != null)
                    {
                        TraceBatch(
                            $"[batch-snap:{label}] roiIdx={idx} enabled={enabled} shape={roiModel.Shape} " +
                            $"CX={roiModel.CX:0.##} CY={roiModel.CY:0.##} R={roiModel.R:0.##} " +
                            $"L={roiModel.Left:0.##} T={roiModel.Top:0.##} W={roiModel.Width:0.##} H={roiModel.Height:0.##} Ang={roiModel.AngleDeg:0.##}");
                    }
                    else
                    {
                        TraceBatch($"[batch-snap:{label}] roiIdx={idx} enabled={enabled} <no-model>");
                    }
                }
            }
            catch (Exception ex)
            {
                TraceBatch($"[batch-snap:{label}] FAIL: {ex.Message}");
            }
        }

        public void BeginBatchStep(int rowIndex1Based, string imagePath)
        {
            var id = Interlocked.Increment(ref _batchStepId);
            _batchAnchorM1Ready = false;
            _batchAnchorM2Ready = false;
            _batchAnchorReadyForStep = -1;
            _batchPlacedForStep = -1;
            _batchPlacementToken = unchecked(_batchPlacementToken + 1);
            _batchPlacementTokenPlaced = -1;
            _batchDetectedM1 = null;
            _batchDetectedM2 = null;
            _batchBaselineM1 = null;
            _batchBaselineM2 = null;
            _batchXformComputed = false;
            _sharedHeatmapGuardLogged = false;
            ClearBaselines();
            CurrentRowIndex = Math.Max(1, rowIndex1Based);
            CurrentImagePath = imagePath;
            _currentBatchFile = Path.GetFileName(imagePath);
            BatchRowOk = null;
            ResetBatchTransform();
            GuiLog.Info($"[batch] step++ id={_batchStepId} file='{Path.GetFileName(imagePath) ?? string.Empty}'");
            TraceBatch($"[batch] step++ id={BatchStepId} row={CurrentRowIndex:000} file='{Path.GetFileName(imagePath) ?? string.Empty}'");

            if (_layoutOriginal != null)
            {
                SetInspectionRoiModels(
                    _layoutOriginal.Inspection1?.Clone(),
                    _layoutOriginal.Inspection2?.Clone(),
                    _layoutOriginal.Inspection3?.Clone(),
                    _layoutOriginal.Inspection4?.Clone());

                FormattableString resetMessage =
                    $"[reset] ROI1={RectToStr(BuildRoiRect(_inspection1))} ROI2={RectToStr(BuildRoiRect(_inspection2))} ROI3={RectToStr(BuildRoiRect(_inspection3))} ROI4={RectToStr(BuildRoiRect(_inspection4))}";
                GuiLog.Info(resetMessage);
            }

            EnterLayoutPersistenceLock();
        }

        public void EndBatchStep()
        {
            TraceBatch($"[batch] step-- id={BatchStepId}");
            ExitLayoutPersistenceLock();
        }

        public IDisposable BeginLayoutIo(string tag)
        {
            return new LayoutIoGuard(this, tag);
        }

        private sealed class LayoutIoGuard : IDisposable
        {
            private readonly WorkflowViewModel _vm;
            private readonly string _tag;
            private bool _disposed;

            public LayoutIoGuard(WorkflowViewModel vm, string tag)
            {
                _vm = vm;
                _tag = string.IsNullOrWhiteSpace(tag) ? "<unnamed>" : tag;
                var depth = Interlocked.Increment(ref vm._layoutIoDepth);
                vm.TraceBatch(FormattableString.Invariant($"[layout-io] begin tag={_tag} depth={depth}"));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                var depth = Interlocked.Decrement(ref _vm._layoutIoDepth);
                _vm.TraceBatch(FormattableString.Invariant($"[layout-io] end tag={_tag} depth={depth}"));
            }
        }

        private void RedrawOverlays()
        {
            OverlayVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
