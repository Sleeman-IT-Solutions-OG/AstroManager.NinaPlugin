using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using Shared.Model.DTO.Client;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Background service that sends periodic heartbeats to the AstroManager API
    /// and polls for remote commands from the web app
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class HeartbeatService : IDisposable
    {
        // Instance ID for debugging multiple instance issues
        private readonly Guid _instanceId = Guid.NewGuid();
        
        private readonly AstroManagerSettings _settings;
        private readonly AstroManagerApiClient _apiClient;
        private readonly ScheduledTargetStore _targetStore;
        private CancellationTokenSource? _cts;
        private Task? _heartbeatTask;
        private Task? _equipmentPollTask;
        private Task? _commandPollTask;
        private bool _isRunning;
        private bool _isExecutingCommand;
        private bool _isShuttingDown;
        private DateTime? _lastHeartbeat;
        private DateTime? _lastRefresh;
        private DateTime _lastStatusUpdate = DateTime.MinValue;
        private string _status = "Idle";
        
        // Auto-reconnect tracking
        private int _consecutiveFailures = 0;
        private const int MAX_FAILURES_BEFORE_RECONNECT = 3;
        private DateTime _lastReconnectAttempt = DateTime.MinValue;
        private const int RECONNECT_INTERVAL_SECONDS = 60;
        
        // Current session state for status updates - use STATIC lock and fields for thread safety
        // CRITICAL: Must be static because MEF may create multiple HeartbeatService instances
        // (one for plugin, one for scheduler) if NINA uses separate composition containers
        private static readonly object _stateLock = new object();
        private static string? _currentOperation;
        private static string? _currentTargetName;
        private static string? _currentFilter;
        private static int _currentGoalCompleted;
        private static int _currentGoalTotal;
        private static string _sessionStatus = "Idle";
        
        // Current slot IDs for AM to display compact target view - STATIC for same reason
        private static Guid? _currentTargetId;
        private static Guid? _currentImagingGoalId;
        private static Guid? _currentPanelId;
        private static int? _currentExposureTimeSeconds;
        private static string? _currentPanelName;
        private static string? _schedulerConfigurationName;
        private static bool _isUsingDefaultConfig;
        private static string? _sequenceFilesFolder;
        private static List<SequenceFileEntryDto>? _availableSequenceFiles;
        private static SequenceTreeDto? _sequenceTreeSnapshot;
        
        // Equipment connection status
        private bool? _isCameraConnected;
        private bool? _isTelescopeConnected;
        private bool? _isFocuserConnected;
        private bool? _isFilterWheelConnected;
        private bool? _isGuiderConnected;
        private bool? _isRotatorConnected;
        private bool? _isDomeConnected;
        private bool? _isWeatherConnected;
        private bool? _isFlatPanelConnected;
        private bool? _isSafetyMonitorConnected;
        private bool? _isSafe;
        
        // Equipment detailed status
        private double? _mountRightAscension;
        private double? _mountDeclination;
        private double? _mountAltitude;
        private double? _mountAzimuth;
        private string? _mountSideOfPier;
        private string? _previousSideOfPier;
        private string? _previousSelectedFilter;
        private bool? _previousIsGuiding;
        private bool? _previousIsTracking;
        private bool? _previousIsSlewing;
        private bool? _previousIsExposing;
        private int? _previousFocuserPosition;
        private bool _isMeridianFlipping;
        private DateTime? _meridianFlipStartedUtc;
        private string? _mountTrackingRate;
        private int? _focuserPosition;
        private double? _focuserTemperature;
        private string? _selectedFilter;
        private List<string>? _filterWheelFilters;
        private double? _rotatorAngle;
        private bool? _rotatorReverse;
        private bool? _rotatorCanReverse;
        private bool? _flatPanelLightOn;
        private int? _flatPanelBrightness;
        private string? _flatPanelCoverState;
        private bool? _flatPanelSupportsOpenClose;
        private double? _guidingRaRms;
        private double? _guidingDecRms;
        private bool? _isGuiding;
        private bool? _isCalibrating;
        private bool? _isTracking;
        private bool? _isParked;
        private bool? _isSlewing;
        private bool? _isFocuserMoving;
        private bool? _isExposing;
        private double? _exposureDurationSeconds;
        private double? _exposureElapsedSeconds;
        private double? _cameraTemperature;
        private double? _cameraTargetTemperature;
        private double? _coolerPower;
        private bool? _isCoolerOn;
        private int? _cameraBinning;
        
        // Last autofocus report (history is maintained by server, not plugin)
        private AutofocusReportDto? _currentAutofocusReport;
        private AutofocusReportDto? _lastAutofocusReport;
        private DateTime _lastAutofocusStreamUpdateUtc = DateTime.MinValue;
        
        // Last plate solve report
        private PlateSolveReportDto? _lastPlateSolveReport;
        
        // Image history (recent captures from NINA)
        private List<ImageHistoryItemDto>? _imageHistory;
        private static double? _lastCaptureHfr;
        private static int? _lastCaptureStarCount;
        private static DateTime? _lastCaptureAtUtc;
        
        // Sequence status - STATIC for cross-instance sharing
        private static bool? _isSequenceRunning;
        private static string? _sequenceName;
        private static string? _loadedSequenceName;
        
        // Weather data
        private double? _weatherTemperature;
        private double? _weatherHumidity;
        private double? _weatherDewPoint;
        private double? _weatherPressure;
        private double? _weatherCloudCover;
        private double? _weatherRainRate;
        private double? _weatherWindSpeed;
        private double? _weatherWindDirection;
        private double? _weatherWindGust;
        private double? _weatherSkyQuality;
        private double? _weatherSkyTemperature;
        private double? _weatherStarFWHM;
        
        // WebSocket for real-time commands (custom implementation, no SignalR dependency)
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _wsTokenSource;
        private bool _wsConnected = false;

        public event EventHandler<HeartbeatStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<RemoteCommandReceivedEventArgs>? CommandReceived;
        public event EventHandler? RefreshRequested;
        public event EventHandler? BeforeStatusUpdate;
        public event EventHandler<SchedulerModeChangedEventArgs>? SchedulerModeChanged;
        
        // Track current scheduler mode
        private SchedulerMode _currentSchedulerMode = SchedulerMode.Auto;
        private bool _schedulerModeUpdateInProgress = false;

        public bool IsRunning => _isRunning;
        public DateTime? LastHeartbeat => _lastHeartbeat;
        public string Status => _status;

        [ImportingConstructor]
        public HeartbeatService(AstroManagerSettings settings, AstroManagerApiClient apiClient, ScheduledTargetStore targetStore)
        {
            _settings = settings;
            _apiClient = apiClient;
            _targetStore = targetStore;
        }

        public void Start()
        {
            if (_isRunning) return;
            if (string.IsNullOrEmpty(_settings.LicenseKey))
            {
                Logger.Warning("HeartbeatService: Cannot start - no license key configured");
                return;
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));
            _equipmentPollTask = Task.Run(() => EquipmentPollLoop(_cts.Token));
            _commandPollTask = Task.Run(() => CommandPollLoop(_cts.Token));
            
            // Connect WebSocket for real-time commands (if enabled)
            if (_settings.EnableRealTimeConnection)
            {
                _ = ConnectWebSocketAsync();
            }
            
            UpdateStatus("Running");
            Logger.Info($"HeartbeatService: Started - Heartbeat interval: {_settings.HeartbeatIntervalSeconds}s, Equipment poll: 5s, Command poll: 3s");
        }

        /// <summary>
        /// Mark the service as shutting down to prevent regular status updates
        /// Call this early in the shutdown sequence before Stop()
        /// </summary>
        public void MarkShuttingDown()
        {
            _isShuttingDown = true;
            Logger.Info("HeartbeatService: Marked as shutting down");
        }
        
        private bool _offlineNotificationSent = false;
        
        public void Stop()
        {
            if (!_isRunning && !_isShuttingDown) return;
            if (_offlineNotificationSent) return; // Already sent offline notification

            _isShuttingDown = true; // Prevent any more regular status updates
            _cts?.Cancel();
            _isRunning = false;
            
            // Send offline notification to WebAPI before disconnecting
            try
            {
                Logger.Info("HeartbeatService: Sending offline notification...");
                _offlineNotificationSent = true;
                // Use 5 second timeout - notification typically takes ~3 seconds
                var task = SendOfflineNotificationAsync();
                task.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Failed to send offline notification: {ex.Message}");
            }
            
            // Disconnect WebSocket
            _ = DisconnectWebSocketAsync();
            
            UpdateStatus("Stopped");
            Logger.Info("HeartbeatService: Stopped");
        }
        
        /// <summary>
        /// Send offline notification to WebAPI when NINA is closing
        /// </summary>
        private async Task SendOfflineNotificationAsync()
        {
            try
            {
                var statusDto = new UpdateSessionStatusDto
                {
                    MachineName = Environment.MachineName,
                    Status = "Offline",
                    CurrentOperation = null,
                    CurrentTargetName = null,
                    CurrentFilter = null,
                    CurrentGoalCompleted = 0,
                    CurrentGoalTotal = 0,
                    // Mark all equipment as disconnected
                    IsCameraConnected = false,
                    IsTelescopeConnected = false,
                    IsFocuserConnected = false,
                    IsFilterWheelConnected = false,
                    IsGuiderConnected = false,
                    IsRotatorConnected = false,
                    IsDomeConnected = false,
                    IsWeatherConnected = false
                };
                
                await _apiClient.UpdateSessionStatusAsync(statusDto);
                Logger.Info("HeartbeatService: Offline notification sent successfully");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Failed to send offline status: {ex.Message}");
            }
        }

        private async Task HeartbeatLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SendHeartbeatAsync();
                    
                    // Wait for the configured interval (minimum 60 seconds)
                    var intervalSeconds = Math.Max(60, _settings.HeartbeatIntervalSeconds);
                    await Task.Delay(intervalSeconds * 1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"HeartbeatService: Error in heartbeat loop: {ex.Message}");
                    UpdateStatus($"Error: {ex.Message}");
                    
                    // Wait a bit before retrying on error
                    try
                    {
                        await Task.Delay(10000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        
        private async Task EquipmentPollLoop(CancellationToken token)
        {
            // Fast polling loop to detect equipment connection changes quickly
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Trigger equipment status update - this will call SetEquipmentStatus
                    // which now detects changes and sends immediate updates
                    BeforeStatusUpdate?.Invoke(this, EventArgs.Empty);
                    
                    // Poll every 5 seconds for responsive equipment status
                    await Task.Delay(5000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"HeartbeatService: Equipment poll error: {ex.Message}");
                    try
                    {
                        await Task.Delay(5000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        
        private async Task CommandPollLoop(CancellationToken token)
        {
            // Fast command polling loop - polls every 3 seconds for responsive command execution
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Only poll if not currently executing a command AND WebSocket is not connected
                    // When WebSocket is connected, commands come via real-time push - no need to poll
                    if (!_isExecutingCommand && !IsSignalRConnected)
                    {
                        await PollRemoteCommandsAsync();
                    }
                    
                    // Poll for scheduler mode changes
                    await PollSchedulerModeAsync();
                    
                    // Poll every 3 seconds for fast command response
                    await Task.Delay(3000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"HeartbeatService: Command poll error: {ex.Message}");
                    try
                    {
                        await Task.Delay(3000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task SendHeartbeatAsync()
        {
            // Check for shutdown before doing anything
            if (_isShuttingDown) return;
            
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished))
                {
                    Logger.Info("HeartbeatService: Detected WPF shutdown, triggering offline notification");
                    _isShuttingDown = true;
                    await SendOfflineNotificationAsync();
                    return;
                }
            }
            catch { }
            
            var success = await _apiClient.SendHeartbeatAsync();
            
            if (success)
            {
                _lastHeartbeat = DateTime.UtcNow;
                _consecutiveFailures = 0; // Reset failure counter on success
                UpdateStatus("Connected");
                Logger.Debug("HeartbeatService: Heartbeat sent successfully");
                
                // Send session status update with current state
                await SendSessionStatusAsync();
                
                // Check if periodic refresh is needed
                CheckPeriodicRefresh();
            }
            else
            {
                _consecutiveFailures++;
                UpdateStatus($"Heartbeat failed ({_consecutiveFailures})");
                Logger.Warning($"HeartbeatService: Heartbeat failed (attempt {_consecutiveFailures})");
                
                // Attempt auto-reconnect after consecutive failures
                await TryAutoReconnectAsync();
            }
        }
        
        /// <summary>
        /// Attempt to auto-reconnect after consecutive heartbeat failures
        /// </summary>
        private async Task TryAutoReconnectAsync()
        {
            if (_isShuttingDown) return;
            
            // Only attempt reconnect after MAX_FAILURES_BEFORE_RECONNECT consecutive failures
            if (_consecutiveFailures < MAX_FAILURES_BEFORE_RECONNECT) return;
            
            // Throttle reconnect attempts to once per minute
            var now = DateTime.UtcNow;
            if ((now - _lastReconnectAttempt).TotalSeconds < RECONNECT_INTERVAL_SECONDS) return;
            
            _lastReconnectAttempt = now;
            Logger.Info($"HeartbeatService: Attempting auto-reconnect after {_consecutiveFailures} failures...");
            
            try
            {
                // Try to re-authenticate by getting a new token
                var token = await _apiClient.GetJwtTokenAsync();
                if (!string.IsNullOrEmpty(token))
                {
                    Logger.Info("HeartbeatService: Auto-reconnect successful - re-authenticated");
                    _consecutiveFailures = 0;
                    UpdateStatus("Reconnected");
                    
                    // Reconnect WebSocket if enabled
                    if (_settings.EnableRealTimeConnection)
                    {
                        _ = ConnectWebSocketAsync();
                    }
                }
                else
                {
                    Logger.Warning("HeartbeatService: Auto-reconnect failed - authentication failed");
                    UpdateStatus("Reconnect failed");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Auto-reconnect error: {ex.Message}");
                UpdateStatus("Reconnect error");
            }
        }
        
        private async Task SendSessionStatusAsync()
        {
            // Check if WPF is shutting down
            bool isShutdown = _isShuttingDown;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished))
                {
                    isShutdown = true;
                    _isShuttingDown = true;
                }
            }
            catch { }
            
            try
            {
                // Allow plugin to update equipment status before sending (skip during shutdown)
                if (!isShutdown)
                {
                    BeforeStatusUpdate?.Invoke(this, EventArgs.Empty);
                }
                
                // Capture state under lock for thread safety
                string statusToSend;
                string? targetNameToSend;
                string? currentOp;
                string? currentFilter;
                int goalCompleted;
                int goalTotal;
                Guid? targetId;
                Guid? goalId;
                Guid? panelId;
                string? panelName;
                int? exposureTime;
                string? configName;
                string sessionStatus;
                string? sequenceFilesFolder;
                List<SequenceFileEntryDto>? availableSequenceFiles;
                SequenceTreeDto? sequenceTree;
                
                lock (_stateLock)
                {
                    currentOp = _currentOperation;
                    currentFilter = _currentFilter;
                    goalCompleted = _currentGoalCompleted;
                    goalTotal = _currentGoalTotal;
                    targetId = _currentTargetId;
                    goalId = _currentImagingGoalId;
                    panelId = _currentPanelId;
                    panelName = _currentPanelName;
                    exposureTime = _currentExposureTimeSeconds;
                    configName = _schedulerConfigurationName;
                    sessionStatus = _sessionStatus;
                    sequenceFilesFolder = _sequenceFilesFolder;
                    availableSequenceFiles = _availableSequenceFiles;
                    sequenceTree = _sequenceTreeSnapshot;
                    // Send target name if available - don't require targetId to be set
                    // This prevents "None" showing when target name exists but targetId is temporarily null
                    targetNameToSend = _currentTargetName;
                }
                
                // Determine status to send:
                // 1. If shutting down -> "Offline"
                // 2. If AM scheduler is actively imaging (targetId is set) -> use sessionStatus
                // 3. If NINA sequence is running but AM not imaging -> "SequenceRunning"
                // 4. If there's an active operation (e.g., manual AF from RC) -> use sessionStatus
                // 5. Otherwise -> "Idle"
                if (isShutdown)
                {
                    statusToSend = "Offline";
                }
                else if (targetId.HasValue)
                {
                    // AM scheduler is actively imaging a target
                    statusToSend = sessionStatus;
                }
                else if (_isSequenceRunning == true)
                {
                    // NINA sequence is running but AM scheduler hasn't started yet
                    // Keep the last known session status if we have an operation set
                    statusToSend = !string.IsNullOrEmpty(currentOp) ? sessionStatus : "SequenceRunning";
                }
                else if (!string.IsNullOrEmpty(currentOp))
                {
                    // Manual operation in progress (e.g., AF triggered from Remote Control)
                    statusToSend = sessionStatus;
                }
                else
                {
                    statusToSend = "Idle";
                }
                
                var statusDto = new UpdateSessionStatusDto
                {
                    MachineName = Environment.MachineName,
                    Status = statusToSend,
                    CurrentOperation = currentOp,
                    CurrentTargetName = targetNameToSend,
                    CurrentFilter = currentFilter,
                    CurrentGoalCompleted = goalCompleted,
                    CurrentGoalTotal = goalTotal,
                    // Current slot IDs for compact target view
                    CurrentTargetId = targetId,
                    CurrentImagingGoalId = goalId,
                    CurrentPanelId = panelId,
                    CurrentPanelName = panelName,
                    CurrentExposureTimeSeconds = exposureTime,
                    SchedulerConfigurationName = configName,
                    IsUsingDefaultConfig = _isUsingDefaultConfig,
                    // Equipment connection status
                    IsCameraConnected = _isCameraConnected,
                    IsTelescopeConnected = _isTelescopeConnected,
                    IsFocuserConnected = _isFocuserConnected,
                    IsFilterWheelConnected = _isFilterWheelConnected,
                    IsGuiderConnected = _isGuiderConnected,
                    IsRotatorConnected = _isRotatorConnected,
                    IsDomeConnected = _isDomeConnected,
                    IsWeatherConnected = _isWeatherConnected,
                    IsFlatPanelConnected = _isFlatPanelConnected,
                    IsSafetyMonitorConnected = _isSafetyMonitorConnected,
                    IsSafe = _isSafe,
                    // Equipment detailed status
                    MountRightAscension = _mountRightAscension,
                    MountDeclination = _mountDeclination,
                    MountAltitude = _mountAltitude,
                    MountAzimuth = _mountAzimuth,
                    MountSideOfPier = _mountSideOfPier,
                    MountTrackingRate = _mountTrackingRate,
                    IsMeridianFlipping = _isMeridianFlipping,
                    MeridianFlipStartedUtc = _meridianFlipStartedUtc,
                    IsTracking = _isTracking,
                    IsParked = _isParked,
                    IsSlewing = _isSlewing,
                    FocuserPosition = _focuserPosition,
                    FocuserTemperature = _focuserTemperature,
                    IsFocuserMoving = _isFocuserMoving,
                    SelectedFilter = _selectedFilter,
                    FilterWheelFilters = _filterWheelFilters,
                    RotatorAngle = _rotatorAngle,
                    RotatorReverse = _rotatorReverse,
                    RotatorCanReverse = _rotatorCanReverse,
                    FlatPanelLightOn = _flatPanelLightOn,
                    FlatPanelBrightness = _flatPanelBrightness,
                    FlatPanelCoverState = _flatPanelCoverState,
                    FlatPanelSupportsOpenClose = _flatPanelSupportsOpenClose,
                    GuidingRaRms = _guidingRaRms,
                    GuidingDecRms = _guidingDecRms,
                    IsGuiding = _isGuiding,
                    IsCalibrating = _isCalibrating,
                    CameraTemperature = _cameraTemperature,
                    CameraTargetTemperature = _cameraTargetTemperature,
                    CoolerPower = _coolerPower,
                    IsCoolerOn = _isCoolerOn,
                    CameraBinning = _cameraBinning,
                    IsExposing = _isExposing,
                    ExposureDurationSeconds = _exposureDurationSeconds,
                    ExposureElapsedSeconds = _exposureElapsedSeconds,
                    CurrentAutofocusReport = _currentAutofocusReport,
                    LastAutofocusReport = _lastAutofocusReport,
                    AutofocusHistory = null, // Server maintains history, not plugin
                    LastPlateSolveReport = _lastPlateSolveReport,
                    // Image history (recent captures)
                    ImageHistory = _imageHistory,
                    // Sequence status
                    IsSequenceRunning = _isSequenceRunning,
                    SequenceName = _sequenceName,
                    LoadedSequenceName = _loadedSequenceName,
                    SequenceFilesFolder = sequenceFilesFolder,
                    AvailableSequenceFiles = availableSequenceFiles,
                    SequenceTree = sequenceTree,
                    // Weather data
                    WeatherTemperature = _weatherTemperature,
                    WeatherHumidity = _weatherHumidity,
                    WeatherDewPoint = _weatherDewPoint,
                    WeatherPressure = _weatherPressure,
                    WeatherCloudCover = _weatherCloudCover,
                    WeatherRainRate = _weatherRainRate,
                    WeatherWindSpeed = _weatherWindSpeed,
                    WeatherWindDirection = _weatherWindDirection,
                    WeatherSkyQuality = _weatherSkyQuality,
                    WeatherSkyTemperature = _weatherSkyTemperature,
                    WeatherStarFWHM = _weatherStarFWHM
                };
                
                Logger.Debug($"HeartbeatService: Sending session status - Operation={_currentOperation ?? "null"}, Status={statusToSend}, Target={targetNameToSend ?? "null"}");
                _lastStatusUpdate = DateTime.UtcNow; // Track when we sent status for throttling
                var result = await _apiClient.UpdateSessionStatusAsync(statusDto);
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Failed to send session status: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update the current session state (call this from the plugin when state changes)
        /// </summary>
        public void SetCurrentState(string operation, string? targetName = null, string? filter = null, 
            int goalCompleted = 0, int goalTotal = 0, string status = "Imaging",
            Guid? targetId = null, Guid? imagingGoalId = null, Guid? panelId = null,
            string? panelName = null, int? exposureTimeSeconds = null)
        {
            lock (_stateLock)
            {
                _currentOperation = operation;
                _currentTargetName = targetName;
                _currentFilter = filter;
                _currentGoalCompleted = goalCompleted;
                _currentGoalTotal = goalTotal;
                _sessionStatus = status;
                _currentTargetId = targetId;
                _currentImagingGoalId = imagingGoalId;
                _currentPanelId = panelId;
                _currentPanelName = panelName;
                _currentExposureTimeSeconds = exposureTimeSeconds;
            }
        }
        
        /// <summary>
        /// Update only the operation/status without clearing target context
        /// Use this for operations like autofocus/plate solve that run within an imaging session
        /// </summary>
        public void SetOperationStatus(string operation, string status = "Imaging")
        {
            lock (_stateLock)
            {
                _currentOperation = operation;
                _sessionStatus = status;
                // Preserve all target-related fields: _currentTargetId, _currentTargetName, _currentFilter, etc.
            }
        }
        
        /// <summary>
        /// Clear the current state (call when imaging stops)
        /// </summary>
        public void ClearCurrentState()
        {
            lock (_stateLock)
            {
                _currentOperation = null;
                _currentTargetName = null;
                _currentFilter = null;
                _currentGoalCompleted = 0;
                _currentGoalTotal = 0;
                _sessionStatus = "Idle";
                _currentTargetId = null;
                _currentImagingGoalId = null;
                _currentPanelId = null;
                _currentPanelName = null;
                _currentExposureTimeSeconds = null;
            }
        }
        
        /// <summary>
        /// Check if the scheduler is actively imaging (has a target and is in Imaging status)
        /// This is used as a fallback check when SharedSchedulerState might be out of sync
        /// </summary>
        public bool IsActivelyImaging
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentTargetId.HasValue && _sessionStatus == "Imaging";
                }
            }
        }
        
        /// <summary>
        /// Set the sequence running status
        /// </summary>
        /// <param name="isRunning">Whether sequence is currently running</param>
        /// <param name="runningSequenceName">Name of running sequence (null if not running)</param>
        /// <param name="loadedSequenceName">Name of loaded sequence (even when not running)</param>
        public void SetSequenceStatus(bool isRunning, string? runningSequenceName = null, string? loadedSequenceName = null)
        {
            _isSequenceRunning = isRunning;
            _sequenceName = runningSequenceName;
            // Always track the loaded sequence name if provided
            if (!string.IsNullOrEmpty(loadedSequenceName))
            {
                _loadedSequenceName = loadedSequenceName;
            }
            Logger.Debug($"HeartbeatService: Sequence status - Running={isRunning}, RunningName={runningSequenceName ?? "null"}, LoadedName={_loadedSequenceName ?? "null"}");
        }

        public void SetSequenceFilesSnapshot(string? sequenceFolder, List<SequenceFileEntryDto>? files)
        {
            lock (_stateLock)
            {
                _sequenceFilesFolder = sequenceFolder;
                _availableSequenceFiles = files;
            }
        }

        public void SetSequenceTreeSnapshot(SequenceTreeDto? tree)
        {
            lock (_stateLock)
            {
                _sequenceTreeSnapshot = tree;
            }
        }
        
        /// <summary>
        /// Set the scheduler configuration name (call when scheduler starts or config changes)
        /// </summary>
        public void SetSchedulerConfigurationName(string? configName, bool isUsingDefault = false)
        {
            _schedulerConfigurationName = configName;
            _isUsingDefaultConfig = isUsingDefault;
            Logger.Debug($"HeartbeatService: Scheduler configuration set - {configName ?? "null"}, IsUsingDefault={isUsingDefault}");
        }
        
        /// <summary>
        /// Poll for scheduler mode changes from the server
        /// </summary>
        private async Task PollSchedulerModeAsync()
        {
            // Skip polling if a user-initiated update is in progress
            if (_schedulerModeUpdateInProgress)
            {
                return;
            }
            
            try
            {
                var serverMode = await _apiClient.GetSchedulerModeAsync();
                
                // Double-check the flag after async call
                if (_schedulerModeUpdateInProgress)
                {
                    return;
                }
                
                if (serverMode != _currentSchedulerMode)
                {
                    var oldMode = _currentSchedulerMode;
                    _currentSchedulerMode = serverMode;
                    
                    Logger.Info($"HeartbeatService: Scheduler mode changed from {oldMode} to {serverMode}");
                    
                    // Notify listeners on UI thread
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        SchedulerModeChanged?.Invoke(this, new SchedulerModeChangedEventArgs(serverMode, oldMode));
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HeartbeatService: Failed to poll scheduler mode: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set the current scheduler mode (used during initial sync)
        /// </summary>
        public void SetSchedulerMode(SchedulerMode mode)
        {
            _currentSchedulerMode = mode;
        }
        
        /// <summary>
        /// Mark that a user-initiated scheduler mode update is starting (prevents poll from overriding)
        /// </summary>
        public void BeginSchedulerModeUpdate()
        {
            _schedulerModeUpdateInProgress = true;
        }
        
        /// <summary>
        /// Mark that a user-initiated scheduler mode update has completed
        /// </summary>
        public void EndSchedulerModeUpdate()
        {
            _schedulerModeUpdateInProgress = false;
        }
        
        /// <summary>
        /// Set the last autofocus report (call when AF completes)
        /// Server will append to history when it receives this report
        /// </summary>
        public void SetAutofocusReport(AutofocusReportDto report)
        {
            _lastAutofocusReport = report;
            Logger.Info($"HeartbeatService: AF report set - Position={report.FinalPosition}, HFR={report.FinalHfr:F2}, Points={report.DataPoints.Count}");
        }

        public void StartCurrentAutofocusReport(double temperature, string? filter)
        {
            _currentAutofocusReport = new AutofocusReportDto
            {
                CompletedAt = DateTime.UtcNow,
                Success = true,
                FinalPosition = 0,
                FinalHfr = 0,
                Temperature = temperature,
                Filter = filter,
                FittingMethod = null,
                FailureReason = null,
                DataPoints = new List<AutofocusDataPointDto>()
            };
            _lastAutofocusStreamUpdateUtc = DateTime.MinValue;
        }

        public bool AddCurrentAutofocusPoint(AutofocusDataPointDto point, int? currentFinalPosition = null, double? currentFinalHfr = null)
        {
            if (_currentAutofocusReport == null)
            {
                _currentAutofocusReport = new AutofocusReportDto
                {
                    CompletedAt = DateTime.UtcNow,
                    Success = true,
                    FinalPosition = 0,
                    FinalHfr = 0,
                    Temperature = 0,
                    DataPoints = new List<AutofocusDataPointDto>()
                };
            }

            _currentAutofocusReport.DataPoints.Add(point);
            if (currentFinalPosition.HasValue) _currentAutofocusReport.FinalPosition = currentFinalPosition.Value;
            if (currentFinalHfr.HasValue) _currentAutofocusReport.FinalHfr = currentFinalHfr.Value;

            var now = DateTime.UtcNow;
            if ((now - _lastAutofocusStreamUpdateUtc).TotalMilliseconds < 750)
                return false;
            _lastAutofocusStreamUpdateUtc = now;
            return true;
        }

        public void ClearCurrentAutofocusReport()
        {
            _currentAutofocusReport = null;
        }
        
        /// <summary>
        /// Set the last plate solve report (call when plate solve completes)
        /// </summary>
        public void SetPlateSolveReport(PlateSolveReportDto report)
        {
            _lastPlateSolveReport = report;
            Logger.Debug($"HeartbeatService: PlateSolve report set - " +
                $"Success={report.Success}, " +
                $"RA={report.RaFormatted}, Dec={report.DecFormatted}, " +
                $"Rotation={report.Rotation?.ToString("F2") ?? "N/A"}°, " +
                $"PixelScale={report.PixelScale?.ToString("F2") ?? "N/A"}\"/px, " +
                $"Separation={report.SeparationArcsec?.ToString("F1") ?? "N/A"}\" " +
                $"(RA:{report.RaSeparationArcsec?.ToString("F1") ?? "?"}\", Dec:{report.DecSeparationArcsec?.ToString("F1") ?? "?"}\"), " +
                $"Synced={report.WasSynced}, " +
                $"Duration={report.SolveDurationSeconds?.ToString("F1") ?? "N/A"}s");
        }
        
        /// <summary>
        /// Set image history (recent captures from NINA)
        /// </summary>
        public void SetImageHistory(List<ImageHistoryItemDto>? history)
        {
            _imageHistory = history;
            Logger.Debug($"HeartbeatService: Image history updated - {history?.Count ?? 0} images");
        }

        public void SetLastCaptureMetrics(double? hfr, int? starCount, DateTime capturedAtUtc)
        {
            lock (_stateLock)
            {
                _lastCaptureHfr = hfr;
                _lastCaptureStarCount = starCount;
                _lastCaptureAtUtc = capturedAtUtc;
            }

            Logger.Debug($"HeartbeatService: Last capture metrics updated - HFR={hfr?.ToString("F2") ?? "null"}, Stars={starCount?.ToString() ?? "null"}, CapturedAtUtc={capturedAtUtc:O}");
        }

        public bool TryGetLastCaptureMetrics(out double? hfr, out int? starCount)
        {
            return TryGetLastCaptureMetrics(out hfr, out starCount, out _);
        }

        public bool TryGetLastCaptureMetrics(out double? hfr, out int? starCount, out DateTime? capturedAtUtc)
        {
            hfr = null;
            starCount = null;
            capturedAtUtc = null;

            lock (_stateLock)
            {
                if (_lastCaptureAtUtc.HasValue)
                {
                    hfr = _lastCaptureHfr;
                    starCount = _lastCaptureStarCount;
                    capturedAtUtc = _lastCaptureAtUtc;
                    return hfr.HasValue || starCount.HasValue;
                }
            }

            var last = _imageHistory?
                .OrderByDescending(x => x.CapturedAt)
                .FirstOrDefault();

            if (last == null)
            {
                return false;
            }

            hfr = last.HFR;
            starCount = last.DetectedStars;
            capturedAtUtc = last.CapturedAt;
            return hfr.HasValue || starCount.HasValue;
        }
        
        /// <summary>
        /// Set sequence status (running state and sequence name)
        /// </summary>
        public void SetEquipmentStatus(
            bool? cameraConnected = null,
            bool? telescopeConnected = null,
            bool? focuserConnected = null,
            bool? filterWheelConnected = null,
            bool? guiderConnected = null,
            bool? rotatorConnected = null,
            bool? domeConnected = null,
            bool? weatherConnected = null,
            bool? flatPanelConnected = null,
            bool? safetyMonitorConnected = null,
            bool? isSafe = null)
        {
            // Check if any equipment connection status changed
            bool changed = _isCameraConnected != cameraConnected ||
                          _isTelescopeConnected != telescopeConnected ||
                          _isFocuserConnected != focuserConnected ||
                          _isFilterWheelConnected != filterWheelConnected ||
                          _isGuiderConnected != guiderConnected ||
                          _isRotatorConnected != rotatorConnected ||
                          _isDomeConnected != domeConnected ||
                          _isWeatherConnected != weatherConnected ||
                          _isFlatPanelConnected != flatPanelConnected ||
                          _isSafetyMonitorConnected != safetyMonitorConnected ||
                          _isSafe != isSafe;
            
            _isCameraConnected = cameraConnected;
            _isTelescopeConnected = telescopeConnected;
            _isFocuserConnected = focuserConnected;
            _isFilterWheelConnected = filterWheelConnected;
            _isGuiderConnected = guiderConnected;
            _isRotatorConnected = rotatorConnected;
            _isDomeConnected = domeConnected;
            _isWeatherConnected = weatherConnected;
            _isFlatPanelConnected = flatPanelConnected;
            _isSafetyMonitorConnected = safetyMonitorConnected;
            _isSafe = isSafe;
            
            // Trigger immediate status update if equipment CONNECTION changed (but not during shutdown)
            // Reduced throttle to 5 seconds for more responsive equipment status updates
            if (changed && _isRunning && !_isShuttingDown)
            {
                var minIntervalSeconds = 5; // Fast updates for connection changes
                var timeSinceLastUpdate = (DateTime.UtcNow - _lastStatusUpdate).TotalSeconds;
                if (timeSinceLastUpdate >= minIntervalSeconds)
                {
                    Logger.Info($"HeartbeatService: Equipment connection changed, sending immediate update");
                    _ = SendSessionStatusAsync();
                }
            }
        }
        
        /// <summary>
        /// Set meridian flip status (called when NINA explicitly starts/ends a flip)
        /// </summary>
        public void SetMeridianFlipStatus(bool isFlipping)
        {
            if (isFlipping && !_isMeridianFlipping)
            {
                _isMeridianFlipping = true;
                _meridianFlipStartedUtc = DateTime.UtcNow;
                Logger.Info("HeartbeatService: Meridian flip started (explicit)");
            }
            else if (!isFlipping && _isMeridianFlipping)
            {
                _isMeridianFlipping = false;
                Logger.Info("HeartbeatService: Meridian flip completed (explicit)");
            }
        }
        
        /// <summary>
        /// Update detailed equipment status data
        /// </summary>
        public void SetDetailedEquipmentStatus(
            double? mountRa = null, double? mountDec = null,
            double? mountAlt = null, double? mountAz = null,
            string? sideOfPier = null, string? trackingRate = null,
            bool? isTracking = null, bool? isParked = null, bool? isSlewing = null,
            int? focuserPosition = null, double? focuserTemp = null, bool? isFocuserMoving = null,
            string? selectedFilter = null, List<string>? filterWheelFilters = null,
            double? rotatorAngle = null, bool? rotatorReverse = null, bool? rotatorCanReverse = null,
            bool? flatPanelLightOn = null, int? flatPanelBrightness = null,
            string? flatPanelCoverState = null, bool? flatPanelSupportsOpenClose = null,
            double? guidingRaRms = null, double? guidingDecRms = null, bool? isGuiding = null, bool? isCalibrating = null,
            double? cameraTemp = null, double? cameraTargetTemp = null,
            double? coolerPower = null, bool? isCoolerOn = null, int? binning = null, bool? isExposing = null,
            double? exposureDuration = null, double? exposureElapsed = null)
        {
            _mountRightAscension = mountRa;
            _mountDeclination = mountDec;
            _mountAltitude = mountAlt;
            _mountAzimuth = mountAz;
            
            // Detect meridian flip by tracking SideOfPier changes
            if (!string.IsNullOrEmpty(sideOfPier) && !string.IsNullOrEmpty(_previousSideOfPier) && sideOfPier != _previousSideOfPier)
            {
                // SideOfPier changed - this indicates a meridian flip just occurred
                _isMeridianFlipping = true;
                _meridianFlipStartedUtc = DateTime.UtcNow;
                Logger.Info($"HeartbeatService: Meridian flip detected - SideOfPier changed from {_previousSideOfPier} to {sideOfPier}");
            }
            else if (_isMeridianFlipping && _meridianFlipStartedUtc.HasValue)
            {
                // Clear flip status after 2 minutes (flip should be complete by then)
                if ((DateTime.UtcNow - _meridianFlipStartedUtc.Value).TotalMinutes > 2)
                {
                    _isMeridianFlipping = false;
                    Logger.Debug("HeartbeatService: Meridian flip status cleared (timeout)");
                }
            }
            _previousSideOfPier = sideOfPier;
            _mountSideOfPier = sideOfPier;
            _mountTrackingRate = trackingRate;
            _isTracking = isTracking;
            _isParked = isParked;
            _isSlewing = isSlewing;
            _focuserPosition = focuserPosition;
            _focuserTemperature = focuserTemp;
            _isFocuserMoving = isFocuserMoving;
            _selectedFilter = selectedFilter;
            _filterWheelFilters = filterWheelFilters;
            _rotatorAngle = rotatorAngle;
            _rotatorReverse = rotatorReverse;
            _rotatorCanReverse = rotatorCanReverse;
            _flatPanelLightOn = flatPanelLightOn;
            _flatPanelBrightness = flatPanelBrightness;
            _flatPanelCoverState = flatPanelCoverState;
            _flatPanelSupportsOpenClose = flatPanelSupportsOpenClose;
            _guidingRaRms = guidingRaRms;
            _guidingDecRms = guidingDecRms;
            _isGuiding = isGuiding;
            _isCalibrating = isCalibrating;
            _cameraTemperature = cameraTemp;
            _cameraTargetTemperature = cameraTargetTemp;
            _coolerPower = coolerPower;
            _isCoolerOn = isCoolerOn;
            _cameraBinning = binning;
            _isExposing = isExposing;
            _exposureDurationSeconds = exposureDuration;
            _exposureElapsedSeconds = exposureElapsed;
            
            // Detect important equipment changes and trigger immediate update
            // This ensures equipment cards update as fast as session status
            bool significantChange = 
                (_previousSelectedFilter != selectedFilter && selectedFilter != null) ||
                (_previousIsGuiding != isGuiding && isGuiding.HasValue) ||
                (_previousIsTracking != isTracking && isTracking.HasValue) ||
                (_previousIsSlewing != isSlewing && isSlewing.HasValue) ||
                (_previousIsExposing != isExposing && isExposing.HasValue) ||
                (_previousFocuserPosition.HasValue && focuserPosition.HasValue && 
                 Math.Abs(_previousFocuserPosition.Value - focuserPosition.Value) > 10);
            
            // Update previous values for change detection
            _previousSelectedFilter = selectedFilter;
            _previousIsGuiding = isGuiding;
            _previousIsTracking = isTracking;
            _previousIsSlewing = isSlewing;
            _previousIsExposing = isExposing;
            _previousFocuserPosition = focuserPosition;
            
            // Send immediate update for significant equipment changes (throttled to 3s)
            if (significantChange && _isRunning && !_isShuttingDown)
            {
                var timeSinceLastUpdate = (DateTime.UtcNow - _lastStatusUpdate).TotalSeconds;
                if (timeSinceLastUpdate >= 3)
                {
                    Logger.Debug($"HeartbeatService: Significant equipment change detected, sending immediate update");
                    _ = SendSessionStatusAsync();
                }
            }
        }
        
        /// <summary>
        /// Update weather data from weather device
        /// </summary>
        public void SetWeatherData(
            double? temperature = null,
            double? humidity = null,
            double? dewPoint = null,
            double? pressure = null,
            double? cloudCover = null,
            double? rainRate = null,
            double? windSpeed = null,
            double? windDirection = null,
            double? windGust = null,
            double? skyQuality = null,
            double? skyTemperature = null,
            double? starFWHM = null)
        {
            _weatherTemperature = temperature;
            _weatherHumidity = humidity;
            _weatherDewPoint = dewPoint;
            _weatherPressure = pressure;
            _weatherCloudCover = cloudCover;
            _weatherRainRate = rainRate;
            _weatherWindSpeed = windSpeed;
            _weatherWindDirection = windDirection;
            _weatherWindGust = windGust;
            _weatherSkyQuality = skyQuality;
            _weatherSkyTemperature = skyTemperature;
            _weatherStarFWHM = starFWHM;
        }
        
        private void CheckPeriodicRefresh()
        {
            var refreshInterval = _settings.AutoRefreshIntervalMinutes;
            if (refreshInterval <= 0) return; // Disabled
            
            var now = DateTime.UtcNow;
            if (_lastRefresh == null || (now - _lastRefresh.Value).TotalMinutes >= refreshInterval)
            {
                _lastRefresh = now;
                Logger.Info($"HeartbeatService: Triggering periodic refresh (interval: {refreshInterval} minutes)");
                RefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task PollRemoteCommandsAsync()
        {
            try
            {
                var commands = await _apiClient.PollCommandsAsync();
                
                if (commands != null && commands.Count > 0)
                {
                    Logger.Info($"HeartbeatService: Received {commands.Count} remote command(s)");
                    
                    foreach (var command in commands)
                    {
                        // Acknowledge receipt
                        await _apiClient.AcknowledgeCommandAsync(command.Id);
                        
                        // Set flag to prevent concurrent command polling during execution
                        _isExecutingCommand = true;
                        try
                        {
                            // Raise event for command execution
                            CommandReceived?.Invoke(this, new RemoteCommandReceivedEventArgs(command));
                        }
                        finally
                        {
                            _isExecutingCommand = false;
                        }
                        
                        // Send immediate status update after command completes
                        await SendSessionStatusAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Error polling commands: {ex.Message}");
                _isExecutingCommand = false;
            }
        }
        
        private DateTime _lastForceUpdate = DateTime.MinValue;
        private const int MIN_FORCE_UPDATE_INTERVAL_MS = 5000; // Throttle to max 1 update per 5 seconds
        
        /// <summary>
        /// Force an immediate status update to the server (call after equipment changes)
        /// Throttled to prevent excessive updates during rapid scheduler operations
        /// </summary>
        public async Task ForceStatusUpdateAsync()
        {
            try
            {
                // Throttle: skip if less than 5 seconds since last forced update
                var now = DateTime.UtcNow;
                if ((now - _lastForceUpdate).TotalMilliseconds < MIN_FORCE_UPDATE_INTERVAL_MS)
                {
                    return; // Skip this update, next heartbeat will catch up
                }
                _lastForceUpdate = now;
                
                BeforeStatusUpdate?.Invoke(this, EventArgs.Empty);
                await SendSessionStatusAsync();
                Logger.Debug("HeartbeatService: Forced status update sent");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Error sending forced status update: {ex.Message}");
            }
        }

        /// <summary>
        /// Report command execution result back to server
        /// </summary>
        public async Task ReportCommandResultAsync(Guid commandId, RemoteCommandStatus status, string? message = null)
        {
            try
            {
                var messageLength = message?.Length ?? 0;
                Logger.Info($"HeartbeatService: Reporting command {commandId} status={status}, messageLength={messageLength}");
                
                var success = await _apiClient.UpdateCommandStatusAsync(commandId, status, message);
                
                if (success)
                {
                    Logger.Info($"HeartbeatService: Command {commandId} result reported successfully: {status}");
                }
                else
                {
                    Logger.Warning($"HeartbeatService: Command {commandId} result report FAILED: {status}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Error reporting command result: {ex.Message}");
            }
        }

        private void UpdateStatus(string status)
        {
            _status = status;
            StatusChanged?.Invoke(this, new HeartbeatStatusChangedEventArgs(status, _lastHeartbeat));
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _ = DisconnectWebSocketAsync();
        }
        
        #region WebSocket Real-Time Connection
        
        /// <summary>
        /// Connect to WebSocket for real-time command delivery
        /// </summary>
        private async Task ConnectWebSocketAsync()
        {
            try
            {
                var baseUrl = _settings.ApiUrl?.TrimEnd('/');
                if (string.IsNullOrEmpty(baseUrl))
                {
                    Logger.Warning("HeartbeatService: Cannot connect WebSocket - no API URL configured");
                    return;
                }
                
                // Get JWT token for authentication
                var token = await _apiClient.GetJwtTokenAsync();
                if (string.IsNullOrEmpty(token))
                {
                    Logger.Warning("HeartbeatService: Cannot connect WebSocket - not authenticated");
                    return;
                }
                
                // Convert HTTP URL to WebSocket URL
                var wsUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://");
                wsUrl = $"{wsUrl}/ws/commands?token={Uri.EscapeDataString(token)}";
                
                _webSocket = new ClientWebSocket();
                _wsTokenSource = new CancellationTokenSource();
                
                Logger.Info($"HeartbeatService: Connecting to WebSocket at {wsUrl.Split('?')[0]}...");
                await _webSocket.ConnectAsync(new Uri(wsUrl), _wsTokenSource.Token);
                
                _wsConnected = true;
                Logger.Info("HeartbeatService: WebSocket connected - real-time commands enabled");
                
                // Start listening for messages
                _ = ListenForWebSocketMessagesAsync();
            }
            catch (Exception ex)
            {
                _wsConnected = false;
                Logger.Warning($"HeartbeatService: WebSocket connection failed (will use polling): {ex.Message}");
            }
        }
        
        /// <summary>
        /// Listen for incoming WebSocket messages
        /// </summary>
        private async Task ListenForWebSocketMessagesAsync()
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();
            
            try
            {
                while (_webSocket?.State == WebSocketState.Open && !_wsTokenSource!.Token.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _wsTokenSource.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _wsConnected = false;
                        Logger.Info("HeartbeatService: WebSocket closed by server");
                        break;
                    }
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        
                        if (result.EndOfMessage)
                        {
                            var message = messageBuilder.ToString();
                            messageBuilder.Clear();
                            
                            await ProcessWebSocketMessageAsync(message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (WebSocketException ex)
            {
                Logger.Warning($"HeartbeatService: WebSocket error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"HeartbeatService: WebSocket listener error: {ex.Message}");
            }
            finally
            {
                _wsConnected = false;
            }
        }
        
        /// <summary>
        /// Process incoming WebSocket message
        /// </summary>
        private async Task ProcessWebSocketMessageAsync(string message)
        {
            try
            {
                Logger.Debug($"HeartbeatService: Received WebSocket message: {message}");
                
                var command = JsonSerializer.Deserialize<RemoteCommandDto>(message, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                if (command != null)
                {
                    Logger.Info($"HeartbeatService: Received command via WebSocket: {command.CommandType}");
                    
                    // Acknowledge receipt
                    await _apiClient.AcknowledgeCommandAsync(command.Id);
                    
                    // Raise event for command execution
                    CommandReceived?.Invoke(this, new RemoteCommandReceivedEventArgs(command));
                }
            }
            catch (JsonException ex)
            {
                Logger.Warning($"HeartbeatService: Failed to parse WebSocket message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Disconnect from WebSocket
        /// </summary>
        private async Task DisconnectWebSocketAsync()
        {
            try
            {
                _wsTokenSource?.Cancel();
                
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    Logger.Info("HeartbeatService: WebSocket disconnected");
                }
                
                _webSocket?.Dispose();
                _webSocket = null;
                _wsConnected = false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"HeartbeatService: Error disconnecting WebSocket: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check if WebSocket is connected for real-time commands
        /// </summary>
        public bool IsSignalRConnected => _wsConnected && _webSocket?.State == WebSocketState.Open;
        
        #endregion
    }

    public class HeartbeatStatusChangedEventArgs : EventArgs
    {
        public string Status { get; }
        public DateTime? LastHeartbeat { get; }

        public HeartbeatStatusChangedEventArgs(string status, DateTime? lastHeartbeat)
        {
            Status = status;
            LastHeartbeat = lastHeartbeat;
        }
    }

    public class RemoteCommandReceivedEventArgs : EventArgs
    {
        public RemoteCommandDto Command { get; }

        public RemoteCommandReceivedEventArgs(RemoteCommandDto command)
        {
            Command = command;
        }
    }
    
    public class SchedulerModeChangedEventArgs : EventArgs
    {
        public SchedulerMode NewMode { get; }
        public SchedulerMode OldMode { get; }

        public SchedulerModeChangedEventArgs(SchedulerMode newMode, SchedulerMode oldMode)
        {
            NewMode = newMode;
            OldMode = oldMode;
        }
    }
}
