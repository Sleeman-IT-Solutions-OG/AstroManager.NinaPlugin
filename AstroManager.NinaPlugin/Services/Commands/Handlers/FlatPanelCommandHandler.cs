using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using Shared.Model.DTO.Client;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles flat panel/flat device commands: Toggle, SetBrightness, OpenCloseCover
    /// </summary>
    public class FlatPanelCommandHandler : ICommandHandler
    {
        private readonly IFlatDeviceMediator _flatDeviceMediator;

        public FlatPanelCommandHandler(IFlatDeviceMediator flatDeviceMediator)
        {
            _flatDeviceMediator = flatDeviceMediator;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.ToggleFlatPanel => true,
                RemoteCommandType.SetFlatPanelBrightness => true,
                RemoteCommandType.OpenCloseFlatPanelCover => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.ToggleFlatPanel => await ExecuteToggleFlatPanelAsync(command.Parameters, token),
                RemoteCommandType.SetFlatPanelBrightness => await ExecuteSetFlatPanelBrightnessAsync(command.Parameters, token),
                RemoteCommandType.OpenCloseFlatPanelCover => await ExecuteOpenCloseFlatPanelCoverAsync(command.Parameters, token),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        #region Parameter Classes

        private class ToggleFlatPanelParameters
        {
            public bool On { get; set; }
        }

        private class SetBrightnessParameters
        {
            public int Brightness { get; set; }
        }

        private class OpenCloseCoverParameters
        {
            public bool Open { get; set; }
        }

        #endregion

        #region Commands

        private async Task<CommandResult> ExecuteToggleFlatPanelAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing TOGGLE FLAT PANEL");
            try
            {
                if (_flatDeviceMediator == null)
                    return CommandResult.Fail("Flat panel not available");

                var info = _flatDeviceMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Flat panel is not connected");

                bool? turnOn = null;
                if (!string.IsNullOrEmpty(parameters))
                {
                    try
                    {
                        var toggleParams = JsonSerializer.Deserialize<ToggleFlatPanelParameters>(parameters);
                        turnOn = toggleParams?.On;
                    }
                    catch { }
                }

                var currentlyOn = info.LightOn;
                var newState = turnOn ?? !currentlyOn;

                Logger.Info($"AstroManager: Flat panel light currently {(currentlyOn ? "ON" : "OFF")}, setting to {(newState ? "ON" : "OFF")}");

                if (newState)
                {
                    await _flatDeviceMediator.SetBrightness(info.Brightness > 0 ? info.Brightness : 100, null, token);
                }
                else
                {
                    await _flatDeviceMediator.SetBrightness(0, null, token);
                }

                return CommandResult.Ok($"Flat panel light turned {(newState ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to toggle flat panel: {ex.Message}");
                return CommandResult.Fail($"Failed to toggle flat panel: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteSetFlatPanelBrightnessAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing SET FLAT PANEL BRIGHTNESS");
            try
            {
                if (_flatDeviceMediator == null)
                    return CommandResult.Fail("Flat panel not available");

                var info = _flatDeviceMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Flat panel is not connected");

                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No brightness value specified");

                var brightnessParams = JsonSerializer.Deserialize<SetBrightnessParameters>(parameters);
                if (brightnessParams == null)
                    return CommandResult.Fail("Invalid brightness parameters");

                var brightness = Math.Clamp(brightnessParams.Brightness, 0, info.MaxBrightness > 0 ? info.MaxBrightness : 255);
                Logger.Info($"AstroManager: Setting flat panel brightness to {brightness}");

                await _flatDeviceMediator.SetBrightness(brightness, null, token);

                return CommandResult.Ok($"Flat panel brightness set to {brightness}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to set flat panel brightness: {ex.Message}");
                return CommandResult.Fail($"Failed to set brightness: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteOpenCloseFlatPanelCoverAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing OPEN/CLOSE FLAT PANEL COVER");
            try
            {
                if (_flatDeviceMediator == null)
                    return CommandResult.Fail("Flat panel not available");

                var info = _flatDeviceMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Flat panel is not connected");

                if (!info.SupportsOpenClose)
                    return CommandResult.Fail("Flat panel does not support open/close");

                bool open = true;
                if (!string.IsNullOrEmpty(parameters))
                {
                    try
                    {
                        var coverParams = JsonSerializer.Deserialize<OpenCloseCoverParameters>(parameters);
                        if (coverParams != null)
                            open = coverParams.Open;
                    }
                    catch { }
                }

                Logger.Info($"AstroManager: {(open ? "Opening" : "Closing")} flat panel cover");

                if (open)
                {
                    await _flatDeviceMediator.OpenCover(null, token);
                }
                else
                {
                    await _flatDeviceMediator.CloseCover(null, token);
                }

                return CommandResult.Ok($"Flat panel cover {(open ? "opened" : "closed")}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to open/close flat panel cover: {ex.Message}");
                return CommandResult.Fail($"Failed to open/close cover: {ex.Message}");
            }
        }

        #endregion
    }
}
