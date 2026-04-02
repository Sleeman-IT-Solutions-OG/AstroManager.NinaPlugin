using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Shared.Model.DTO.Client;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles imaging commands: RunAutofocus, StopAutofocus, TakeExposure, PlateSolve, PlateSolveAndSync
    /// </summary>
    public class ImagingCommandHandler : ICommandHandler
    {
        private readonly ICameraMediator _cameraMediator;
        private readonly IFocuserMediator _focuserMediator;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IProfileService _profileService;
        private readonly IAutoFocusVMFactory _autoFocusVMFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly HeartbeatService _heartbeatService;

        private volatile bool _isAutofocusRunning;

        public ImagingCommandHandler(
            ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IProfileService profileService,
            IAutoFocusVMFactory autoFocusVMFactory,
            IWindowServiceFactory windowServiceFactory,
            IImageHistoryVM imageHistoryVM,
            IPlateSolverFactory plateSolverFactory,
            HeartbeatService heartbeatService)
        {
            _cameraMediator = cameraMediator;
            _focuserMediator = focuserMediator;
            _telescopeMediator = telescopeMediator;
            _filterWheelMediator = filterWheelMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _profileService = profileService;
            _autoFocusVMFactory = autoFocusVMFactory;
            _windowServiceFactory = windowServiceFactory;
            _imageHistoryVM = imageHistoryVM;
            _plateSolverFactory = plateSolverFactory;
            _heartbeatService = heartbeatService;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.RunAutofocus => true,
                RemoteCommandType.StopAutofocus => true,
                RemoteCommandType.TakeExposure => true,
                RemoteCommandType.PlateSolve => true,
                RemoteCommandType.PlateSolveAndSync => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.RunAutofocus => await ExecuteAutofocusAsync(token),
                RemoteCommandType.StopAutofocus => ExecuteStopAutofocus(),
                RemoteCommandType.TakeExposure => await ExecuteTakeExposureAsync(command.Parameters, token),
                RemoteCommandType.PlateSolve => await ExecutePlateSolveAsync(false, token),
                RemoteCommandType.PlateSolveAndSync => await ExecutePlateSolveAsync(true, token),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        #region Autofocus

        private async Task<CommandResult> ExecuteAutofocusAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing AUTOFOCUS command");
            
            // Check if scheduler is running using both SharedSchedulerState AND HeartbeatService
            // HeartbeatService.IsActivelyImaging is a fallback check when SharedSchedulerState might be out of sync
            var isSchedulerRunning = SharedSchedulerState.Instance.IsSchedulerRunning || _heartbeatService.IsActivelyImaging;
            Logger.Debug($"AstroManager: Scheduler running check - SharedState={SharedSchedulerState.Instance.IsSchedulerRunning}, HeartbeatActive={_heartbeatService.IsActivelyImaging}, Combined={isSchedulerRunning}");
            
            // If scheduler is running, queue the AF request instead of executing immediately
            // The scheduler will run AF after the current exposure completes
            if (isSchedulerRunning)
            {
                if (SharedSchedulerState.Instance.PendingAutofocus)
                {
                    Logger.Info("AstroManager: AF already queued, ignoring duplicate request");
                    return CommandResult.Ok("Autofocus already queued - will run after current exposure");
                }
                
                // Force the SharedSchedulerState flag to true if heartbeat shows we're imaging
                // This ensures QueueAutofocus will work
                if (!SharedSchedulerState.Instance.IsSchedulerRunning && _heartbeatService.IsActivelyImaging)
                {
                    Logger.Warning("AstroManager: SharedSchedulerState.IsSchedulerRunning was false but HeartbeatService shows active imaging - syncing state");
                    SharedSchedulerState.Instance.SetSchedulerRunning(true);
                }
                
                var queued = SharedSchedulerState.Instance.QueueAutofocus();
                if (queued)
                {
                    Logger.Info("AstroManager: Scheduler is running - AF queued to run after current exposure completes");
                    return CommandResult.Ok("Autofocus queued - will run after current exposure completes");
                }
            }
            
            // Scheduler not running - execute AF immediately
            Logger.Info("AstroManager: Executing AUTOFOCUS immediately (scheduler not running)");
            
            if (_isAutofocusRunning)
            {
                Logger.Warning("AstroManager: AF already running, ignoring new AF request");
                return CommandResult.Fail("Autofocus is already running");
            }
            
            var afStartTime = DateTime.Now;
            
            try
            {
                _isAutofocusRunning = true;
                
                if (!_focuserMediator.GetInfo().Connected)
                    return CommandResult.Fail("Focuser is not connected");
                
                if (!_cameraMediator.GetInfo().Connected)
                    return CommandResult.Fail("Camera is not connected");
                
                var telescopeInfo = _telescopeMediator.GetInfo();
                if (telescopeInfo.Connected && telescopeInfo.CanSetTrackingEnabled && !telescopeInfo.TrackingEnabled)
                {
                    Logger.Info("AstroManager: Telescope not tracking, starting tracking before AF");
                    _telescopeMediator.SetTrackingEnabled(true);
                    await Task.Delay(500, token);
                }

                var initialPosition = _focuserMediator.GetInfo().Position;
                var initialTemp = _focuserMediator.GetInfo().Temperature;
                
                string? currentFilter = null;
                NINA.Core.Model.Equipment.FilterInfo? selectedFilter = null;
                try
                {
                    var filterInfo = _filterWheelMediator?.GetInfo();
                    if (filterInfo?.Connected == true)
                    {
                        selectedFilter = filterInfo.SelectedFilter;
                        currentFilter = selectedFilter?.Name;
                    }
                }
                catch { }
                
                Logger.Info($"AstroManager: Starting autofocus run from position {initialPosition}, Temp: {initialTemp:F1}C, Filter: {currentFilter ?? "none"}");

                var autofocusVM = _autoFocusVMFactory.Create();
                Logger.Info($"AstroManager: Created AutoFocusVM: {autofocusVM.GetType().FullName}");
                
                NotifyCollectionChangedEventHandler? afPointsHandler = null;
                INotifyCollectionChanged? afPointsCollection = null;
                var seenAfPointKeys = new HashSet<string>();
                var capturedLivePoints = new List<AutofocusDataPointDto>();
                
                try
                {
                    afPointsCollection = TryGetAutofocusPointsCollection(autofocusVM);
                    if (afPointsCollection != null)
                    {
                        Logger.Info($"AstroManager: Subscribed to AutoFocusVM points collection: {afPointsCollection.GetType().FullName}");

                        afPointsHandler = (sender, e) =>
                        {
                            try
                            {
                                Logger.Info($"AstroManager: AF CollectionChanged fired - Action={e.Action}, NewItems={e.NewItems?.Count ?? 0}");
                                
                                System.Collections.IList? itemsToProcess = null;
                                
                                if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                                    itemsToProcess = e.NewItems;
                                else if (e.Action == NotifyCollectionChangedAction.Reset && sender is System.Collections.IEnumerable enumerable)
                                    itemsToProcess = enumerable.Cast<object>().ToList();
                                else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems != null)
                                    itemsToProcess = e.NewItems;
                                
                                if (itemsToProcess == null || itemsToProcess.Count == 0)
                                    return;

                                foreach (var item in itemsToProcess)
                                {
                                    if (!TryReadAfPoint(item, out var pos, out var hfr, out var stars))
                                        continue;

                                    var key = $"{pos}_{hfr:F4}_{stars}";
                                    if (!seenAfPointKeys.Add(key)) continue;

                                    Logger.Info($"AstroManager: Live AF point captured - Pos: {pos}, HFR: {hfr:F2}, Stars: {stars}");
                                    
                                    capturedLivePoints.Add(new AutofocusDataPointDto
                                    {
                                        Position = pos,
                                        Hfr = hfr,
                                        StarCount = stars
                                    });
                                    
                                    var shouldUpdate = _heartbeatService.AddCurrentAutofocusPoint(new AutofocusDataPointDto
                                    {
                                        Position = pos,
                                        Hfr = hfr,
                                        StarCount = stars
                                    }, currentFinalPosition: _focuserMediator.GetInfo().Position);

                                    if (shouldUpdate)
                                        _ = _heartbeatService.ForceStatusUpdateAsync();
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"AstroManager: Failed to stream AF point: {ex.Message}");
                            }
                        };

                        afPointsCollection.CollectionChanged += afPointsHandler;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"AstroManager: Failed to initialize live AF streaming: {ex.Message}");
                }
                
                var lastStatusUpdate = DateTime.UtcNow;
                var afHasStarted = false;
                
                bool afSucceeded = false;
                string afError = "";
                object? afResult = null;
                
                try
                {
                    var windowService = _windowServiceFactory.Create();
                    windowService.Show(autofocusVM, "AutoFocus", System.Windows.ResizeMode.CanResize, System.Windows.WindowStyle.ToolWindow);
                    
                    var statusMediator = new Progress<ApplicationStatus>(status =>
                    {
                        if (!afHasStarted && !string.IsNullOrEmpty(status.Status))
                        {
                            afHasStarted = true;
                            _heartbeatService.StartCurrentAutofocusReport(_focuserMediator.GetInfo().Temperature, currentFilter);
                            _heartbeatService.SetOperationStatus("Autofocus: Starting...", "Autofocusing");
                            _ = _heartbeatService.ForceStatusUpdateAsync();
                            Logger.Info("AstroManager: AF actually started running now");
                        }
                        
                        if (afHasStarted && (DateTime.UtcNow - lastStatusUpdate).TotalSeconds >= 1 && !string.IsNullOrEmpty(status.Status))
                        {
                            lastStatusUpdate = DateTime.UtcNow;
                            var currentPos = _focuserMediator.GetInfo().Position;
                            _heartbeatService.SetOperationStatus($"AF: Running (Pos: {currentPos})", "Autofocusing");
                            _ = _heartbeatService.ForceStatusUpdateAsync();
                        }
                    });
                    
                    afResult = await autofocusVM.StartAutoFocus(selectedFilter, token, statusMediator);
                    
                    if (afResult != null)
                    {
                        afSucceeded = true;
                        try
                        {
                            var appendMethod = _imageHistoryVM?.GetType().GetMethod("AppendAutoFocusPoint");
                            appendMethod?.Invoke(_imageHistoryVM, new[] { afResult });
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"AstroManager: Failed to append AF point to history: {ex.Message}");
                        }
                        
                        var calcPoint = afResult.GetType().GetProperty("CalculatedFocusPoint")?.GetValue(afResult);
                        var pos = calcPoint?.GetType().GetProperty("Position")?.GetValue(calcPoint) ?? 0;
                        var hfr = calcPoint?.GetType().GetProperty("Value")?.GetValue(calcPoint) ?? 0;
                        Logger.Info($"AstroManager: AF completed - Position: {pos}, HFR: {hfr:F2}");
                    }
                    else
                    {
                        afError = "Autofocus returned null result";
                        Logger.Warning("AstroManager: AF returned null result");
                    }
                    
                    windowService.DelayedClose(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    afError = ex.Message;
                    Logger.Warning($"AstroManager: AF execution threw exception: {ex.Message}");
                }
                finally
                {
                    if (afPointsCollection != null && afPointsHandler != null)
                        afPointsCollection.CollectionChanged -= afPointsHandler;
                }
                
                var afReport = ReadLatestNinaAfReport(afStartTime);
                
                var finalPosition = _focuserMediator.GetInfo().Position;
                var finalTemp = _focuserMediator.GetInfo().Temperature;
                var positionChange = finalPosition - initialPosition;
                
                var dataPoints = new List<AutofocusDataPointDto>();
                double finalHfr = 0;
                string fittingMethod = "Hyperbolic";
                string? failureReason = null;
                double? rSquared = null;
                double? rSquaredHyperbolic = null;
                double? rSquaredParabolic = null;
                
                if (afResult != null)
                {
                    ExtractAfDataFromResult(afResult, ref dataPoints, ref finalPosition, ref finalHfr, 
                        ref fittingMethod, ref rSquared, ref rSquaredHyperbolic, ref rSquaredParabolic, 
                        ref finalTemp, ref currentFilter);
                }
                else if (afReport.HasValue)
                {
                    ExtractAfDataFromJson(afReport.Value, ref dataPoints, ref finalPosition, ref finalHfr, 
                        ref fittingMethod, ref finalTemp, ref currentFilter, ref failureReason, afSucceeded);
                }
                else
                {
                    ExtractAfDataFromFallback(capturedLivePoints, ref dataPoints, ref finalHfr);
                }
                
                if (!afSucceeded && finalPosition > 0 && finalHfr > 0)
                {
                    afSucceeded = true;
                    failureReason = null;
                    Logger.Info($"AstroManager: AF success detected - Pos={finalPosition}, HFR={finalHfr:F2}, Points={dataPoints.Count}");
                }
                
                if (!afSucceeded)
                    failureReason = failureReason ?? "Curve fitting failed - no valid focus curve";
                
                var amAfReport = new AutofocusReportDto
                {
                    CompletedAt = DateTime.UtcNow,
                    Success = afSucceeded,
                    FinalPosition = finalPosition,
                    FinalHfr = finalHfr,
                    Temperature = finalTemp,
                    Filter = currentFilter,
                    FittingMethod = fittingMethod,
                    DataPoints = dataPoints,
                    FailureReason = failureReason,
                    RSquared = rSquared,
                    RSquaredHyperbolic = rSquaredHyperbolic,
                    RSquaredParabolic = rSquaredParabolic
                };
                
                _heartbeatService.SetAutofocusReport(amAfReport);
                _heartbeatService.ClearCurrentAutofocusReport();
                _heartbeatService.SetOperationStatus("AF Complete", "Imaging");
                await _heartbeatService.ForceStatusUpdateAsync();
                
                if (afSucceeded)
                {
                    var resultMessage = $"Position: {finalPosition} (Δ{positionChange:+#;-#;0}) | HFR: {finalHfr:F2} | Temp: {finalTemp:F1}°C | {dataPoints.Count} pts";
                    return CommandResult.Ok(resultMessage);
                }
                else
                {
                    var errorMsg = failureReason ?? afError ?? "Autofocus failed";
                    return CommandResult.Fail($"AF failed: {errorMsg}");
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("AstroManager: Autofocus was cancelled");
                _heartbeatService.SetOperationStatus("AF Cancelled", "Imaging");
                await _heartbeatService.ForceStatusUpdateAsync();
                return CommandResult.Fail("Autofocus was cancelled");
            }
            catch (Exception ex)
            {
                var errorDetails = ex.InnerException != null ? $"{ex.Message} -> {ex.InnerException.Message}" : ex.Message;
                Logger.Error($"AstroManager: Failed to run autofocus: {errorDetails}");
                _heartbeatService.SetOperationStatus("AF Failed", "Imaging");
                await _heartbeatService.ForceStatusUpdateAsync();
                return CommandResult.Fail($"AF failed: {errorDetails}");
            }
            finally
            {
                _isAutofocusRunning = false;
            }
        }

        private CommandResult ExecuteStopAutofocus()
        {
            Logger.Info("AstroManager: Executing STOP AUTOFOCUS");
            try
            {
                _cameraMediator.AbortExposure();
                _heartbeatService.SetOperationStatus("AF Stopped", "Imaging");
                return CommandResult.Ok("Autofocus interrupted (exposure aborted)");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop autofocus: {ex.Message}");
                return CommandResult.Fail($"Failed to stop autofocus: {ex.Message}");
            }
        }

        #endregion

        #region Take Exposure

        private class ExposureParameters
        {
            public double Duration { get; set; } = 10;
            public double ExposureTime { get; set; } = 10;
            public int Bin { get; set; } = 1;
            public int Gain { get; set; } = -1;
            public int Offset { get; set; } = -1;
        }

        private async Task<CommandResult> ExecuteTakeExposureAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing TAKE EXPOSURE");
            try
            {
                var cameraInfo = _cameraMediator.GetInfo();
                if (!cameraInfo.Connected)
                    return CommandResult.Fail("Camera not connected");
                
                double exposureTime = 10;
                int binning = 1;
                int gain = -1;
                int offset = -1;
                
                if (!string.IsNullOrEmpty(parameters))
                {
                    var expParams = JsonSerializer.Deserialize<ExposureParameters>(parameters);
                    if (expParams != null)
                    {
                        exposureTime = expParams.Duration > 0 ? expParams.Duration : expParams.ExposureTime;
                        binning = expParams.Bin > 0 ? expParams.Bin : 1;
                        gain = expParams.Gain;
                        offset = expParams.Offset;
                    }
                }
                
                Logger.Info($"AstroManager: Taking {exposureTime}s exposure (Bin={binning}, Gain={gain}, Offset={offset})");
                
                var exposureItem = new NINA.Sequencer.SequenceItem.Imaging.TakeExposure(
                    _profileService, 
                    _cameraMediator, 
                    _imagingMediator, 
                    _imageSaveMediator, 
                    _imageHistoryVM);
                
                exposureItem.ExposureTime = exposureTime;
                exposureItem.ExposureCount = 1;
                exposureItem.ImageType = "SNAPSHOT";
                exposureItem.Binning = new BinningMode((short)binning, (short)binning);
                if (gain >= 0) exposureItem.Gain = gain;
                if (offset >= 0) exposureItem.Offset = offset;
                
                var progress = new Progress<ApplicationStatus>(status =>
                {
                    Logger.Debug($"AstroManager: Exposure progress: {status.Status}");
                });
                
                await exposureItem.Execute(progress, token);
                
                Logger.Info($"AstroManager: Exposure completed successfully");
                return CommandResult.Ok($"Exposure {exposureTime}s completed");
            }
            catch (OperationCanceledException)
            {
                return CommandResult.Fail("Exposure cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to take exposure: {ex.Message}");
                return CommandResult.Fail($"Failed to take exposure: {ex.Message}");
            }
        }

        #endregion

        #region Plate Solve

        private async Task<CommandResult> ExecutePlateSolveAsync(bool syncMount, CancellationToken token)
        {
            Logger.Info($"AstroManager: Executing PLATE SOLVE (Sync: {syncMount})");
            var startTime = DateTime.UtcNow;
            
            try
            {
                if (!_cameraMediator.GetInfo().Connected)
                    return CommandResult.Fail("Camera is not connected");

                if (syncMount && !_telescopeMediator.GetInfo().Connected)
                    return CommandResult.Fail("Telescope is not connected (required for sync)");

                var telescopeInfoBefore = _telescopeMediator.GetInfo();
                double? raBefore = telescopeInfoBefore.Connected && telescopeInfoBefore.Coordinates != null 
                    ? telescopeInfoBefore.Coordinates.RA : null;
                double? decBefore = telescopeInfoBefore.Connected && telescopeInfoBefore.Coordinates != null 
                    ? telescopeInfoBefore.Coordinates.Dec : null;
                
                Logger.Info($"AstroManager: Position before solve: RA={raBefore:F5}h, Dec={decBefore:F4}°");
                
                _heartbeatService.SetOperationStatus("Plate Solve: Capturing...", "PlateSolving");
                await _heartbeatService.ForceStatusUpdateAsync();
                
                var plateSolveSettings = _profileService.ActiveProfile.PlateSolveSettings;
                var cameraInfo = _cameraMediator.GetInfo();
                
                var plateSolver = _plateSolverFactory.GetPlateSolver(plateSolveSettings);
                var blindSolver = _plateSolverFactory.GetBlindSolver(plateSolveSettings);
                
                if (plateSolver == null)
                    return CommandResult.Fail("Plate solver not configured - check NINA Options > Plate Solving");
                
                var captureSolver = _plateSolverFactory.GetCaptureSolver(
                    plateSolver,
                    blindSolver,
                    _imagingMediator,
                    _filterWheelMediator);
                
                Coordinates hintCoords = null;
                if (telescopeInfoBefore.Connected && telescopeInfoBefore.Coordinates != null)
                    hintCoords = telescopeInfoBefore.Coordinates;
                
                var captureSequence = new CaptureSequence(
                    plateSolveSettings.ExposureTime,
                    CaptureSequence.ImageTypes.SNAPSHOT,
                    plateSolveSettings.Filter,
                    new BinningMode(plateSolveSettings.Binning, plateSolveSettings.Binning),
                    1)
                {
                    Gain = plateSolveSettings.Gain
                };
                
                var solverParameter = new CaptureSolverParameter
                {
                    Coordinates = hintCoords,
                    FocalLength = _profileService.ActiveProfile.TelescopeSettings.FocalLength,
                    PixelSize = cameraInfo.PixelSize,
                    DownSampleFactor = plateSolveSettings.DownSampleFactor,
                    MaxObjects = plateSolveSettings.MaxObjects,
                    SearchRadius = plateSolveSettings.SearchRadius,
                    Regions = plateSolveSettings.Regions,
                    BlindFailoverEnabled = plateSolveSettings.BlindFailoverEnabled
                };
                
                _heartbeatService.SetOperationStatus($"Plate Solve: Solving...", status: "PlateSolving");
                _ = _heartbeatService.ForceStatusUpdateAsync();
                
                var solveResult = await captureSolver.Solve(
                    captureSequence,
                    solverParameter,
                    new Progress<PlateSolveProgress>(psProgress =>
                    {
                        _heartbeatService.SetOperationStatus($"Plate Solve: Processing...", status: "PlateSolving");
                        _ = _heartbeatService.ForceStatusUpdateAsync();
                    }),
                    new Progress<ApplicationStatus>(status =>
                    {
                        if (!string.IsNullOrEmpty(status.Status))
                        {
                            _heartbeatService.SetOperationStatus($"Plate Solve: {status.Status}", status: "PlateSolving");
                            _ = _heartbeatService.ForceStatusUpdateAsync();
                        }
                    }),
                    token);
                
                var solveDuration = (DateTime.UtcNow - startTime).TotalSeconds;
                
                if (solveResult.Success)
                {
                    var raHours = solveResult.Coordinates.RA;
                    var dec = solveResult.Coordinates.Dec;
                    var raHms = $"{(int)raHours}h{(int)((raHours % 1) * 60)}m{((raHours * 60) % 1 * 60):F1}s";
                    var decDms = $"{(dec >= 0 ? "+" : "")}{(int)dec}°{Math.Abs((int)((dec % 1) * 60))}'{Math.Abs((dec * 60) % 1 * 60):F1}\"";
                    
                    double? skyPA = solveResult.PositionAngle;
                    double? pixelScale = solveResult.Pixscale;
                    
                    double? raSepArcsec = null;
                    double? decSepArcsec = null;
                    double? totalSepArcsec = null;
                    if (raBefore.HasValue && decBefore.HasValue)
                    {
                        raSepArcsec = (raHours - raBefore.Value) * 15.0 * 3600.0 * Math.Cos(dec * Math.PI / 180.0);
                        decSepArcsec = (dec - decBefore.Value) * 3600.0;
                        totalSepArcsec = Math.Sqrt(raSepArcsec.Value * raSepArcsec.Value + decSepArcsec.Value * decSepArcsec.Value);
                    }
                    
                    if (syncMount && _telescopeMediator.GetInfo().Connected)
                    {
                        _heartbeatService.SetOperationStatus($"Plate Solve: Syncing...", status: "PlateSolving");
                        _ = _heartbeatService.ForceStatusUpdateAsync();
                        
                        await _telescopeMediator.Sync(solveResult.Coordinates);
                        Logger.Info($"AstroManager: Synced mount to solved coordinates");
                    }
                    
                    var psReport = new PlateSolveReportDto
                    {
                        CompletedAt = DateTime.UtcNow,
                        Success = true,
                        SolvedRa = raHours,
                        SolvedDec = dec,
                        RaFormatted = raHms,
                        DecFormatted = decDms,
                        Rotation = skyPA,
                        PixelScale = pixelScale,
                        WasSynced = syncMount,
                        SolveDurationSeconds = solveDuration,
                        SeparationArcsec = totalSepArcsec,
                        RaSeparationArcsec = raSepArcsec,
                        DecSeparationArcsec = decSepArcsec
                    };
                    _heartbeatService.SetPlateSolveReport(psReport);
                    
                    _heartbeatService.SetOperationStatus("Plate Solve Complete", "Imaging");
                    await _heartbeatService.ForceStatusUpdateAsync();
                    
                    var resultMessage = $"RA: {raHms} | Dec: {decDms} | PA: {skyPA:F1}°" + (syncMount ? " | Synced ✓" : "");
                    Logger.Info($"AstroManager: Plate solve completed in {solveDuration:F1}s");
                    return CommandResult.Ok(resultMessage);
                }
                
                var psReportFailed = new PlateSolveReportDto
                {
                    CompletedAt = DateTime.UtcNow,
                    Success = false,
                    FailureReason = "Plate solve failed - no solution found",
                    SolveDurationSeconds = solveDuration
                };
                _heartbeatService.SetPlateSolveReport(psReportFailed);
                _heartbeatService.SetOperationStatus("Plate Solve Failed", "Imaging");
                await _heartbeatService.ForceStatusUpdateAsync();
                
                return CommandResult.Fail("Plate solve failed - no solution found");
            }
            catch (OperationCanceledException)
            {
                Logger.Info("AstroManager: Plate solve was cancelled");
                _heartbeatService.SetOperationStatus("Plate Solve Cancelled", "Imaging");
                await _heartbeatService.ForceStatusUpdateAsync();
                return CommandResult.Fail("Plate solve was cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to plate solve: {ex.Message}");
                
                var psReportFailed = new PlateSolveReportDto
                {
                    CompletedAt = DateTime.UtcNow,
                    Success = false,
                    FailureReason = ex.Message,
                    SolveDurationSeconds = (DateTime.UtcNow - startTime).TotalSeconds
                };
                _heartbeatService.SetPlateSolveReport(psReportFailed);
                _heartbeatService.SetOperationStatus("Plate Solve Error", "Imaging");
                await _heartbeatService.ForceStatusUpdateAsync();
                
                return CommandResult.Fail($"Failed to plate solve: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private JsonElement? ReadLatestNinaAfReport(DateTime afterTime)
        {
            try
            {
                var afFolder = Path.Combine(NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");
                if (!Directory.Exists(afFolder))
                    return null;
                
                var afFiles = Directory.GetFiles(afFolder, "*.json")
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime >= afterTime.AddMinutes(-1))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                
                if (afFiles.Count == 0)
                {
                    var latestAny = Directory.GetFiles(afFolder, "*.json")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (latestAny == null)
                        return null;

                    afFiles.Add(latestAny);
                }
                
                var latestFile = afFiles.First();
                Logger.Info($"AstroManager: Reading AF report from {latestFile.FullName}");
                
                var json = File.ReadAllText(latestFile.FullName);
                var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to read NINA AF report: {ex.Message}");
                return null;
            }
        }

        private static INotifyCollectionChanged? TryGetAutofocusPointsCollection(object? source)
        {
            if (source == null) return null;

            try
            {
                var sourceType = source.GetType();
                var propertyNames = new[] { "FocusPoints", "AutoFocusPoints", "ChartPoints", "PlotPoints", "MeasurePoints" };
                
                foreach (var propName in propertyNames)
                {
                    var prop = sourceType.GetProperty(propName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (prop != null)
                    {
                        var val = prop.GetValue(source);
                        if (val is INotifyCollectionChanged incc)
                            return incc;
                    }
                }

                foreach (var p in sourceType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    object? val;
                    try { val = p.GetValue(source); } catch { continue; }
                    if (val is INotifyCollectionChanged incc)
                    {
                        if (p.Name.IndexOf("Focus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            p.Name.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            p.Name.IndexOf("Chart", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return incc;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static bool TryReadAfPoint(object? item, out int position, out double hfr, out int stars)
        {
            position = 0;
            hfr = 0;
            stars = 0;
            if (item == null) return false;

            try
            {
                var t = item.GetType();

                position = TryGetInt(t, item, "FocuserPosition") 
                    ?? TryGetInt(t, item, "Position") 
                    ?? (int?)(TryGetDouble(t, item, "X") ?? 0) 
                    ?? 0;
                    
                var rawHfr = TryGetDouble(t, item, "HFR") 
                    ?? TryGetDouble(t, item, "Hfr") 
                    ?? TryGetDouble(t, item, "Value");
                
                if (!rawHfr.HasValue || double.IsNaN(rawHfr.Value))
                {
                    var yVal = TryGetDouble(t, item, "Y");
                    var errorY = TryGetDouble(t, item, "ErrorY");
                    
                    if (yVal.HasValue && !double.IsNaN(yVal.Value) && !double.IsInfinity(yVal.Value))
                        rawHfr = yVal;
                    else if (errorY.HasValue && !double.IsNaN(errorY.Value) && !double.IsInfinity(errorY.Value) && errorY.Value > 0)
                        rawHfr = errorY;
                }
                
                if (rawHfr.HasValue && !double.IsNaN(rawHfr.Value) && !double.IsInfinity(rawHfr.Value))
                    hfr = rawHfr.Value;
                else
                    hfr = 0;
                    
                stars = TryGetInt(t, item, "Stars") ?? TryGetInt(t, item, "StarCount") ?? 0;

                return position > 0 && hfr > 0;
            }
            catch
            {
                return false;
            }
        }

        private static int? TryGetInt(Type t, object instance, string propName)
        {
            var p = t.GetProperty(propName);
            if (p == null) return null;
            var v = p.GetValue(instance);
            if (v == null) return null;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            return null;
        }

        private static double? TryGetDouble(Type t, object instance, string propName)
        {
            var p = t.GetProperty(propName);
            if (p == null) return null;
            var v = p.GetValue(instance);
            if (v == null) return null;
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is long l) return l;
            return null;
        }

        private void ExtractAfDataFromResult(object afResult, ref List<AutofocusDataPointDto> dataPoints,
            ref int finalPosition, ref double finalHfr, ref string fittingMethod, 
            ref double? rSquared, ref double? rSquaredHyperbolic, ref double? rSquaredParabolic,
            ref double finalTemp, ref string? currentFilter)
        {
            var resultType = afResult.GetType();
            
            var measurePointsProp = resultType.GetProperty("MeasurePoints");
            var measurePoints = measurePointsProp?.GetValue(afResult) as System.Collections.IEnumerable;
            if (measurePoints != null)
            {
                foreach (var mp in measurePoints)
                {
                    var mpType = mp.GetType();
                    var posVal = mpType.GetProperty("Position")?.GetValue(mp);
                    var hfrVal = mpType.GetProperty("Value")?.GetValue(mp);
                    
                    double pos = posVal != null ? Convert.ToDouble(posVal) : 0;
                    double hfr = hfrVal != null ? Convert.ToDouble(hfrVal) : 0;
                    
                    if (pos > 0 && hfr > 0)
                    {
                        dataPoints.Add(new AutofocusDataPointDto
                        {
                            Position = (int)pos,
                            Hfr = hfr,
                            StarCount = 0
                        });
                    }
                }
            }
            
            var calcPointProp = resultType.GetProperty("CalculatedFocusPoint");
            var calcPoint = calcPointProp?.GetValue(afResult);
            if (calcPoint != null)
            {
                var cpType = calcPoint.GetType();
                var cpPos = cpType.GetProperty("Position")?.GetValue(calcPoint);
                var cpHfr = cpType.GetProperty("Value")?.GetValue(calcPoint);
                if (cpPos != null) finalPosition = Convert.ToInt32(cpPos);
                if (cpHfr != null) finalHfr = Convert.ToDouble(cpHfr);
            }
            
            var fittingsProp = resultType.GetProperty("Fittings");
            var fittingsVal = fittingsProp?.GetValue(afResult);
            if (fittingsVal != null) fittingMethod = fittingsVal.ToString() ?? "Hyperbolic";
            
            var rSquaresProp = resultType.GetProperty("RSquares");
            var rSquaresVal = rSquaresProp?.GetValue(afResult);
            if (rSquaresVal != null)
            {
                var rsType = rSquaresVal.GetType();
                var hypR2Prop = rsType.GetProperty("Hyperbolic");
                var paraR2Prop = rsType.GetProperty("Parabolic");
                
                if (hypR2Prop != null)
                {
                    var hypR2Val = hypR2Prop.GetValue(rSquaresVal);
                    if (hypR2Val != null) rSquaredHyperbolic = Convert.ToDouble(hypR2Val);
                }
                if (paraR2Prop != null)
                {
                    var paraR2Val = paraR2Prop.GetValue(rSquaresVal);
                    if (paraR2Val != null) rSquaredParabolic = Convert.ToDouble(paraR2Val);
                }
                
                if (fittingMethod.Contains("Hyperbolic", StringComparison.OrdinalIgnoreCase) && rSquaredHyperbolic.HasValue)
                    rSquared = rSquaredHyperbolic;
                else if (fittingMethod.Contains("Parabolic", StringComparison.OrdinalIgnoreCase) && rSquaredParabolic.HasValue)
                    rSquared = rSquaredParabolic;
                else
                    rSquared = rSquaredHyperbolic ?? rSquaredParabolic;
            }
            
            var tempProp = resultType.GetProperty("Temperature");
            var tempVal = tempProp?.GetValue(afResult);
            if (tempVal != null)
            {
                var t = Convert.ToDouble(tempVal);
                if (t > -100) finalTemp = t;
            }
            
            var filterProp = resultType.GetProperty("Filter");
            var filterVal = filterProp?.GetValue(afResult) as string;
            if (!string.IsNullOrEmpty(filterVal)) currentFilter = filterVal;
        }

        private void ExtractAfDataFromJson(JsonElement report, ref List<AutofocusDataPointDto> dataPoints,
            ref int finalPosition, ref double finalHfr, ref string fittingMethod, 
            ref double finalTemp, ref string? currentFilter, ref string? failureReason, bool afSucceeded)
        {
            JsonElement? measurePoints = null;
            if (report.TryGetProperty("MeasurePoints", out var mp)) measurePoints = mp;
            else if (report.TryGetProperty("Measurements", out var m2)) measurePoints = m2;
            else if (report.TryGetProperty("Points", out var m3)) measurePoints = m3;
            
            if (measurePoints.HasValue && measurePoints.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in measurePoints.Value.EnumerateArray())
                {
                    double pos = 0;
                    double hfr = 0;

                    if (point.TryGetProperty("Position", out var posVal) && posVal.ValueKind == JsonValueKind.Number)
                        pos = posVal.GetDouble();
                    else if (point.TryGetProperty("FocuserPosition", out var fpVal) && fpVal.ValueKind == JsonValueKind.Number)
                        pos = fpVal.GetDouble();

                    if (point.TryGetProperty("Value", out var hfrVal) && hfrVal.ValueKind == JsonValueKind.Number)
                        hfr = hfrVal.GetDouble();
                    else if (point.TryGetProperty("HFR", out var hfrVal2) && hfrVal2.ValueKind == JsonValueKind.Number)
                        hfr = hfrVal2.GetDouble();

                    if (pos > 0 && hfr > 0)
                    {
                        dataPoints.Add(new AutofocusDataPointDto
                        {
                            Position = (int)pos,
                            Hfr = hfr,
                            StarCount = 0
                        });
                    }
                }
            }

            JsonElement? calcPoint = null;
            if (report.TryGetProperty("CalculatedFocusPoint", out var cp)) calcPoint = cp;
            else if (report.TryGetProperty("CalculatedPoint", out var cp2)) calcPoint = cp2;
            else if (report.TryGetProperty("BestFocusPoint", out var cp3)) calcPoint = cp3;
            
            if (calcPoint.HasValue && calcPoint.Value.ValueKind == JsonValueKind.Object)
            {
                if (calcPoint.Value.TryGetProperty("Position", out var calcPos) && calcPos.ValueKind == JsonValueKind.Number)
                    finalPosition = (int)calcPos.GetDouble();
                else if (calcPoint.Value.TryGetProperty("FocuserPosition", out var calcFp) && calcFp.ValueKind == JsonValueKind.Number)
                    finalPosition = (int)calcFp.GetDouble();

                if (calcPoint.Value.TryGetProperty("Value", out var calcHfr) && calcHfr.ValueKind == JsonValueKind.Number)
                    finalHfr = calcHfr.GetDouble();
                else if (calcPoint.Value.TryGetProperty("HFR", out var calcHfr2) && calcHfr2.ValueKind == JsonValueKind.Number)
                    finalHfr = calcHfr2.GetDouble();
            }
            
            if (report.TryGetProperty("Fitting", out var fitting))
                fittingMethod = fitting.GetString() ?? "Hyperbolic";
            
            if (report.TryGetProperty("Temperature", out var temp))
                finalTemp = temp.GetDouble();
            
            if (report.TryGetProperty("Filter", out var filter))
                currentFilter = filter.GetString();
            
            if (!afSucceeded)
            {
                if (report.TryGetProperty("RSquares", out var rSquares))
                {
                    if (rSquares.TryGetProperty("Hyperbolic", out var hypR2) && hypR2.GetDouble() < 0.5)
                        failureReason = $"Hyperbolic fit failed (R²={hypR2.GetDouble():F2})";
                    else if (rSquares.TryGetProperty("Parabolic", out var paraR2) && paraR2.GetDouble() < 0.5)
                        failureReason = $"Parabolic fit failed (R²={paraR2.GetDouble():F2})";
                }
                
                if (string.IsNullOrEmpty(failureReason))
                    failureReason = "Curve fitting failed - no valid hyperbolic/parabolic fit";
            }
        }

        private void ExtractAfDataFromFallback(List<AutofocusDataPointDto> capturedLivePoints, 
            ref List<AutofocusDataPointDto> dataPoints, ref double finalHfr)
        {
            if (capturedLivePoints.Count > 0)
            {
                Logger.Info($"AstroManager: Using {capturedLivePoints.Count} live captured points");
                dataPoints.AddRange(capturedLivePoints);
                
                var lastLivePoint = capturedLivePoints.LastOrDefault();
                if (lastLivePoint != null && lastLivePoint.Hfr > 0)
                    finalHfr = lastLivePoint.Hfr;
            }
            
            if (dataPoints.Count == 0 && _imageHistoryVM?.AutoFocusPoints != null && _imageHistoryVM.AutoFocusPoints.Count > 0)
            {
                Logger.Info($"AstroManager: Found {_imageHistoryVM.AutoFocusPoints.Count} points in AutoFocusPoints");
                foreach (var point in _imageHistoryVM.AutoFocusPoints)
                {
                    if (point.HFR > 0 && point.FocuserPosition > 0)
                    {
                        dataPoints.Add(new AutofocusDataPointDto
                        {
                            Position = point.FocuserPosition,
                            Hfr = point.HFR,
                            StarCount = point.Stars
                        });
                    }
                }
                
                var lastPoint = _imageHistoryVM.AutoFocusPoints.LastOrDefault();
                if (lastPoint != null && lastPoint.HFR > 0)
                    finalHfr = lastPoint.HFR;
            }
            
            if (dataPoints.Count == 0 && _imageHistoryVM?.ImageHistory != null)
            {
                var afImages = _imageHistoryVM.ImageHistory
                    .Where(img => img.HFR > 0 && img.FocuserPosition > 0)
                    .TakeLast(20)
                    .ToList();
                
                foreach (var img in afImages)
                {
                    dataPoints.Add(new AutofocusDataPointDto
                    {
                        Position = img.FocuserPosition,
                        Hfr = img.HFR,
                        StarCount = img.Stars
                    });
                }
                
                var lastAfImage = afImages.LastOrDefault();
                if (lastAfImage != null)
                    finalHfr = lastAfImage.HFR;
            }
        }

        #endregion
    }
}
