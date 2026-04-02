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
    /// Handles camera and filter wheel commands: StopExposure, ChangeFilter
    /// </summary>
    public class CameraCommandHandler : ICommandHandler
    {
        private readonly ICameraMediator _cameraMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IProfileService _profileService;

        public CameraCommandHandler(
            ICameraMediator cameraMediator,
            IFilterWheelMediator filterWheelMediator,
            IProfileService profileService)
        {
            _cameraMediator = cameraMediator;
            _filterWheelMediator = filterWheelMediator;
            _profileService = profileService;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.StopExposure => true,
                RemoteCommandType.ChangeFilter => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.StopExposure => ExecuteStopExposure(),
                RemoteCommandType.ChangeFilter => await ExecuteChangeFilterAsync(command.Parameters, token),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        private CommandResult ExecuteStopExposure()
        {
            Logger.Info("AstroManager: Executing STOP EXPOSURE");
            try
            {
                var info = _cameraMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Camera is not connected");

                _cameraMediator.AbortExposure();
                return CommandResult.Ok("Camera exposure aborted");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop exposure: {ex.Message}");
                return CommandResult.Fail($"Failed to stop exposure: {ex.Message}");
            }
        }

        private class FilterParameters
        {
            public string? Filter { get; set; }
        }

        private async Task<CommandResult> ExecuteChangeFilterAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing CHANGE FILTER");
            try
            {
                var info = _filterWheelMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Filter wheel is not connected");

                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No filter specified");

                var filterParams = JsonSerializer.Deserialize<FilterParameters>(parameters);
                if (filterParams == null || string.IsNullOrEmpty(filterParams.Filter))
                    return CommandResult.Fail("Invalid filter parameters");

                var filters = _profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
                var targetFilter = filters?.FirstOrDefault(f => f.Name.Equals(filterParams.Filter, StringComparison.OrdinalIgnoreCase));

                if (targetFilter == null)
                    return CommandResult.Fail($"Filter '{filterParams.Filter}' not found");

                await _filterWheelMediator.ChangeFilter(targetFilter, token);
                return CommandResult.Ok($"Changed to filter: {filterParams.Filter}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to change filter: {ex.Message}");
                return CommandResult.Fail($"Failed to change filter: {ex.Message}");
            }
        }
    }
}
