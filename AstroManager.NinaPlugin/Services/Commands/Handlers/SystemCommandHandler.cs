using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using Shared.Model.DTO.Client;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles system-level commands: StopAll, EmergencyStop, ReconnectEquipment
    /// </summary>
    public class SystemCommandHandler : ICommandHandler
    {
        private readonly ISequenceMediator _sequenceMediator;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly IFocuserMediator _focuserMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly Action _updateEquipmentStatus;

        public SystemCommandHandler(
            ISequenceMediator sequenceMediator,
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator,
            IGuiderMediator guiderMediator,
            IFilterWheelMediator filterWheelMediator,
            IRotatorMediator rotatorMediator,
            Action updateEquipmentStatus)
        {
            _sequenceMediator = sequenceMediator;
            _telescopeMediator = telescopeMediator;
            _cameraMediator = cameraMediator;
            _focuserMediator = focuserMediator;
            _guiderMediator = guiderMediator;
            _filterWheelMediator = filterWheelMediator;
            _rotatorMediator = rotatorMediator;
            _updateEquipmentStatus = updateEquipmentStatus;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.StopAll => true,
                RemoteCommandType.EmergencyStop => true,
                RemoteCommandType.ReconnectEquipment => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.StopAll => await ExecuteStopAllAsync(),
                RemoteCommandType.EmergencyStop => await ExecuteEmergencyStopAsync(),
                RemoteCommandType.ReconnectEquipment => await ExecuteReconnectEquipmentAsync(token),
                _ => CommandResult.Fail($"Unsupported command: {command.CommandType}")
            };
        }

        private async Task<CommandResult> ExecuteEmergencyStopAsync()
        {
            Logger.Warning("AstroManager: Executing EMERGENCY STOP - halting all operations!");
            var results = new List<string>();

            // 1. Stop sequence
            try
            {
                _sequenceMediator.CancelAdvancedSequence();
                results.Add("Sequence stopped");
            }
            catch (Exception ex)
            {
                results.Add($"Sequence stop failed: {ex.Message}");
            }

            // 2. Stop guiding
            try
            {
                await _guiderMediator.StopGuiding(CancellationToken.None);
                results.Add("Guiding stopped");
            }
            catch (Exception ex)
            {
                results.Add($"Guiding stop failed: {ex.Message}");
            }

            // 3. Stop telescope tracking (if possible without parking)
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (info.Connected && info.CanSetTrackingEnabled)
                {
                    _telescopeMediator.SetTrackingEnabled(false);
                    results.Add("Tracking disabled");
                }
            }
            catch (Exception ex)
            {
                results.Add($"Tracking disable failed: {ex.Message}");
            }

            // 4. Abort any camera exposure
            try
            {
                _cameraMediator.AbortExposure();
                results.Add("Camera exposure aborted");
            }
            catch (Exception ex)
            {
                results.Add($"Camera abort failed: {ex.Message}");
            }

            return CommandResult.Ok($"Emergency stop executed: {string.Join("; ", results)}");
        }

        private async Task<CommandResult> ExecuteStopAllAsync()
        {
            Logger.Info("AstroManager: Executing STOP ALL");
            var results = new List<string>();

            // Stop mount slew
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (info.Connected && info.Slewing)
                {
                    _telescopeMediator.StopSlew();
                    results.Add("Mount slew stopped");
                }
            }
            catch (Exception ex) { results.Add($"Mount: {ex.Message}"); }

            // Stop camera exposure
            try
            {
                _cameraMediator.AbortExposure();
                results.Add("Exposure aborted");
            }
            catch (Exception ex) { results.Add($"Camera: {ex.Message}"); }

            // Stop guiding
            try
            {
                await _guiderMediator.StopGuiding(CancellationToken.None);
                results.Add("Guiding stopped");
            }
            catch (Exception ex) { results.Add($"Guider: {ex.Message}"); }

            // Stop focuser
            try
            {
                var focuserInfo = _focuserMediator.GetInfo();
                if (focuserInfo.Connected && focuserInfo.IsMoving)
                {
                    await _focuserMediator.MoveFocuser(focuserInfo.Position, CancellationToken.None);
                    results.Add("Focuser stopped");
                }
            }
            catch (Exception ex) { results.Add($"Focuser: {ex.Message}"); }

            // Stop rotator
            try
            {
                if (_rotatorMediator != null)
                {
                    var rotatorInfo = _rotatorMediator.GetInfo();
                    if (rotatorInfo.Connected && rotatorInfo.IsMoving)
                    {
                        await _rotatorMediator.MoveMechanical((float)rotatorInfo.MechanicalPosition, CancellationToken.None);
                        results.Add("Rotator stopped");
                    }
                }
            }
            catch (Exception ex) { results.Add($"Rotator: {ex.Message}"); }

            return CommandResult.Ok($"Stop all: {string.Join("; ", results)}");
        }

        private async Task<CommandResult> ExecuteReconnectEquipmentAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing RECONNECT EQUIPMENT");
            var reconnected = new List<string>();
            var failed = new List<string>();

            try
            {
                // Reconnect Camera
                var cameraInfo = _cameraMediator.GetInfo();
                if (!cameraInfo.Connected)
                {
                    try
                    {
                        await _cameraMediator.Rescan();
                        var newInfo = _cameraMediator.GetInfo();
                        if (newInfo.Connected) reconnected.Add("Camera");
                        else failed.Add("Camera");
                    }
                    catch { failed.Add("Camera"); }
                }

                // Reconnect Telescope/Mount
                var telescopeInfo = _telescopeMediator.GetInfo();
                if (!telescopeInfo.Connected)
                {
                    try
                    {
                        await _telescopeMediator.Rescan();
                        var newInfo = _telescopeMediator.GetInfo();
                        if (newInfo.Connected) reconnected.Add("Mount");
                        else failed.Add("Mount");
                    }
                    catch { failed.Add("Mount"); }
                }

                // Reconnect Focuser
                var focuserInfo = _focuserMediator.GetInfo();
                if (!focuserInfo.Connected)
                {
                    try
                    {
                        await _focuserMediator.Rescan();
                        var newInfo = _focuserMediator.GetInfo();
                        if (newInfo.Connected) reconnected.Add("Focuser");
                        else failed.Add("Focuser");
                    }
                    catch { failed.Add("Focuser"); }
                }

                // Reconnect Filter Wheel
                var filterWheelInfo = _filterWheelMediator.GetInfo();
                if (!filterWheelInfo.Connected)
                {
                    try
                    {
                        await _filterWheelMediator.Rescan();
                        var newInfo = _filterWheelMediator.GetInfo();
                        if (newInfo.Connected) reconnected.Add("Filter Wheel");
                        else failed.Add("Filter Wheel");
                    }
                    catch { failed.Add("Filter Wheel"); }
                }

                // Reconnect Guider
                var guiderInfo = _guiderMediator.GetInfo();
                if (!guiderInfo.Connected)
                {
                    try
                    {
                        await _guiderMediator.Rescan();
                        var newInfo = _guiderMediator.GetInfo();
                        if (newInfo.Connected) reconnected.Add("Guider");
                        else failed.Add("Guider");
                    }
                    catch { failed.Add("Guider"); }
                }

                // Update equipment status after reconnect attempt
                _updateEquipmentStatus?.Invoke();

                if (failed.Count == 0 && reconnected.Count == 0)
                {
                    return CommandResult.Ok("All equipment already connected");
                }
                else if (failed.Count == 0)
                {
                    return CommandResult.Ok($"Reconnected: {string.Join(", ", reconnected)}");
                }
                else if (reconnected.Count == 0)
                {
                    return CommandResult.Fail($"Failed to reconnect: {string.Join(", ", failed)}");
                }
                else
                {
                    return CommandResult.Ok($"Reconnected: {string.Join(", ", reconnected)}. Failed: {string.Join(", ", failed)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to reconnect equipment: {ex.Message}");
                return CommandResult.Fail($"Failed to reconnect equipment: {ex.Message}");
            }
        }

    }
}
