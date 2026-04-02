using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Sequencer.Interfaces.Mediator;
using Shared.Model.DTO.Client;
using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles configuration commands: RefreshConfig, StartSession, RestartSessionWithConfig
    /// </summary>
    public class ConfigurationCommandHandler : ICommandHandler
    {
        private readonly ISequenceMediator _sequenceMediator;
        private readonly Func<Task> _refreshConfigAction;
        private readonly Func<Guid?, Task<bool>> _startSessionAction;

        public ConfigurationCommandHandler(
            ISequenceMediator sequenceMediator,
            Func<Task> refreshConfigAction,
            Func<Guid?, Task<bool>> startSessionAction)
        {
            _sequenceMediator = sequenceMediator;
            _refreshConfigAction = refreshConfigAction;
            _startSessionAction = startSessionAction;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.RefreshConfig => true,
                RemoteCommandType.StartSession => true,
                RemoteCommandType.RestartSessionWithConfig => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.RefreshConfig => await ExecuteRefreshConfigAsync(),
                RemoteCommandType.StartSession => await ExecuteStartSessionAsync(command.Parameters),
                RemoteCommandType.RestartSessionWithConfig => await ExecuteRestartSessionWithConfigAsync(command.Parameters),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        private async Task<CommandResult> ExecuteRefreshConfigAsync()
        {
            Logger.Info("AstroManager: Executing REFRESH CONFIG");
            try
            {
                await _refreshConfigAction();
                return CommandResult.Ok("Configuration refreshed from server");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to refresh config: {ex.Message}");
                return CommandResult.Fail($"Failed to refresh config: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteStartSessionAsync(string? parameters)
        {
            Logger.Info($"AstroManager: Executing START SESSION (params: {parameters})");
            
            try
            {
                // Parse optional config ID from parameters
                Guid? configId = null;
                if (!string.IsNullOrEmpty(parameters))
                {
                    if (Guid.TryParse(parameters, out var parsedId))
                    {
                        configId = parsedId;
                    }
                    else
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(parameters);
                            if (doc.RootElement.TryGetProperty("configId", out var configIdProp) ||
                                doc.RootElement.TryGetProperty("ConfigId", out configIdProp) ||
                                doc.RootElement.TryGetProperty("configurationId", out configIdProp) ||
                                doc.RootElement.TryGetProperty("ConfigurationId", out configIdProp))
                            {
                                var idStr = configIdProp.GetString();
                                if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out var jsonId))
                                {
                                    configId = jsonId;
                                }
                            }
                        }
                        catch { }
                    }
                }
                
                var success = await _startSessionAction(configId);
                
                if (success)
                {
                    var msg = configId.HasValue 
                        ? $"Session started with configuration {configId.Value}" 
                        : "Session started";
                    return CommandResult.Ok(msg);
                }
                else
                {
                    return CommandResult.Fail("Failed to start session");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to start session: {ex.Message}");
                return CommandResult.Fail($"Failed to start session: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteRestartSessionWithConfigAsync(string? parameters)
        {
            Logger.Info($"AstroManager: Executing RESTART SESSION WITH CONFIG (params: {parameters})");
            
            try
            {
                // Parse config ID from parameters
                Guid? configId = null;
                if (!string.IsNullOrEmpty(parameters))
                {
                    if (Guid.TryParse(parameters, out var parsedId))
                    {
                        configId = parsedId;
                    }
                    else
                    {
                        // Try JSON format
                        try
                        {
                            using var doc = JsonDocument.Parse(parameters);
                            if (doc.RootElement.TryGetProperty("configId", out var configIdProp) ||
                                doc.RootElement.TryGetProperty("ConfigId", out configIdProp) ||
                                doc.RootElement.TryGetProperty("configurationId", out configIdProp) ||
                                doc.RootElement.TryGetProperty("ConfigurationId", out configIdProp))
                            {
                                var idStr = configIdProp.GetString();
                                if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out var jsonId))
                                {
                                    configId = jsonId;
                                }
                            }
                        }
                        catch { }
                    }
                }
                
                // Find the AstroManagerTargetScheduler in the sequence
                AstroManagerTargetScheduler? scheduler = null;
                
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var mediatorType = _sequenceMediator.GetType();
                        
                        // Get sequenceNavigation field (same approach as GetSequenceTree)
                        var navField = mediatorType.GetField("sequenceNavigation", 
                            BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? mediatorType.GetField("_sequenceNavigation", 
                                BindingFlags.NonPublic | BindingFlags.Instance);
                        
                        if (navField == null)
                        {
                            Logger.Warning("AstroManager: Could not find sequenceNavigation field");
                            return;
                        }
                        
                        var nav = navField.GetValue(_sequenceMediator);
                        if (nav == null)
                        {
                            Logger.Warning("AstroManager: sequenceNavigation is null");
                            return;
                        }
                        
                        // Get Sequence2VM from navigation
                        var navType = nav.GetType();
                        var sequence2VMProp = navType.GetProperty("Sequence2VM");
                        var sequence2VM = sequence2VMProp?.GetValue(nav);
                        
                        if (sequence2VM != null)
                        {
                            // Search for AstroManagerTargetScheduler in the sequence
                            scheduler = FindSchedulerInSequence(sequence2VM);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"AstroManager: Error finding scheduler: {ex.Message}");
                    }
                });
                
                if (scheduler == null)
                {
                    return CommandResult.Fail("AstroManager Scheduler not found in current sequence. Add it to your sequence first.");
                }
                
                // Update scheduler configuration if provided
                if (configId.HasValue)
                {
                    scheduler.SelectedConfigurationId = configId.Value;
                    Logger.Info($"AstroManager: Set scheduler config to {configId.Value}");
                }
                
                // Refresh configurations from server to get latest
                await scheduler.LoadConfigurationsAsync();
                
                // Reset the session so it will restart with new config on next execution
                scheduler.ResetSession();
                
                var configName = scheduler.SelectedConfigurationName;
                Logger.Info($"AstroManager: Scheduler session reset, will use '{configName}' on next execution");
                
                return CommandResult.Ok($"Scheduler session reset. Will use '{configName}' configuration on next slot.");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to restart session with config: {ex.Message}");
                return CommandResult.Fail($"Failed to restart session: {ex.Message}");
            }
        }

        /// <summary>
        /// Recursively search for AstroManagerTargetScheduler in a sequence
        /// </summary>
        private AstroManagerTargetScheduler? FindSchedulerInSequence(object container)
        {
            if (container is AstroManagerTargetScheduler scheduler)
            {
                return scheduler;
            }
            
            // Check if it's a container with Items
            var itemsProp = container.GetType().GetProperty("Items");
            if (itemsProp != null)
            {
                var items = itemsProp.GetValue(container) as System.Collections.IEnumerable;
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item != null)
                        {
                            var found = FindSchedulerInSequence(item);
                            if (found != null) return found;
                        }
                    }
                }
            }
            
            // Check Sequencer property (for Sequence2VM)
            var seqProp = container.GetType().GetProperty("Sequencer") ?? container.GetType().GetProperty("Sequence");
            if (seqProp != null)
            {
                var seq = seqProp.GetValue(container);
                if (seq != null)
                {
                    var found = FindSchedulerInSequence(seq);
                    if (found != null) return found;
                }
            }
            
            return null;
        }
    }
}
