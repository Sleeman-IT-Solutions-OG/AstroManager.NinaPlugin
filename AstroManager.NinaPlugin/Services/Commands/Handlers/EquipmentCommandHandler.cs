using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using Shared.Model.DTO.Client;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles equipment-related commands: Connect, Disconnect, Park, Unpark, Cool, Warm
    /// </summary>
    public class EquipmentCommandHandler : ICommandHandler
    {
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly IFocuserMediator _focuserMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IFlatDeviceMediator _flatDeviceMediator;
        private readonly ISafetyMonitorMediator _safetyMonitorMediator;
        private readonly IWeatherDataMediator _weatherDataMediator;
        private readonly Action _updateEquipmentStatus;

        public EquipmentCommandHandler(
            ITelescopeMediator telescopeMediator,
            ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator,
            IFilterWheelMediator filterWheelMediator,
            IGuiderMediator guiderMediator,
            IRotatorMediator rotatorMediator,
            IFlatDeviceMediator flatDeviceMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            IWeatherDataMediator weatherDataMediator,
            Action updateEquipmentStatus)
        {
            _telescopeMediator = telescopeMediator;
            _cameraMediator = cameraMediator;
            _focuserMediator = focuserMediator;
            _filterWheelMediator = filterWheelMediator;
            _guiderMediator = guiderMediator;
            _rotatorMediator = rotatorMediator;
            _flatDeviceMediator = flatDeviceMediator;
            _safetyMonitorMediator = safetyMonitorMediator;
            _weatherDataMediator = weatherDataMediator;
            _updateEquipmentStatus = updateEquipmentStatus;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.ConnectEquipment => true,
                RemoteCommandType.DisconnectEquipment => true,
                RemoteCommandType.ConnectAllEquipment => true,
                RemoteCommandType.DisconnectAllEquipment => true,
                RemoteCommandType.ParkTelescope => true,
                RemoteCommandType.UnparkTelescope => true,
                RemoteCommandType.HomeTelescope => true,
                RemoteCommandType.WarmCamera => true,
                RemoteCommandType.CoolCamera => true,
                RemoteCommandType.TurnOffCooler => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.ConnectEquipment => await ExecuteConnectEquipmentAsync(command.Parameters, token),
                RemoteCommandType.DisconnectEquipment => await ExecuteDisconnectEquipmentAsync(command.Parameters),
                RemoteCommandType.ConnectAllEquipment => await ExecuteConnectAllEquipmentAsync(token),
                RemoteCommandType.DisconnectAllEquipment => ExecuteDisconnectAllEquipment(),
                RemoteCommandType.ParkTelescope => await ExecuteParkTelescopeAsync(token),
                RemoteCommandType.UnparkTelescope => await ExecuteUnparkTelescopeAsync(token),
                RemoteCommandType.HomeTelescope => await ExecuteHomeTelescopeAsync(token),
                RemoteCommandType.WarmCamera => ExecuteWarmCamera(),
                RemoteCommandType.CoolCamera => ExecuteCoolCamera(command.Parameters),
                RemoteCommandType.TurnOffCooler => ExecuteTurnOffCooler(),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        #region Telescope Commands

        private async Task<CommandResult> ExecuteParkTelescopeAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing PARK TELESCOPE");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (!info.CanPark)
                    return CommandResult.Fail("Telescope does not support parking");

                await _telescopeMediator.ParkTelescope(null, token);
                return CommandResult.Ok("Telescope parked successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to park telescope: {ex.Message}");
                return CommandResult.Fail($"Failed to park telescope: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteUnparkTelescopeAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing UNPARK TELESCOPE");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (!info.CanPark)
                    return CommandResult.Fail("Telescope does not support parking/unparking");

                await _telescopeMediator.UnparkTelescope(null, token);
                return CommandResult.Ok("Telescope unparked successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to unpark telescope: {ex.Message}");
                return CommandResult.Fail($"Failed to unpark telescope: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteHomeTelescopeAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing HOME TELESCOPE");
            try
            {
                var info = _telescopeMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Telescope is not connected");

                if (!info.CanFindHome)
                    return CommandResult.Fail("Telescope does not support homing");

                await _telescopeMediator.FindHome(null, token);
                return CommandResult.Ok("Telescope homing started");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to home telescope: {ex.Message}");
                return CommandResult.Fail($"Failed to home telescope: {ex.Message}");
            }
        }

        #endregion

        #region Camera Commands

        private CommandResult ExecuteWarmCamera()
        {
            Logger.Info("AstroManager: Executing WARM CAMERA");
            try
            {
                var info = _cameraMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Camera is not connected");

                if (!info.CanSetTemperature)
                    return CommandResult.Fail("Camera does not support temperature control");

                var cameraDevice = _cameraMediator.GetDevice() as NINA.Equipment.Interfaces.ICamera;
                if (cameraDevice != null)
                {
                    cameraDevice.CoolerOn = false;
                    Logger.Info("AstroManager: Camera cooler turned off - warming to ambient");
                    return CommandResult.Ok("Camera cooler off - warming to ambient");
                }

                return CommandResult.Fail("Could not access camera device");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to warm camera: {ex.Message}");
                return CommandResult.Fail($"Failed to warm camera: {ex.Message}");
            }
        }

        private CommandResult ExecuteTurnOffCooler()
        {
            Logger.Info("AstroManager: Executing TURN OFF COOLER");
            try
            {
                var info = _cameraMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Camera is not connected");

                if (!info.CanSetTemperature)
                    return CommandResult.Fail("Camera does not support cooling control");

                var cameraDevice = _cameraMediator.GetDevice() as NINA.Equipment.Interfaces.ICamera;
                if (cameraDevice != null)
                {
                    cameraDevice.CoolerOn = false;
                    Logger.Info("AstroManager: Camera cooler turned off");
                    return CommandResult.Ok("Camera cooler turned off");
                }

                return CommandResult.Fail("Could not access camera device");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to turn off cooler: {ex.Message}");
                return CommandResult.Fail($"Failed to turn off cooler: {ex.Message}");
            }
        }

        private CommandResult ExecuteCoolCamera(string? parameters)
        {
            Logger.Info("AstroManager: Executing COOL CAMERA");
            try
            {
                var info = _cameraMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Camera is not connected");

                if (!info.CanSetTemperature)
                    return CommandResult.Fail("Camera does not support temperature control");

                double targetTemp = -10;
                if (!string.IsNullOrEmpty(parameters))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(parameters);
                        if (doc.RootElement.TryGetProperty("TargetTemperature", out var tempProp) ||
                            doc.RootElement.TryGetProperty("targetTemperature", out tempProp))
                        {
                            targetTemp = tempProp.GetDouble();
                        }
                    }
                    catch
                    {
                        if (double.TryParse(parameters, out var parsedTemp))
                            targetTemp = parsedTemp;
                    }
                }

                var cameraDevice = _cameraMediator.GetDevice() as NINA.Equipment.Interfaces.ICamera;
                if (cameraDevice != null)
                {
                    cameraDevice.CoolerOn = true;
                    cameraDevice.TemperatureSetPoint = targetTemp;
                    Logger.Info($"AstroManager: Camera cooler enabled, target temperature set to {targetTemp}°C");
                    return CommandResult.Ok($"Camera cooling to {targetTemp}°C");
                }

                return CommandResult.Fail("Could not access camera device");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to cool camera: {ex.Message}");
                return CommandResult.Fail($"Failed to cool camera: {ex.Message}");
            }
        }

        #endregion

        #region Connect/Disconnect Commands

        private class EquipmentParameters
        {
            public string? Equipment { get; set; }
        }

        private async Task<CommandResult> ExecuteConnectEquipmentAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing CONNECT EQUIPMENT");
            try
            {
                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No equipment specified");

                var equipParams = JsonSerializer.Deserialize<EquipmentParameters>(parameters);
                if (equipParams == null || string.IsNullOrEmpty(equipParams.Equipment))
                    return CommandResult.Fail("Invalid equipment parameters");

                var result = equipParams.Equipment.ToLower() switch
                {
                    "camera" => await ConnectCameraAsync(),
                    "telescope" or "mount" => await ConnectTelescopeAsync(),
                    "focuser" => await ConnectFocuserAsync(),
                    "filterwheel" or "filter" => await ConnectFilterWheelAsync(),
                    "guider" => await ConnectGuiderAsync(),
                    "rotator" => await ConnectRotatorAsync(),
                    "flatpanel" or "flatdevice" => await ConnectFlatPanelAsync(),
                    "safetymonitor" or "safety" => await ConnectSafetyMonitorAsync(),
                    "weather" => await ConnectWeatherAsync(),
                    _ => CommandResult.Fail($"Unknown equipment: {equipParams.Equipment}")
                };

                ScheduleStatusUpdates();
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to connect equipment: {ex.Message}");
                return CommandResult.Fail($"Failed to connect: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteDisconnectEquipmentAsync(string? parameters)
        {
            Logger.Info("AstroManager: Executing DISCONNECT EQUIPMENT");
            try
            {
                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No equipment specified");

                var equipParams = JsonSerializer.Deserialize<EquipmentParameters>(parameters);
                if (equipParams == null || string.IsNullOrEmpty(equipParams.Equipment))
                    return CommandResult.Fail("Invalid equipment parameters");

                var result = equipParams.Equipment.ToLower() switch
                {
                    "camera" => DisconnectCamera(),
                    "telescope" or "mount" => DisconnectTelescope(),
                    "focuser" => DisconnectFocuser(),
                    "filterwheel" or "filter" => DisconnectFilterWheel(),
                    "guider" => DisconnectGuider(),
                    "rotator" => DisconnectRotator(),
                    "flatpanel" or "flatdevice" => DisconnectFlatPanel(),
                    "safetymonitor" or "safety" => DisconnectSafetyMonitor(),
                    "weather" => DisconnectWeather(),
                    _ => CommandResult.Fail($"Unknown equipment: {equipParams.Equipment}")
                };

                ScheduleStatusUpdates();
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to disconnect equipment: {ex.Message}");
                return CommandResult.Fail($"Failed to disconnect: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteConnectAllEquipmentAsync(CancellationToken token)
        {
            Logger.Info("AstroManager: Executing CONNECT ALL EQUIPMENT");
            var connected = new List<string>();
            var failed = new List<string>();

            // Connect in order: Camera, Telescope, Focuser, Filter Wheel, Rotator, Guider
            var connectTasks = new (string Name, Func<Task<CommandResult>> Connect)[]
            {
                ("Camera", ConnectCameraAsync),
                ("Telescope", ConnectTelescopeAsync),
                ("Focuser", ConnectFocuserAsync),
                ("FilterWheel", ConnectFilterWheelAsync),
                ("Rotator", ConnectRotatorAsync),
                ("Guider", ConnectGuiderAsync),
                ("FlatPanel", ConnectFlatPanelAsync),
                ("SafetyMonitor", ConnectSafetyMonitorAsync),
                ("Weather", ConnectWeatherAsync)
            };

            foreach (var (name, connect) in connectTasks)
            {
                try
                {
                    var result = await connect();
                    if (result.Success)
                        connected.Add(name);
                    else if (!result.Message.Contains("already connected"))
                        failed.Add($"{name}: {result.Message}");
                }
                catch (Exception ex)
                {
                    failed.Add($"{name}: {ex.Message}");
                }
            }

            ScheduleStatusUpdates();

            if (failed.Count == 0)
                return CommandResult.Ok($"Connected: {string.Join(", ", connected)}");
            if (connected.Count == 0)
                return CommandResult.Fail($"Failed to connect: {string.Join("; ", failed)}");

            return CommandResult.Ok($"Connected: {string.Join(", ", connected)}. Failed: {string.Join("; ", failed)}");
        }

        private CommandResult ExecuteDisconnectAllEquipment()
        {
            Logger.Info("AstroManager: Executing DISCONNECT ALL EQUIPMENT");

            _guiderMediator.Disconnect();
            _rotatorMediator?.Disconnect();
            _filterWheelMediator.Disconnect();
            _focuserMediator.Disconnect();
            _cameraMediator.Disconnect();
            _telescopeMediator.Disconnect();
            _flatDeviceMediator?.Disconnect();
            _safetyMonitorMediator?.Disconnect();
            _weatherDataMediator?.Disconnect();

            ScheduleStatusUpdates();
            return CommandResult.Ok("All equipment disconnected");
        }

        private void ScheduleStatusUpdates()
        {
            _updateEquipmentStatus();
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                _updateEquipmentStatus();
                await Task.Delay(1500);
                _updateEquipmentStatus();
            });
        }

        #endregion

        #region Individual Connect Methods

        private async Task<CommandResult> ConnectCameraAsync()
        {
            var info = _cameraMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Camera already connected");

            var result = await _cameraMediator.Connect();
            return result 
                ? CommandResult.Ok("Camera connected") 
                : CommandResult.Fail("Failed to connect camera - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectTelescopeAsync()
        {
            var info = _telescopeMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Telescope already connected");

            var result = await _telescopeMediator.Connect();
            return result 
                ? CommandResult.Ok("Telescope connected") 
                : CommandResult.Fail("Failed to connect telescope - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectFocuserAsync()
        {
            var info = _focuserMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Focuser already connected");

            var result = await _focuserMediator.Connect();
            return result 
                ? CommandResult.Ok("Focuser connected") 
                : CommandResult.Fail("Failed to connect focuser - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectFilterWheelAsync()
        {
            var info = _filterWheelMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Filter wheel already connected");

            var result = await _filterWheelMediator.Connect();
            return result 
                ? CommandResult.Ok("Filter wheel connected") 
                : CommandResult.Fail("Failed to connect filter wheel - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectGuiderAsync()
        {
            var info = _guiderMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Guider already connected");

            var result = await _guiderMediator.Connect();
            return result 
                ? CommandResult.Ok("Guider connected") 
                : CommandResult.Fail("Failed to connect guider - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectRotatorAsync()
        {
            if (_rotatorMediator == null) return CommandResult.Fail("Rotator not available");

            var info = _rotatorMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Rotator already connected");

            var result = await _rotatorMediator.Connect();
            return result 
                ? CommandResult.Ok("Rotator connected") 
                : CommandResult.Fail("Failed to connect rotator - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectFlatPanelAsync()
        {
            if (_flatDeviceMediator == null) return CommandResult.Fail("Flat panel not available");

            var info = _flatDeviceMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Flat panel already connected");

            var result = await _flatDeviceMediator.Connect();
            return result 
                ? CommandResult.Ok("Flat panel connected") 
                : CommandResult.Fail("Failed to connect flat panel - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectSafetyMonitorAsync()
        {
            if (_safetyMonitorMediator == null) return CommandResult.Fail("Safety monitor not available");

            var info = _safetyMonitorMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Safety monitor already connected");

            var result = await _safetyMonitorMediator.Connect();
            return result 
                ? CommandResult.Ok("Safety monitor connected") 
                : CommandResult.Fail("Failed to connect safety monitor - check NINA equipment profile");
        }

        private async Task<CommandResult> ConnectWeatherAsync()
        {
            if (_weatherDataMediator == null) return CommandResult.Fail("Weather device not available");

            var info = _weatherDataMediator.GetInfo();
            if (info.Connected) return CommandResult.Ok("Weather device already connected");

            var result = await _weatherDataMediator.Connect();
            return result 
                ? CommandResult.Ok("Weather device connected") 
                : CommandResult.Fail("Failed to connect weather device - check NINA equipment profile");
        }

        #endregion

        #region Individual Disconnect Methods

        private CommandResult DisconnectCamera()
        {
            _cameraMediator.Disconnect();
            return CommandResult.Ok("Camera disconnected");
        }

        private CommandResult DisconnectTelescope()
        {
            _telescopeMediator.Disconnect();
            return CommandResult.Ok("Telescope disconnected");
        }

        private CommandResult DisconnectFocuser()
        {
            _focuserMediator.Disconnect();
            return CommandResult.Ok("Focuser disconnected");
        }

        private CommandResult DisconnectFilterWheel()
        {
            _filterWheelMediator.Disconnect();
            return CommandResult.Ok("Filter wheel disconnected");
        }

        private CommandResult DisconnectGuider()
        {
            _guiderMediator.Disconnect();
            return CommandResult.Ok("Guider disconnected");
        }

        private CommandResult DisconnectRotator()
        {
            if (_rotatorMediator == null) return CommandResult.Fail("Rotator not available");
            _rotatorMediator.Disconnect();
            return CommandResult.Ok("Rotator disconnected");
        }

        private CommandResult DisconnectFlatPanel()
        {
            if (_flatDeviceMediator == null) return CommandResult.Fail("Flat panel not available");
            _flatDeviceMediator.Disconnect();
            return CommandResult.Ok("Flat panel disconnected");
        }

        private CommandResult DisconnectSafetyMonitor()
        {
            if (_safetyMonitorMediator == null) return CommandResult.Fail("Safety monitor not available");
            _safetyMonitorMediator.Disconnect();
            return CommandResult.Ok("Safety monitor disconnected");
        }

        private CommandResult DisconnectWeather()
        {
            if (_weatherDataMediator == null) return CommandResult.Fail("Weather device not available");
            _weatherDataMediator.Disconnect();
            return CommandResult.Ok("Weather device disconnected");
        }

        #endregion
    }
}
