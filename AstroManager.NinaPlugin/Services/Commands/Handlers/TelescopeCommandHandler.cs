using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Shared.Model.DTO.Client;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles telescope/mount-related commands: Slew, Center, SetTracking, StopMount
    /// </summary>
    public class TelescopeCommandHandler : ICommandHandler
    {
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IProfileService _profileService;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        private readonly Action<string, string> _setOperationStatus;
        private readonly Func<Task> _forceStatusUpdate;

        public TelescopeCommandHandler(
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IGuiderMediator guiderMediator,
            IRotatorMediator rotatorMediator,
            IFilterWheelMediator filterWheelMediator,
            IImagingMediator imagingMediator,
            IProfileService profileService,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            Action<string, string> setOperationStatus,
            Func<Task> forceStatusUpdate)
        {
            _telescopeMediator = telescopeMediator;
            _cameraMediator = cameraMediator;
            _guiderMediator = guiderMediator;
            _rotatorMediator = rotatorMediator;
            _filterWheelMediator = filterWheelMediator;
            _imagingMediator = imagingMediator;
            _profileService = profileService;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _setOperationStatus = setOperationStatus;
            _forceStatusUpdate = forceStatusUpdate;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.SlewToCoordinates => true,
                RemoteCommandType.SlewAndCenter => true,
                RemoteCommandType.CenterTarget => true,
                RemoteCommandType.MeridianFlip => true,
                RemoteCommandType.SetTracking => true,
                RemoteCommandType.SetTrackingRate => true,
                RemoteCommandType.StopMount => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.SlewToCoordinates => await ExecuteSlewToCoordinatesAsync(command.Parameters, false, token),
                RemoteCommandType.SlewAndCenter => await ExecuteSlewToCoordinatesAsync(command.Parameters, true, token),
                RemoteCommandType.CenterTarget => await ExecuteCenterTargetAsync(token),
                RemoteCommandType.MeridianFlip => await ExecuteMeridianFlipAsync(token),
                RemoteCommandType.SetTracking => ExecuteSetTracking(command.Parameters),
                RemoteCommandType.SetTrackingRate => ExecuteSetTrackingRate(command.Parameters),
                RemoteCommandType.StopMount => ExecuteStopMount(),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        #region Slew Commands

        private class SlewParameters
        {
            public double RaHours { get; set; }
            public double Dec { get; set; }
        }

        private async Task<CommandResult> ExecuteSlewToCoordinatesAsync(string? parameters, bool center, CancellationToken token)
        {
            Logger.Info($"AstroManager: Executing SLEW TO COORDINATES (center={center})");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No coordinates provided");

                var coords = JsonSerializer.Deserialize<SlewParameters>(parameters);
                if (coords == null)
                    return CommandResult.Fail("Invalid coordinates format");

                var raAngle = Angle.ByHours(coords.RaHours);
                var decAngle = Angle.ByDegree(coords.Dec);
                var targetCoords = new Coordinates(raAngle, decAngle, Epoch.J2000);
                var inputCoordinates = new InputCoordinates(targetCoords);

                _setOperationStatus("Slewing to target...", "Slewing");
                await _forceStatusUpdate();

                var slewItem = new SlewScopeToRaDec(_telescopeMediator, _guiderMediator);
                slewItem.Coordinates = inputCoordinates;
                await slewItem.Execute(new Progress<NINA.Core.Model.ApplicationStatus>(status =>
                {
                    if (!string.IsNullOrEmpty(status.Status))
                    {
                        _setOperationStatus($"Slewing: {status.Status}", "Slewing");
                        _ = _forceStatusUpdate();
                    }
                }), token);

                if (center)
                {
                    if (!_cameraMediator.GetInfo().Connected)
                    {
                        _setOperationStatus("Centering Failed", "Imaging");
                        return CommandResult.Fail("Camera is not connected (required for centering)");
                    }

                    _setOperationStatus("Centering: Plate solving...", "PlateSolving");
                    await _forceStatusUpdate();

                    var solveAndSync = new SolveAndSync(
                        _profileService,
                        _telescopeMediator,
                        _rotatorMediator,
                        _imagingMediator,
                        _filterWheelMediator,
                        _plateSolverFactory,
                        _windowServiceFactory);

                    await solveAndSync.Execute(new Progress<NINA.Core.Model.ApplicationStatus>(status =>
                    {
                        if (!string.IsNullOrEmpty(status.Status))
                        {
                            _setOperationStatus($"Centering: {status.Status}", "PlateSolving");
                            _ = _forceStatusUpdate();
                        }
                    }), token);

                    _setOperationStatus("Slew Complete", "Imaging");
                    await _forceStatusUpdate();

                    return CommandResult.Ok($"Slewed and centered on RA {coords.RaHours:F4}h Dec {coords.Dec:F4}°");
                }
                else
                {
                    _setOperationStatus("Slew Complete", "Imaging");
                    await _forceStatusUpdate();
                    return CommandResult.Ok($"Slewed to RA {coords.RaHours:F4}h Dec {coords.Dec:F4}°");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to slew: {ex.Message}");
                _setOperationStatus("Slew Failed", "Imaging");
                return CommandResult.Fail($"Failed to slew: {ex.Message}");
            }
        }

        #endregion

        #region Tracking Commands

        private class TrackingParameters
        {
            public bool Enabled { get; set; }
        }

        private class TrackingRateParameters
        {
            public string? Rate { get; set; }
        }

        private CommandResult ExecuteSetTracking(string? parameters)
        {
            Logger.Info("AstroManager: Executing SET TRACKING");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (!info.CanSetTrackingEnabled)
                    return CommandResult.Fail("Telescope does not support tracking control");

                bool enabled = true;
                if (!string.IsNullOrEmpty(parameters))
                {
                    var trackParams = JsonSerializer.Deserialize<TrackingParameters>(parameters);
                    enabled = trackParams?.Enabled ?? true;
                }

                _telescopeMediator.SetTrackingEnabled(enabled);
                return CommandResult.Ok($"Tracking {(enabled ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to set tracking: {ex.Message}");
                return CommandResult.Fail($"Failed to set tracking: {ex.Message}");
            }
        }

        private CommandResult ExecuteSetTrackingRate(string? parameters)
        {
            Logger.Info("AstroManager: Executing SET TRACKING RATE");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No tracking rate specified");

                var rateParams = JsonSerializer.Deserialize<TrackingRateParameters>(parameters);
                if (rateParams == null || string.IsNullOrEmpty(rateParams.Rate))
                    return CommandResult.Fail("Invalid tracking rate parameters");

                var supportedRates = info.TrackingModes;
                if (supportedRates == null || supportedRates.Count == 0)
                    return CommandResult.Fail("Mount does not report supported tracking modes");

                var matchingMode = supportedRates.FirstOrDefault(m =>
                    m.ToString().Equals(rateParams.Rate, StringComparison.OrdinalIgnoreCase));

                if (matchingMode == null)
                {
                    var availableModes = string.Join(", ", supportedRates.Select(m => m.ToString()));
                    return CommandResult.Fail($"Unknown tracking rate: {rateParams.Rate}. Available: {availableModes}");
                }

                _telescopeMediator.SetTrackingMode(matchingMode);

                if (!info.TrackingEnabled)
                {
                    _telescopeMediator.SetTrackingEnabled(true);
                }

                _ = _forceStatusUpdate();

                return CommandResult.Ok($"Tracking rate set to {matchingMode}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to set tracking rate: {ex.Message}");
                return CommandResult.Fail($"Failed to set tracking rate: {ex.Message}");
            }
        }

        #endregion

        #region Center and Meridian Flip Commands

        private async Task<CommandResult> ExecuteCenterTargetAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing CENTER TARGET");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (!_cameraMediator.GetInfo().Connected)
                    return CommandResult.Fail("Camera is not connected (required for centering)");

                // Get current mount position as target
                var currentRa = info.RightAscension;
                var currentDec = info.Declination;

                _setOperationStatus("Centering: Plate solving...", "PlateSolving");
                await _forceStatusUpdate();

                // Create center command using current position (9-arg constructor)
                var center = new Center(
                    _profileService,
                    _telescopeMediator,
                    _imagingMediator,
                    _filterWheelMediator,
                    _guiderMediator,
                    null, // IDomeMediator - optional
                    null, // IDomeFollower - optional
                    _plateSolverFactory,
                    _windowServiceFactory);

                var raAngle = Angle.ByHours(currentRa);
                var decAngle = Angle.ByDegree(currentDec);
                var targetCoords = new Coordinates(raAngle, decAngle, Epoch.J2000);
                center.Coordinates = new InputCoordinates(targetCoords);

                await center.Execute(new Progress<NINA.Core.Model.ApplicationStatus>(status =>
                {
                    if (!string.IsNullOrEmpty(status.Status))
                    {
                        _setOperationStatus($"Centering: {status.Status}", "PlateSolving");
                        _ = _forceStatusUpdate();
                    }
                }), token);

                _setOperationStatus("Center Complete", "Imaging");
                await _forceStatusUpdate();

                return CommandResult.Ok($"Centered on RA {currentRa:F4}h Dec {currentDec:F4}°");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to center: {ex.Message}");
                _setOperationStatus("Center Failed", "Imaging");
                return CommandResult.Fail($"Failed to center: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteMeridianFlipAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing MERIDIAN FLIP");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                // Check if mount supports flipping
                if (!info.CanSetPierSide)
                    return CommandResult.Fail("Telescope does not support pier side control");

                _setOperationStatus("Executing meridian flip...", "MeridianFlip");
                await _forceStatusUpdate();

                // Get current coordinates
                var currentRa = info.RightAscension;
                var currentDec = info.Declination;

                // Perform meridian flip by flipping pier side
                var currentSide = info.SideOfPier;
                var targetSide = currentSide == NINA.Core.Enum.PierSide.pierEast 
                    ? NINA.Core.Enum.PierSide.pierWest 
                    : NINA.Core.Enum.PierSide.pierEast;

                Logger.Info($"AstroManager: Flipping from {currentSide} to {targetSide}");
                
                // Use NINA's flip method - slew to same coordinates but on opposite pier side
                var result = await _telescopeMediator.MeridianFlip(
                    new Coordinates(Angle.ByHours(currentRa), Angle.ByDegree(currentDec), Epoch.J2000),
                    token);

                if (result)
                {
                    _setOperationStatus("Meridian Flip Complete", "Imaging");
                    await _forceStatusUpdate();
                    return CommandResult.Ok("Meridian flip completed successfully");
                }
                else
                {
                    _setOperationStatus("Meridian Flip Failed", "Imaging");
                    return CommandResult.Fail("Meridian flip failed");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to execute meridian flip: {ex.Message}");
                _setOperationStatus("Meridian Flip Failed", "Imaging");
                return CommandResult.Fail($"Failed to execute meridian flip: {ex.Message}");
            }
        }

        #endregion

        #region Stop Commands

        private CommandResult ExecuteStopMount()
        {
            Logger.Info("AstroManager: Executing STOP MOUNT");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Mount is not connected");

                _telescopeMediator.StopSlew();
                return CommandResult.Ok("Mount slew stopped");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop mount: {ex.Message}");
                return CommandResult.Fail($"Failed to stop mount: {ex.Message}");
            }
        }

        #endregion
    }
}
