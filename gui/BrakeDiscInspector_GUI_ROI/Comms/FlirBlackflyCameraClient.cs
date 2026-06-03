using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class FlirBlackflyCameraClient : ICameraClient
    {
        private const string DefaultSdkDllPath = @"C:\Program Files\Teledyne\Spinnaker\bin64\vs2015\SpinnakerNET_v140.dll";
        private readonly SemaphoreSlim _sync = new(1, 1);
        private readonly string _selector;
        private Assembly? _sdkAssembly;
        private Type? _managedSystemType;
        private Type? _managedImageProcessorType;
        private Type? _pixelFormatEnumsType;
        private Type? _colorProcessingAlgorithmType;
        private Type? _nodeMapType;
        private Type? _enumNodeType;
        private Type? _commandNodeType;
        private Type? _stringNodeType;
        private Type? _integerNodeType;
        private object? _system;
        private object? _cameraList;
        private object? _camera;
        private object? _nodeMap;
        private object? _tlDeviceNodeMap;
        private object? _processor;
        private string _deviceSerial = string.Empty;
        private string _deviceModel = string.Empty;
        private string _deviceIp = string.Empty;
        private string _deviceSubnetMask = string.Empty;
        private bool _disposed;
        private bool _isConnected;
        private bool _isAcquiring;

        public FlirBlackflyCameraClient(CameraConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _selector = config.Source?.Trim() ?? string.Empty;
        }

        public CameraConfig Config { get; }

        public bool IsConnected => !_disposed && _isConnected;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _sync.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_isConnected)
                {
                    return;
                }

                await RunSdkWorkerAsync(() =>
                {
                    ConnectCore(ct);
                    return true;
                }, ct).ConfigureAwait(false);
                _isConnected = true;
                GuiLog.Info($"[camera-flir] Connected model='{_deviceModel}' serial='{_deviceSerial}' selector='{_selector}'");
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _sync.WaitAsync().ConfigureAwait(false);
            try
            {
                DisconnectCore();
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task<CameraFrame> AcquireAsync(CancellationToken ct = default)
        {
            await _sync.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                EnsureConnected();
                return await RunSdkWorkerAsync(() => AcquireCore(ct), ct).ConfigureAwait(false);
            }
            finally
            {
                _sync.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _sync.Wait();
                try
                {
                    DisconnectCore();
                    _disposed = true;
                }
                finally
                {
                    _sync.Release();
                }
            }
            finally
            {
                _sync.Dispose();
            }
        }

        private void ConnectCore(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LoadSdk();

            _system = Activator.CreateInstance(_managedSystemType!)
                ?? throw new InvalidOperationException("Unable to create Spinnaker ManagedSystem.");
            _cameraList = Invoke(_system, "GetCameras");
            var count = Convert.ToInt32(GetProperty(_cameraList!, "Count"), CultureInfo.InvariantCulture);
            if (count <= 0)
            {
                throw new InvalidOperationException("No FLIR/Teledyne Spinnaker cameras were detected.");
            }

            _camera = ResolveCamera(_cameraList, count);
            _tlDeviceNodeMap = Invoke(_camera, "GetTLDeviceNodeMap");
            _deviceSerial = TryReadStringNode(_tlDeviceNodeMap, "DeviceSerialNumber") ?? string.Empty;
            _deviceModel = TryReadStringNode(_tlDeviceNodeMap, "DeviceModelName") ?? "FLIR Blackfly";
            _deviceIp = TryReadGigEIp(_tlDeviceNodeMap) ?? string.Empty;
            _deviceSubnetMask = TryReadGigESubnetMask(_tlDeviceNodeMap) ?? string.Empty;

            try
            {
                _nodeMap = Invoke(_camera, "Init") ?? Invoke(_camera, "GetNodeMap");
            }
            catch (Exception ex) when (IsWrongSubnetError(ex))
            {
                throw new InvalidOperationException(BuildWrongSubnetMessage(ex), ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(BuildConnectFailureMessage(ex), ex);
            }
            var streamNodeMap = TryInvoke(_camera, "GetTLStreamNodeMap");
            TrySetEnum(streamNodeMap, "StreamMode", "TeledyneGigeVision", required: false);
            TrySetEnum(streamNodeMap, "StreamBufferHandlingMode", "NewestOnly", required: false);

            TrySetEnum(_nodeMap, "PixelFormat", "Mono8", required: false);
            ConfigureSoftwareTrigger(_nodeMap!);

            _processor = Activator.CreateInstance(_managedImageProcessorType!)
                ?? throw new InvalidOperationException("Unable to create Spinnaker image processor.");
            TrySetColorProcessing("HQ_LINEAR");
        }

        private CameraFrame AcquireCore(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var timeoutMs = Math.Max(1, Config.TimeoutMs);

            var imageTimeoutMs = (ulong)timeoutMs;
            object? rawImage = null;
            object? convertedImage = null;
            var acquiredAt = DateTimeOffset.Now;
            try
            {
                Invoke(_camera!, "BeginAcquisition");
                _isAcquiring = true;

                try
                {
                    ExecuteCommand(_nodeMap!, "TriggerSoftware", TimeSpan.FromMilliseconds(timeoutMs), ct);

                    rawImage = Invoke(_camera!, "GetNextImage", imageTimeoutMs)
                        ?? throw new InvalidOperationException("Spinnaker returned no image.");
                    if (GetBoolProperty(rawImage, "IsIncomplete"))
                    {
                        var status = TryGetProperty(rawImage, "ImageStatus")?.ToString() ?? "unknown";
                        throw new InvalidOperationException($"Spinnaker image is incomplete: {status}");
                    }

                    convertedImage = ConvertToMono8(rawImage);
                    var outputPath = ResolveOutputPath(acquiredAt);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    Invoke(convertedImage, "Save", outputPath);
                    GuiLog.Info($"[camera-flir] Acquired single frame dst='{outputPath}' serial='{_deviceSerial}'");
                    return new CameraFrame(outputPath, acquiredAt, CameraProviders.FlirBlackfly);
                }
                finally
                {
                    DisposeObject(convertedImage);
                    DisposeObject(rawImage);
                    try
                    {
                        Invoke(_camera!, "EndAcquisition");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Warn($"[camera-flir] EndAcquisition failed after single frame: {ex.Message}");
                    }

                    _isAcquiring = false;
                }
            }
            catch
            {
                _isAcquiring = false;
                throw;
            }
        }

        private object ResolveCamera(object cameraList, int count)
        {
            if (!string.IsNullOrWhiteSpace(_selector))
            {
                try
                {
                    return Invoke(cameraList, "GetBySerial", _selector)
                        ?? throw new InvalidOperationException();
                }
                catch
                {
                    // Fall back to a scan so source can also match model or IP.
                }
            }

            object? first = null;
            for (var i = 0; i < count; i++)
            {
                var camera = Invoke(cameraList, "GetByIndex", (uint)i);
                if (camera == null)
                {
                    continue;
                }

                first ??= camera;
                if (string.IsNullOrWhiteSpace(_selector))
                {
                    return camera;
                }

                var tlNodeMap = TryInvoke(camera, "GetTLDeviceNodeMap");
                var serial = TryReadStringNode(tlNodeMap, "DeviceSerialNumber");
                var model = TryReadStringNode(tlNodeMap, "DeviceModelName");
                var ip = TryReadGigEIp(tlNodeMap);

                if (TextMatches(serial, _selector) ||
                    TextMatches(model, _selector) ||
                    TextMatches(ip, _selector))
                {
                    return camera;
                }
            }

            if (first != null && string.IsNullOrWhiteSpace(_selector))
            {
                return first;
            }

            throw new InvalidOperationException($"FLIR Blackfly camera '{_selector}' was not found.");
        }

        private void ConfigureSoftwareTrigger(object nodeMap)
        {
            SetEnum(nodeMap, "TriggerMode", "Off");
            SetEnum(nodeMap, "AcquisitionMode", "SingleFrame");
            SetEnum(nodeMap, "TriggerSelector", "FrameStart");
            SetEnum(nodeMap, "TriggerSource", "Software");
            SetEnum(nodeMap, "TriggerMode", "On");

            // Teledyne examples note that Blackfly GigE cameras need a short delay
            // after enabling trigger mode before the first trigger is reliable.
            Thread.Sleep(1000);
        }

        private void ResetTrigger()
        {
            try
            {
                if (_nodeMap != null)
                {
                    TrySetEnum(_nodeMap, "TriggerMode", "Off", required: false);
                }
            }
            catch (Exception ex)
            {
                GuiLog.Warn($"[camera-flir] Failed to reset trigger mode: {ex.Message}");
            }
        }

        private void DisconnectCore()
        {
            if (_camera != null)
            {
                if (_isAcquiring)
                {
                    try
                    {
                        Invoke(_camera, "EndAcquisition");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Warn($"[camera-flir] EndAcquisition failed: {ex.Message}");
                    }
                    finally
                    {
                        _isAcquiring = false;
                    }
                }

                ResetTrigger();

                try
                {
                    Invoke(_camera, "DeInit");
                }
                catch (Exception ex)
                {
                    GuiLog.Warn($"[camera-flir] DeInit failed: {ex.Message}");
                }
            }

            DisposeObject(_processor);
            DisposeObject(_camera);

            try
            {
                TryInvoke(_cameraList, "Clear");
            }
            catch
            {
                // Best effort cleanup.
            }

            DisposeObject(_cameraList);
            DisposeObject(_system);

            _processor = null;
            _nodeMap = null;
            _tlDeviceNodeMap = null;
            _camera = null;
            _cameraList = null;
            _system = null;
            _deviceSerial = string.Empty;
            _deviceModel = string.Empty;
            _deviceIp = string.Empty;
            _deviceSubnetMask = string.Empty;
            _isConnected = false;
        }

        private object ConvertToMono8(object rawImage)
        {
            var mono8 = Enum.Parse(_pixelFormatEnumsType!, "Mono8");
            return Invoke(_processor!, "Convert", rawImage, mono8)
                ?? throw new InvalidOperationException("Spinnaker failed to convert image to Mono8.");
        }

        private string ResolveOutputPath(DateTimeOffset acquiredAt)
        {
            var outputDirectory = Config.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrakeDiscInspector",
                    "captures");
            }

            var serial = string.IsNullOrWhiteSpace(_deviceSerial) ? "blackfly" : SanitizeFileComponent(_deviceSerial);
            return Path.Combine(outputDirectory, $"flir_{serial}_{acquiredAt:yyyyMMdd_HHmmss_fff}.png");
        }

        private void LoadSdk()
        {
            if (_sdkAssembly != null)
            {
                return;
            }

            var dllPath = ResolveSdkDllPath();
            var dllDir = Path.GetDirectoryName(dllPath);
            if (!string.IsNullOrWhiteSpace(dllDir))
            {
                SetDllDirectory(dllDir);
            }

            _sdkAssembly = Assembly.LoadFrom(dllPath);
            _managedSystemType = RequiredType("SpinnakerNET.ManagedSystem");
            _managedImageProcessorType = RequiredType("SpinnakerNET.ManagedImageProcessor");
            _pixelFormatEnumsType = RequiredType("SpinnakerNET.PixelFormatEnums");
            _colorProcessingAlgorithmType = RequiredType("SpinnakerNET.ColorProcessingAlgorithm");
            _nodeMapType = RequiredType("SpinnakerNET.GenApi.INodeMap");
            _enumNodeType = RequiredType("SpinnakerNET.GenApi.IEnum");
            _commandNodeType = RequiredType("SpinnakerNET.GenApi.ICommand");
            _stringNodeType = RequiredType("SpinnakerNET.GenApi.IString");
            _integerNodeType = RequiredType("SpinnakerNET.GenApi.IInteger");
        }

        private string ResolveSdkDllPath()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("BDI_SPINNAKER_NET_DLL"),
                BuildSdkPath(Environment.GetEnvironmentVariable("SPINNAKER_ROOT")),
                DefaultSdkDllPath,
                Path.Combine(AppContext.BaseDirectory, "SpinnakerNET_v140.dll")
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException(
                "SpinnakerNET_v140.dll was not found. Install Teledyne Spinnaker 4.3 or set BDI_SPINNAKER_NET_DLL.",
                DefaultSdkDllPath);
        }

        private static string? BuildSdkPath(string? root)
        {
            return string.IsNullOrWhiteSpace(root)
                ? null
                : Path.Combine(root.Trim(), "bin64", "vs2015", "SpinnakerNET_v140.dll");
        }

        private Type RequiredType(string typeName)
        {
            return _sdkAssembly!.GetType(typeName)
                ?? throw new InvalidOperationException($"Spinnaker type '{typeName}' was not found.");
        }

        private object? GetNode(object? nodeMap, string nodeName, Type nodeType)
        {
            if (nodeMap == null)
            {
                return null;
            }

            var method = _nodeMapType!
                .GetMethods()
                .First(m => m.Name == "GetNode" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(nodeType);
            return InvokeMethod(method, nodeMap, nodeName);
        }

        private void SetEnum(object nodeMap, string nodeName, string entryName)
        {
            TrySetEnum(nodeMap, nodeName, entryName, required: true);
        }

        private void TrySetEnum(object? nodeMap, string nodeName, string entryName, bool required)
        {
            var node = GetNode(nodeMap, nodeName, _enumNodeType!);
            if (node == null)
            {
                if (required)
                {
                    throw new InvalidOperationException($"Spinnaker enum node '{nodeName}' was not found.");
                }

                return;
            }

            if (!GetBoolProperty(node, "IsWritable"))
            {
                if (required)
                {
                    throw new InvalidOperationException($"Spinnaker enum node '{nodeName}' is not writable.");
                }

                return;
            }

            var entry = TryInvoke(node, "GetEntryByName", entryName);
            if (entry == null || !GetBoolProperty(entry, "IsReadable"))
            {
                if (required)
                {
                    throw new InvalidOperationException($"Spinnaker enum entry '{nodeName}.{entryName}' is not readable.");
                }

                return;
            }

            var fromString = FindMethod(node.GetType(), "FromString", new object?[] { entryName });
            if (fromString != null)
            {
                InvokeMethod(fromString, node, entryName);
                return;
            }

            var enumValueType = RequiredType("SpinnakerNET.GenApi.EnumValue");
            var enumValue = Activator.CreateInstance(enumValueType, entryName);
            SetProperty(node, "Value", enumValue);
        }

        private void ExecuteCommand(object nodeMap, string nodeName, TimeSpan timeout, CancellationToken ct)
        {
            var node = GetNode(nodeMap, nodeName, _commandNodeType!)
                ?? throw new InvalidOperationException($"Spinnaker command node '{nodeName}' was not found.");

            var deadline = DateTime.UtcNow + timeout;
            while (!GetBoolProperty(node, "IsWritable"))
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"Spinnaker command node '{nodeName}' was not writable within {timeout.TotalMilliseconds:0} ms.");
                }

                Thread.Sleep(25);
            }

            Invoke(node, "Execute");
            WaitForCommandDone(node, TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(25, timeout.TotalMilliseconds))), ct);
        }

        private static void WaitForCommandDone(object commandNode, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var done = TryInvoke(commandNode, "IsDone", true);
                if (done is bool isDone && isDone)
                {
                    return;
                }

                Thread.Sleep(10);
            }
        }

        private string? TryReadStringNode(object? nodeMap, string nodeName)
        {
            var node = GetNode(nodeMap, nodeName, _stringNodeType!);
            return node == null || !GetBoolProperty(node, "IsReadable")
                ? null
                : TryGetProperty(node, "Value")?.ToString();
        }

        private string? TryReadGigEIp(object? nodeMap)
        {
            return TryReadIpIntegerNode(
                nodeMap,
                "GevCurrentIPAddress",
                "GevDeviceIPAddress",
                "DeviceIPAddress",
                "GevIPAddress");
        }

        private string? TryReadGigESubnetMask(object? nodeMap)
        {
            return TryReadIpIntegerNode(
                nodeMap,
                "GevCurrentSubnetMask",
                "GevDeviceSubnetMask",
                "DeviceSubnetMask",
                "GevSubnetMask");
        }

        private string? TryReadIpIntegerNode(object? nodeMap, params string[] nodeNames)
        {
            foreach (var nodeName in nodeNames)
            {
                var value = TryReadIpIntegerNode(nodeMap, nodeName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private string? TryReadIpIntegerNode(object? nodeMap, string nodeName)
        {
            var node = GetNode(nodeMap, nodeName, _integerNodeType!);
            if (node == null || !GetBoolProperty(node, "IsReadable"))
            {
                return null;
            }

            var value = Convert.ToUInt32(TryGetProperty(node, "Value"), CultureInfo.InvariantCulture);
            return string.Join(
                ".",
                (value >> 24) & 0xff,
                (value >> 16) & 0xff,
                (value >> 8) & 0xff,
                value & 0xff);
        }

        private void TrySetColorProcessing(string algorithm)
        {
            try
            {
                var value = Enum.Parse(_colorProcessingAlgorithmType!, algorithm);
                Invoke(_processor!, "SetColorProcessing", value);
            }
            catch (Exception ex)
            {
                GuiLog.Warn($"[camera-flir] SetColorProcessing failed: {ex.Message}");
            }
        }

        [System.Diagnostics.DebuggerNonUserCode]
        private static async Task<T> RunSdkWorkerAsync<T>(Func<T> work, CancellationToken ct)
        {
            Exception? error = null;
            T? result = default;

            await Task.Run(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }, ct).ConfigureAwait(false);

            if (error != null)
            {
                throw error;
            }

            return result!;
        }

        private string BuildWrongSubnetMessage(Exception ex)
        {
            var network = BuildDeviceNetworkText();
            return "FLIR Blackfly is reachable by discovery but cannot be initialized because Spinnaker reports it is on a wrong subnet. " +
                   network +
                   " Check the Windows Ethernet adapter used by the camera and make sure it is in the same subnet as the camera, then reconnect. " +
                   $"Original Spinnaker message: {ex.Message}";
        }

        private string BuildConnectFailureMessage(Exception ex)
        {
            return "FLIR Blackfly connection failed. " +
                   BuildDeviceNetworkText() +
                   $" Original Spinnaker message: {ex.Message}";
        }

        private string BuildDeviceNetworkText()
        {
            return $"Camera model='{_deviceModel}', serial='{_deviceSerial}', ip='{ValueOrUnknown(_deviceIp)}', subnet='{ValueOrUnknown(_deviceSubnetMask)}', selector='{_selector}'.";
        }

        private static bool IsWrongSubnetError(Exception ex)
        {
            var message = ex.ToString();
            return message.IndexOf("wrong subnet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("-1015", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ValueOrUnknown(string value)
            => string.IsNullOrWhiteSpace(value) ? "unknown" : value;

        private void EnsureConnected()
        {
            if (!_isConnected || _camera == null || _nodeMap == null)
            {
                throw new InvalidOperationException("FLIR Blackfly camera is not connected.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FlirBlackflyCameraClient));
            }
        }

        private static object? Invoke(object target, string methodName, params object?[] args)
        {
            var method = FindMethod(target.GetType(), methodName, args)
                ?? target.GetType()
                    .GetInterfaces()
                    .Select(type => FindMethod(type, methodName, args))
                    .FirstOrDefault(methodInfo => methodInfo != null)
                ?? throw new MissingMethodException(target.GetType().FullName, methodName);

            return InvokeMethod(method, target, args);
        }

        private static object? TryInvoke(object? target, string methodName, params object?[] args)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                return Invoke(target, methodName, args);
            }
            catch
            {
                return null;
            }
        }

        [System.Diagnostics.DebuggerNonUserCode]
        private static object? InvokeMethod(MethodInfo method, object target, params object?[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
            }
        }

        private static MethodInfo? FindMethod(Type type, string methodName, object?[] args)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == methodName)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != args.Length)
                    {
                        return false;
                    }

                    for (var i = 0; i < parameters.Length; i++)
                    {
                        if (args[i] == null)
                        {
                            continue;
                        }

                        if (!parameters[i].ParameterType.IsInstanceOfType(args[i]) &&
                            !CanConvertNumeric(args[i]!, parameters[i].ParameterType))
                        {
                            return false;
                        }
                    }

                    return true;
                });
        }

        private static bool CanConvertNumeric(object value, Type targetType)
        {
            var type = value.GetType();
            return type == typeof(uint) && targetType == typeof(uint) ||
                   type == typeof(ulong) && targetType == typeof(ulong) ||
                   type == typeof(int) && targetType == typeof(int);
        }

        private static object? GetProperty(object target, string propertyName)
        {
            return TryGetProperty(target, propertyName)
                ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        }

        private static object? TryGetProperty(object? target, string propertyName)
        {
            if (target == null)
            {
                return null;
            }

            var property = FindProperty(target.GetType(), propertyName)
                ?? target.GetType()
                    .GetInterfaces()
                    .Select(type => FindProperty(type, propertyName))
                    .FirstOrDefault(propertyInfo => propertyInfo != null);

            return property?.GetValue(target);
        }

        private static void SetProperty(object target, string propertyName, object? value)
        {
            var property = FindProperty(target.GetType(), propertyName)
                ?? target.GetType()
                    .GetInterfaces()
                    .Select(type => FindProperty(type, propertyName))
                    .FirstOrDefault(propertyInfo => propertyInfo != null)
                ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
            property.SetValue(target, value);
        }

        private static PropertyInfo? FindProperty(Type type, string propertyName)
            => type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        private static bool GetBoolProperty(object target, string propertyName)
        {
            var value = TryGetProperty(target, propertyName);
            return value is bool b && b;
        }

        private static bool TextMatches(string? value, string selector)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Trim().Equals(selector.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string SanitizeFileComponent(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        private static void DisposeObject(object? target)
        {
            switch (target)
            {
                case null:
                    return;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                default:
                    TryInvoke(target, "Dispose");
                    break;
            }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string? lpPathName);
    }
}
