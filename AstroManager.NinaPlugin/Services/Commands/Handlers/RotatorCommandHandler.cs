using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Shared.Model.DTO.Client;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles rotator-related commands: MoveRotator, StopRotator, ReverseRotator
    /// </summary>
    public class RotatorCommandHandler : ICommandHandler
    {
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IProfileService _profileService;
        private readonly object _rotatorMoveLock = new();
        private CancellationTokenSource? _rotatorMoveCts;

        public RotatorCommandHandler(
            IRotatorMediator rotatorMediator,
            IProfileService profileService)
        {
            _rotatorMediator = rotatorMediator;
            _profileService = profileService;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.MoveRotator => true,
                RemoteCommandType.StopRotator => true,
                RemoteCommandType.ReverseRotator => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.MoveRotator => await ExecuteMoveRotatorAsync(command.Parameters, token),
                RemoteCommandType.StopRotator => ExecuteStopRotator(),
                RemoteCommandType.ReverseRotator => ExecuteReverseRotator(command.Parameters),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        #region Parameter Classes

        private class RotatorParameters
        {
            public double Angle { get; set; }
        }

        private class ReverseRotatorParameters
        {
            public bool? Reverse { get; set; }
        }

        #endregion

        #region Move Commands

        private async Task<CommandResult> ExecuteMoveRotatorAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing MOVE ROTATOR");
            try
            {
                if (_rotatorMediator == null)
                    return CommandResult.Fail("Rotator not available");

                var info = _rotatorMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Rotator is not connected");

                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No angle specified");

                var rotatorParams = JsonSerializer.Deserialize<RotatorParameters>(parameters);
                if (rotatorParams == null)
                    return CommandResult.Fail("Invalid rotator parameters");

                CancellationTokenSource localCts;
                CancellationTokenSource? oldCts;
                lock (_rotatorMoveLock)
                {
                    oldCts = _rotatorMoveCts;
                    _rotatorMoveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    localCts = _rotatorMoveCts;
                }
                try { oldCts?.Cancel(); } catch { }

                try
                {
                    await _rotatorMediator.MoveMechanical((float)rotatorParams.Angle, localCts.Token);
                    return CommandResult.Ok($"Rotator moved to {rotatorParams.Angle:F1}°");
                }
                finally
                {
                    lock (_rotatorMoveLock)
                    {
                        if (ReferenceEquals(_rotatorMoveCts, localCts))
                            _rotatorMoveCts = null;
                    }
                    localCts.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("AstroManager: Rotator move cancelled");
                return CommandResult.Fail("Rotator move cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to move rotator: {ex.Message}");
                return CommandResult.Fail($"Failed to move rotator: {ex.Message}");
            }
        }

        #endregion

        #region Stop Commands

        private CommandResult ExecuteStopRotator()
        {
            Logger.Info("AstroManager: Executing STOP ROTATOR");
            try
            {
                if (_rotatorMediator == null)
                    return CommandResult.Fail("Rotator not available");

                var info = _rotatorMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Rotator is not connected");

                CancellationTokenSource? ctsToCancel;
                lock (_rotatorMoveLock)
                {
                    ctsToCancel = _rotatorMoveCts;
                    _rotatorMoveCts = null;
                }
                try { ctsToCancel?.Cancel(); } catch { }

                _ = TryHaltRotatorAsync();

                return CommandResult.Ok("Rotator stop requested");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop rotator: {ex.Message}");
                return CommandResult.Fail($"Failed to stop rotator: {ex.Message}");
            }
        }

        private async Task TryHaltRotatorAsync()
        {
            try
            {
                if (_rotatorMediator == null) return;

                var t = _rotatorMediator.GetType();
                var method = t.GetMethod("Halt") ?? t.GetMethod("Stop") ?? t.GetMethod("Abort") ?? t.GetMethod("AbortMove") ?? t.GetMethod("StopMove");
                if (method != null)
                {
                    method.Invoke(_rotatorMediator, Array.Empty<object>());
                    Logger.Info($"AstroManager: Rotator halt invoked via {method.Name}()");
                    return;
                }

                var info = _rotatorMediator.GetInfo();
                if (info.Connected && info.IsMoving)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _rotatorMediator.MoveMechanical((float)info.MechanicalPosition, CancellationToken.None);
                        }
                        catch { }
                    });
                    Logger.Info("AstroManager: Rotator halt fallback requested (move to current position in background)");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Rotator halt attempt failed: {ex.Message}");
            }
        }

        #endregion

        #region Configuration Commands

        private CommandResult ExecuteReverseRotator(string? parameters)
        {
            Logger.Info("AstroManager: Executing SET ROTATOR REVERSE");
            try
            {
                if (_rotatorMediator == null)
                    return CommandResult.Fail("Rotator not available");

                var info = _rotatorMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Rotator is not connected");

                bool? reverseValue = null;
                if (!string.IsNullOrEmpty(parameters))
                {
                    try
                    {
                        var reverseParams = JsonSerializer.Deserialize<ReverseRotatorParameters>(parameters);
                        reverseValue = reverseParams?.Reverse;
                    }
                    catch { }
                }

                var rotatorSettings = _profileService?.ActiveProfile?.RotatorSettings;
                if (rotatorSettings != null)
                {
                    var settingsType = rotatorSettings.GetType();
                    var allProps = settingsType.GetProperties();
                    Logger.Info($"AstroManager: RotatorSettings type: {settingsType.FullName}, properties: {string.Join(", ", allProps.Select(p => p.Name))}");

                    var reverseProperty = settingsType.GetProperty("Reverse") ?? settingsType.GetProperty("IsReversed") ?? settingsType.GetProperty("Reversed");
                    if (reverseProperty != null)
                    {
                        Logger.Info($"AstroManager: Found reverse property: {reverseProperty.Name}, CanRead={reverseProperty.CanRead}, CanWrite={reverseProperty.CanWrite}");

                        if (reverseProperty.CanWrite)
                        {
                            var currentValue = (bool)(reverseProperty.GetValue(rotatorSettings) ?? false);
                            var newValue = reverseValue ?? !currentValue;
                            Logger.Info($"AstroManager: Setting rotator reverse from {currentValue} to {newValue}");
                            reverseProperty.SetValue(rotatorSettings, newValue);

                            var verifyValue = (bool)(reverseProperty.GetValue(rotatorSettings) ?? false);
                            Logger.Info($"AstroManager: Rotator reverse verified as: {verifyValue}");

                            if (verifyValue == newValue)
                            {
                                return CommandResult.Ok($"Rotator reverse set to {(newValue ? "ON" : "OFF")}");
                            }
                            else
                            {
                                return CommandResult.Fail($"Failed to apply reverse setting (set {newValue} but got {verifyValue})");
                            }
                        }
                        else
                        {
                            Logger.Warning($"AstroManager: Reverse property {reverseProperty.Name} is not writable");
                        }
                    }
                    else
                    {
                        Logger.Warning("AstroManager: No Reverse/IsReversed/Reversed property found on RotatorSettings");
                    }
                }
                else
                {
                    Logger.Warning("AstroManager: RotatorSettings is null");
                }

                return CommandResult.Fail("Rotator reverse setting not accessible");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to set rotator reverse: {ex.Message}");
                return CommandResult.Fail($"Failed to set rotator reverse: {ex.Message}");
            }
        }

        #endregion
    }
}
