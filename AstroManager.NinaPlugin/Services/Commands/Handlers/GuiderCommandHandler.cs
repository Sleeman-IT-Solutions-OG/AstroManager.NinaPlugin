using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using Shared.Model.DTO.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles guider-related commands: Start, Stop, Calibrate
    /// </summary>
    public class GuiderCommandHandler : ICommandHandler
    {
        private readonly IGuiderMediator _guiderMediator;
        private readonly HeartbeatService _heartbeatService;
        private bool _isCalibrating;

        public GuiderCommandHandler(IGuiderMediator guiderMediator, HeartbeatService heartbeatService)
        {
            _guiderMediator = guiderMediator;
            _heartbeatService = heartbeatService;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.StartGuiding => true,
                RemoteCommandType.StopGuiding => true,
                RemoteCommandType.CalibrateGuider => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.StartGuiding => await ExecuteStartGuidingAsync(token),
                RemoteCommandType.StopGuiding => await ExecuteStopGuidingAsync(token),
                RemoteCommandType.CalibrateGuider => await ExecuteCalibrateGuiderAsync(token),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        private async Task<CommandResult> ExecuteStartGuidingAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing START GUIDING");
            try
            {
                var info = _guiderMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Guider is not connected");

                var result = await _guiderMediator.StartGuiding(false, null, token);
                return result
                    ? CommandResult.Ok("Guiding started successfully")
                    : CommandResult.Fail("Failed to start guiding");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to start guiding: {ex.Message}");
                return CommandResult.Fail($"Failed to start guiding: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteStopGuidingAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing STOP GUIDING");
            try
            {
                var info = _guiderMediator.GetInfo();
                var isGuiding = info.RMSError?.Total?.Arcseconds > 0;
                Logger.Info($"AstroManager: Guider state - Connected={info.Connected}, IsGuiding={isGuiding}, RMS={info.RMSError?.Total?.Arcseconds:F2}\"");

                if (!info.Connected)
                    return CommandResult.Fail("Guider is not connected");

                if (!isGuiding)
                {
                    Logger.Info("AstroManager: Guider is not currently guiding (RMS=0)");
                    return CommandResult.Ok("Guider was not actively guiding");
                }

                await _guiderMediator.StopGuiding(token);

                await Task.Delay(500, token);
                var infoAfter = _guiderMediator.GetInfo();
                var stillGuiding = infoAfter.RMSError?.Total?.Arcseconds > 0;
                Logger.Info($"AstroManager: Guider state after stop - IsGuiding={stillGuiding}, RMS={infoAfter.RMSError?.Total?.Arcseconds:F2}\"");

                if (stillGuiding)
                    return CommandResult.Fail($"StopGuiding called but guider still has RMS={infoAfter.RMSError?.Total?.Arcseconds:F2}\"");

                return CommandResult.Ok("Guiding stopped successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop guiding: {ex.Message}");
                return CommandResult.Fail($"Failed to stop guiding: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteCalibrateGuiderAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing CALIBRATE GUIDER command");
            
            // Check if scheduler is running using both SharedSchedulerState AND HeartbeatService
            // HeartbeatService.IsActivelyImaging is a fallback check when SharedSchedulerState might be out of sync
            var isSchedulerRunning = SharedSchedulerState.Instance.IsSchedulerRunning || _heartbeatService.IsActivelyImaging;
            Logger.Debug($"AstroManager: Scheduler running check - SharedState={SharedSchedulerState.Instance.IsSchedulerRunning}, HeartbeatActive={_heartbeatService.IsActivelyImaging}, Combined={isSchedulerRunning}");
            
            // If scheduler is running, queue the calibration request instead of executing immediately
            // The scheduler will run calibration after the current exposure completes
            if (isSchedulerRunning)
            {
                if (SharedSchedulerState.Instance.PendingGuiderCalibration)
                {
                    Logger.Info("AstroManager: Guider calibration already queued, ignoring duplicate request");
                    return CommandResult.Ok("Guider calibration already queued - will run after current exposure");
                }
                
                // Force the SharedSchedulerState flag to true if heartbeat shows we're imaging
                // This ensures QueueGuiderCalibration will work
                if (!SharedSchedulerState.Instance.IsSchedulerRunning && _heartbeatService.IsActivelyImaging)
                {
                    Logger.Warning("AstroManager: SharedSchedulerState.IsSchedulerRunning was false but HeartbeatService shows active imaging - syncing state");
                    SharedSchedulerState.Instance.SetSchedulerRunning(true);
                }
                
                var queued = SharedSchedulerState.Instance.QueueGuiderCalibration();
                if (queued)
                {
                    Logger.Info("AstroManager: Scheduler is running - guider calibration queued to run after current exposure completes");
                    return CommandResult.Ok("Guider calibration queued - will run after current exposure completes");
                }
            }
            
            // Scheduler not running - execute calibration immediately
            Logger.Info("AstroManager: Executing CALIBRATE GUIDER immediately (scheduler not running)");
            
            try
            {
                var info = _guiderMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Guider is not connected");

                // Stop guiding first if active
                if (info.Connected)
                {
                    await _guiderMediator.StopGuiding(token);
                }

                _isCalibrating = true;

                try
                {
                    // Start calibration - forceCalibration=true forces a new calibration
                    await _guiderMediator.StartGuiding(true, null, token);
                    return CommandResult.Ok("Guider calibration started");
                }
                finally
                {
                    _isCalibrating = false;
                }
            }
            catch (Exception ex)
            {
                _isCalibrating = false;
                Logger.Error($"AstroManager: Failed to calibrate guider: {ex.Message}");
                return CommandResult.Fail($"Failed to calibrate guider: {ex.Message}");
            }
        }

        public bool IsCalibrating => _isCalibrating;
    }
}
