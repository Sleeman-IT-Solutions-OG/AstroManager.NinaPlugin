using AstroManager.NinaPlugin.Services.Commands.Abstractions;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using Shared.Model.DTO.Client;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Handlers
{
    /// <summary>
    /// Handles sequence-related commands: Start, Stop, Pause, Resume, Load, Skip, etc.
    /// </summary>
    public class SequenceCommandHandler : ICommandHandler
    {
        private readonly ISequenceMediator _sequenceMediator;
        private readonly IProfileService _profileService;
        private readonly HeartbeatService _heartbeatService;

        public SequenceCommandHandler(
            ISequenceMediator sequenceMediator,
            IProfileService profileService,
            HeartbeatService heartbeatService)
        {
            _sequenceMediator = sequenceMediator;
            _profileService = profileService;
            _heartbeatService = heartbeatService;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.StopSequence => true,
                RemoteCommandType.PauseSequence => true,
                RemoteCommandType.ResumeSequence => true,
                RemoteCommandType.StartSequence => true,
                RemoteCommandType.ResetAndStartSequence => true,
                RemoteCommandType.LoadSequence => true,
                RemoteCommandType.ListSequenceFiles => true,
                RemoteCommandType.GetSequenceTree => true,
                RemoteCommandType.SkipTarget => true,
                RemoteCommandType.StopAmScheduler => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.StopSequence => ExecuteStopSequence(),
                RemoteCommandType.PauseSequence => ExecutePauseSequence(),
                RemoteCommandType.ResumeSequence => ExecuteResumeSequence(),
                RemoteCommandType.StartSequence => ExecuteStartSequence(command.Parameters),
                RemoteCommandType.ResetAndStartSequence => await ExecuteResetAndStartSequenceAsync(),
                RemoteCommandType.LoadSequence => await ExecuteLoadSequenceAsync(command.Parameters),
                RemoteCommandType.ListSequenceFiles => ExecuteListSequenceFiles(),
                RemoteCommandType.GetSequenceTree => await ExecuteGetSequenceTreeAsync(),
                RemoteCommandType.SkipTarget => ExecuteSkipTarget(),
                RemoteCommandType.StopAmScheduler => ExecuteStopAmScheduler(),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        #region Sequence Control Commands

        private CommandResult ExecuteStopSequence()
        {
            Logger.Info("AstroManager: Executing STOP SEQUENCE");
            try
            {
                _sequenceMediator.CancelAdvancedSequence();
                return CommandResult.Ok("Stop Sequence command sent successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop sequence: {ex.Message}");
                return CommandResult.Fail($"Failed to stop sequence: {ex.Message}");
            }
        }

        private CommandResult ExecutePauseSequence()
        {
            Logger.Info("AstroManager: Executing PAUSE SEQUENCE");
            try
            {
                Logger.Warning("AstroManager: Pause not directly supported - use Stop and manually resume");
                return CommandResult.Ok("Pause command received - use Stop/Resume workflow");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to pause sequence: {ex.Message}");
                return CommandResult.Fail($"Failed to pause sequence: {ex.Message}");
            }
        }

        private CommandResult ExecuteResumeSequence()
        {
            Logger.Info("AstroManager: Executing RESUME SEQUENCE");
            try
            {
                Logger.Warning("AstroManager: Resume not directly supported - restart sequence from UI");
                return CommandResult.Ok("Resume command received - restart sequence from NINA UI");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to resume sequence: {ex.Message}");
                return CommandResult.Fail($"Failed to resume sequence: {ex.Message}");
            }
        }

        private CommandResult ExecuteStartSequence(string? parameters)
        {
            Logger.Info("AstroManager: Executing START SEQUENCE");
            try
            {
                bool skipValidation = true;
                if (!string.IsNullOrEmpty(parameters))
                {
                    try
                    {
                        var paramObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters);
                        if (paramObj != null && paramObj.TryGetValue("skipValidation", out var skipVal))
                        {
                            skipValidation = skipVal.GetBoolean();
                        }
                    }
                    catch { /* Use default */ }
                }

                _sequenceMediator.StartAdvancedSequence(skipValidation: skipValidation);
                return CommandResult.Ok(skipValidation
                    ? "Sequence started (validation skipped)"
                    : "Sequence started");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to start sequence: {ex.Message}");
                return CommandResult.Fail($"Failed to start sequence: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteResetAndStartSequenceAsync()
        {
            Logger.Info("AstroManager: Executing RESET AND START SEQUENCE");
            try
            {
                try
                {
                    _sequenceMediator.CancelAdvancedSequence();
                    await Task.Delay(500);
                }
                catch { /* Ignore if not running */ }

                bool resetSuccess = false;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var mediatorType = _sequenceMediator.GetType();
                        var navField = mediatorType.GetField("sequenceNavigation",
                            BindingFlags.NonPublic | BindingFlags.Instance);

                        if (navField != null)
                        {
                            var nav = navField.GetValue(_sequenceMediator);
                            if (nav != null)
                            {
                                var sequence2VMProp = nav.GetType().GetProperty("Sequence2VM");
                                if (sequence2VMProp != null)
                                {
                                    var sequence2VM = sequence2VMProp.GetValue(nav);
                                    if (sequence2VM != null)
                                    {
                                        var seq2Type = sequence2VM.GetType();
                                        var seqProp = seq2Type.GetProperty("Sequencer")
                                            ?? seq2Type.GetProperty("Sequence")
                                            ?? seq2Type.GetProperty("RootContainer");

                                        if (seqProp != null)
                                        {
                                            var rootSequence = seqProp.GetValue(sequence2VM);
                                            if (rootSequence != null)
                                            {
                                                var resetMethod = rootSequence.GetType().GetMethod("ResetProgress",
                                                    BindingFlags.Public | BindingFlags.Instance);

                                                if (resetMethod != null)
                                                {
                                                    resetMethod.Invoke(rootSequence, null);
                                                    resetSuccess = true;
                                                    Logger.Info("AstroManager: Sequence reset via ResetProgress on root container");
                                                }
                                                else
                                                {
                                                    var itemsProp = rootSequence.GetType().GetProperty("Items");
                                                    if (itemsProp != null)
                                                    {
                                                        var items = itemsProp.GetValue(rootSequence) as IEnumerable;
                                                        if (items != null)
                                                        {
                                                            ResetItemsRecursively(items);
                                                            resetSuccess = true;
                                                            Logger.Info("AstroManager: Sequence reset via recursive item reset");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"AstroManager: Could not reset sequence via reflection: {ex.Message}");
                    }
                });

                if (!resetSuccess)
                {
                    Logger.Warning("AstroManager: Could not find reset method - starting sequence without reset");
                }

                _sequenceMediator.StartAdvancedSequence(skipValidation: true);
                return CommandResult.Ok(resetSuccess
                    ? "Sequence reset and started"
                    : "Sequence started (reset not available - may resume from current position)");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to reset and start sequence: {ex.Message}");
                return CommandResult.Fail($"Failed to reset and start sequence: {ex.Message}");
            }
        }

        private void ResetItemsRecursively(IEnumerable items)
        {
            foreach (var item in items)
            {
                var resetMethod = item.GetType().GetMethod("ResetProgress",
                    BindingFlags.Public | BindingFlags.Instance);
                resetMethod?.Invoke(item, null);

                var childItemsProp = item.GetType().GetProperty("Items");
                if (childItemsProp != null)
                {
                    var childItems = childItemsProp.GetValue(item) as IEnumerable;
                    if (childItems != null)
                    {
                        ResetItemsRecursively(childItems);
                    }
                }
            }
        }

        private CommandResult ExecuteSkipTarget()
        {
            Logger.Info("AstroManager: Executing SKIP TARGET");
            try
            {
                Logger.Warning("AstroManager: Skip target not directly supported - use Stop/Start workflow");
                return CommandResult.Ok("Skip command received - manual skip recommended via NINA UI");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to skip target: {ex.Message}");
                return CommandResult.Fail($"Failed to skip target: {ex.Message}");
            }
        }

        private CommandResult ExecuteStopAmScheduler()
        {
            Logger.Info("AstroManager: Executing STOP AM SCHEDULER");
            try
            {
                var isSchedulerRunning = SharedSchedulerState.Instance.IsSchedulerRunning || _heartbeatService.IsActivelyImaging;
                Logger.Debug($"AstroManager: Stop AM scheduler running check - SharedState={SharedSchedulerState.Instance.IsSchedulerRunning}, HeartbeatActive={_heartbeatService.IsActivelyImaging}, Combined={isSchedulerRunning}");

                if (!SharedSchedulerState.Instance.IsSchedulerRunning && _heartbeatService.IsActivelyImaging)
                {
                    Logger.Warning("AstroManager: SharedSchedulerState.IsSchedulerRunning was false but HeartbeatService shows active imaging - syncing state");
                    SharedSchedulerState.Instance.SetSchedulerRunning(true);
                }

                var queued = SharedSchedulerState.Instance.QueueStopScheduler();
                if (!queued)
                {
                    if (!isSchedulerRunning)
                    {
                        return CommandResult.Fail("AM scheduler is not currently running");
                    }

                    return CommandResult.Fail("AM scheduler is not currently running");
                }

                return CommandResult.Ok("AM scheduler stop requested; current exposure (if any) will finish and sequence will continue");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to request AM scheduler stop: {ex.Message}");
                return CommandResult.Fail($"Failed to request AM scheduler stop: {ex.Message}");
            }
        }

        #endregion

        #region Sequence File Commands

        public SequenceFileListDto? GetAvailableSequenceFilesSnapshot()
        {
            try
            {
                var sequenceFolder = ResolveSequenceFolder();
                if (string.IsNullOrEmpty(sequenceFolder) || !Directory.Exists(sequenceFolder))
                {
                    return null;
                }

                var sequenceFiles = Directory.GetFiles(sequenceFolder, "*.json", SearchOption.AllDirectories)
                    .Select(f => new SequenceFileEntryDto
                    {
                        FullPath = f,
                        FileName = Path.GetFileName(f),
                        RelativePath = f.Replace(sequenceFolder, "").TrimStart(Path.DirectorySeparatorChar),
                        LastModified = File.GetLastWriteTime(f)
                    })
                    .OrderByDescending(f => f.LastModified)
                    .ToList();

                return new SequenceFileListDto
                {
                    SequenceFolder = sequenceFolder,
                    Files = sequenceFiles,
                    CapturedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Failed to build sequence file snapshot: {ex.Message}");
                return null;
            }
        }

        private string ResolveSequenceFolder()
        {
            var sequenceFolder = NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH;

            if (_profileService?.ActiveProfile?.SequenceSettings?.DefaultSequenceFolder != null)
            {
                sequenceFolder = _profileService.ActiveProfile.SequenceSettings.DefaultSequenceFolder;
            }

            if (string.IsNullOrEmpty(sequenceFolder) || !Directory.Exists(sequenceFolder))
            {
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                sequenceFolder = Path.Combine(documentsPath, "N.I.N.A", "Sequences");
            }

            return sequenceFolder;
        }

        private async Task<CommandResult> ExecuteLoadSequenceAsync(string? parameters)
        {
            Logger.Info("AstroManager: Executing LOAD SEQUENCE");
            try
            {
                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No sequence file path provided");

                string? filePath = null;
                
                // Try to parse as JSON first (new format: {"filePath": "C:\\..."})
                if (parameters.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        var paramObj = JsonSerializer.Deserialize<Dictionary<string, string>>(parameters);
                        if (paramObj != null)
                        {
                            paramObj.TryGetValue("filePath", out filePath);
                        }
                    }
                    catch (JsonException ex)
                    {
                        Logger.Warning($"AstroManager: Failed to parse JSON parameters: {ex.Message}");
                    }
                }
                
                // Fallback: treat the entire parameter as a file path (legacy format)
                if (string.IsNullOrEmpty(filePath))
                {
                    filePath = parameters;
                    Logger.Info("AstroManager: Using raw parameter as file path (legacy format)");
                }
                
                if (string.IsNullOrEmpty(filePath))
                    return CommandResult.Fail("Invalid parameters - no file path found");

                if (!File.Exists(filePath))
                    return CommandResult.Fail($"Sequence file not found: {filePath}");

                var fileName = Path.GetFileName(filePath);
                Logger.Info($"AstroManager: Loading sequence file: {filePath}");

                bool loadSuccess = false;
                string? errorMessage = null;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var mediatorType = _sequenceMediator.GetType();
                        Logger.Info($"AstroManager: SequenceMediator type: {mediatorType.FullName}");

                        var navField = mediatorType.GetField("sequenceNavigation",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (navField == null)
                        {
                            errorMessage = "Could not find sequenceNavigation field";
                            return;
                        }

                        var nav = navField.GetValue(_sequenceMediator);
                        if (nav == null)
                        {
                            errorMessage = "sequenceNavigation is null";
                            return;
                        }

                        var factoryField = nav.GetType().GetField("factory",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (factoryField == null)
                        {
                            errorMessage = "Could not find factory field";
                            return;
                        }

                        var factory = factoryField.GetValue(nav);
                        if (factory == null)
                        {
                            errorMessage = "Could not get ISequencerFactory";
                            return;
                        }

                        Type? converterType = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                converterType = asm.GetType("NINA.Sequencer.Utility.SequenceJsonConverter");
                                if (converterType != null) break;
                            }
                            catch { }
                        }

                        if (converterType == null)
                        {
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    if (!asm.FullName.StartsWith("NINA")) continue;
                                    foreach (var type in asm.GetTypes())
                                    {
                                        if (type.Name.Contains("SequenceJsonConverter"))
                                        {
                                            converterType = type;
                                            break;
                                        }
                                    }
                                    if (converterType != null) break;
                                }
                                catch { }
                            }
                        }

                        if (converterType == null)
                        {
                            errorMessage = "Could not find SequenceJsonConverter type";
                            return;
                        }

                        var converter = Activator.CreateInstance(converterType, factory);
                        if (converter == null)
                        {
                            errorMessage = "Could not create SequenceJsonConverter";
                            return;
                        }

                        var json = File.ReadAllText(filePath);
                        var deserializeMethod = converterType.GetMethod("Deserialize", new[] { typeof(string) });
                        if (deserializeMethod == null)
                        {
                            errorMessage = "Could not find Deserialize method";
                            return;
                        }

                        var container = deserializeMethod.Invoke(converter, new object[] { json }) as NINA.Sequencer.Container.ISequenceContainer;
                        if (container == null)
                        {
                            errorMessage = "Failed to deserialize sequence file";
                            return;
                        }

                        NINA.Sequencer.Container.SequenceRootContainer? root = null;

                        if (container is NINA.Sequencer.Container.DeepSkyObjectContainer dso)
                        {
                            var getContainerMethod = factory.GetType().GetMethod("GetContainer");
                            if (getContainerMethod != null)
                            {
                                var rootContainerType = typeof(NINA.Sequencer.Container.SequenceRootContainer);
                                var genericMethod = getContainerMethod.MakeGenericMethod(rootContainerType);
                                root = genericMethod.Invoke(factory, null) as NINA.Sequencer.Container.SequenceRootContainer;

                                if (root != null)
                                {
                                    root.Name = "Advanced Sequence";
                                    var startMethod = getContainerMethod.MakeGenericMethod(typeof(NINA.Sequencer.Container.StartAreaContainer));
                                    var targetMethod = getContainerMethod.MakeGenericMethod(typeof(NINA.Sequencer.Container.TargetAreaContainer));
                                    var endMethod = getContainerMethod.MakeGenericMethod(typeof(NINA.Sequencer.Container.EndAreaContainer));

                                    var startArea = startMethod.Invoke(factory, null) as NINA.Sequencer.Container.ISequenceContainer;
                                    var targetArea = targetMethod.Invoke(factory, null) as NINA.Sequencer.Container.TargetAreaContainer;
                                    var endArea = endMethod.Invoke(factory, null) as NINA.Sequencer.Container.ISequenceContainer;

                                    if (startArea != null) root.Add(startArea);
                                    if (targetArea != null)
                                    {
                                        targetArea.Add(dso);
                                        root.Add(targetArea);
                                    }
                                    if (endArea != null) root.Add(endArea);
                                }
                            }
                        }
                        else if (container is NINA.Sequencer.Container.SequenceRootContainer rootContainer)
                        {
                            root = rootContainer;
                        }

                        if (root == null)
                        {
                            errorMessage = $"Could not create SequenceRootContainer from: {container.GetType().Name}";
                            return;
                        }

                        _sequenceMediator.SetAdvancedSequence(root);
                        loadSuccess = true;
                        Logger.Info($"AstroManager: Sequence loaded successfully: {root.Name}");
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.InnerException?.Message ?? ex.Message;
                        Logger.Error($"AstroManager: Failed to load sequence: {errorMessage}");
                    }
                });

                if (loadSuccess)
                    return CommandResult.Ok($"Sequence loaded: {fileName}");

                try
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        System.Windows.Clipboard.SetText(filePath);
                    });
                    return CommandResult.Ok($"Sequence path copied to clipboard: {fileName}. Use NINA Sequencer → Load. Error: {errorMessage ?? "unknown"}");
                }
                catch
                {
                    return CommandResult.Fail($"Failed to load sequence: {errorMessage ?? "unknown error"}. Path: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to load sequence: {ex.Message}");
                return CommandResult.Fail($"Failed to load sequence: {ex.Message}");
            }
        }

        private CommandResult ExecuteListSequenceFiles()
        {
            Logger.Info("AstroManager: Executing LIST SEQUENCE FILES");
            try
            {
                var snapshot = GetAvailableSequenceFilesSnapshot();
                if (snapshot == null)
                {
                    var folder = ResolveSequenceFolder();
                    return CommandResult.Fail($"Sequence folder not found: {folder}");
                }

                var result = JsonSerializer.Serialize(new
                {
                    snapshot.SequenceFolder,
                    Files = snapshot.Files
                });

                return CommandResult.Ok(result);
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to list sequence files: {ex.Message}");
                return CommandResult.Fail($"Failed to list sequence files: {ex.Message}");
            }
        }

        #endregion

        #region Sequence Tree Commands

        public SequenceTreeDto? GetSequenceTreeSnapshot()
        {
            try
            {
                var treeDto = new SequenceTreeDto();

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var mediatorType = _sequenceMediator.GetType();
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

                        var navType = nav.GetType();
                        var sequence2VMProp = navType.GetProperty("Sequence2VM");
                        if (sequence2VMProp == null)
                        {
                            Logger.Warning("AstroManager: Could not find Sequence2VM property");
                            return;
                        }

                        var sequence2VM = sequence2VMProp.GetValue(nav);
                        if (sequence2VM == null)
                        {
                            Logger.Warning("AstroManager: Sequence2VM is null - no advanced sequence loaded");
                            return;
                        }

                        var seq2Type = sequence2VM.GetType();
                        var seqProp = seq2Type.GetProperty("Sequencer")
                            ?? seq2Type.GetProperty("Sequence")
                            ?? seq2Type.GetProperty("RootContainer")
                            ?? seq2Type.GetProperty("MainContainer");

                        if (seqProp == null)
                        {
                            var availableProps = seq2Type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                            Logger.Warning($"AstroManager: Could not find Sequence on Sequence2VM. Available: {string.Join(", ", availableProps.Select(p => p.Name))}");
                            return;
                        }

                        var sequence = seqProp.GetValue(sequence2VM);
                        if (sequence == null)
                        {
                            Logger.Warning("AstroManager: No sequence loaded");
                            return;
                        }

                        var nameProp = sequence.GetType().GetProperty("Name");
                        treeDto.SequenceName = nameProp?.GetValue(sequence) as string ?? "Sequence";

                        var isRunningFunc = _sequenceMediator.IsAdvancedSequenceRunning;
                        treeDto.IsRunning = isRunningFunc?.Invoke() ?? false;

                        treeDto.RootNodes = ExtractSequenceNodes(sequence, 0);

                        int totalItems = 0, completedItems = 0, runningItems = 0;
                        CountItems(treeDto.RootNodes, ref totalItems, ref completedItems, ref runningItems);
                        treeDto.TotalItems = totalItems;
                        treeDto.CompletedItems = completedItems;
                        treeDto.RunningItems = runningItems;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"AstroManager: Error extracting sequence tree: {ex.Message}");
                    }
                });

                treeDto.CapturedAt = DateTime.UtcNow;
                return treeDto;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Failed to build sequence tree snapshot: {ex.Message}");
                return null;
            }
        }

        private async Task<CommandResult> ExecuteGetSequenceTreeAsync()
        {
            Logger.Debug("AstroManager: Executing GET SEQUENCE TREE");
            try
            {
                await Task.Yield();
                var treeDto = GetSequenceTreeSnapshot() ?? new SequenceTreeDto();

                var json = JsonSerializer.Serialize(treeDto, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                return CommandResult.Ok(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to get sequence tree: {ex.Message}");
                return CommandResult.Fail($"Failed to get sequence tree: {ex.Message}");
            }
        }

        private List<SequenceTreeNodeDto> ExtractSequenceNodes(object item, int level)
        {
            var nodes = new List<SequenceTreeNodeDto>();

            try
            {
                var itemType = item.GetType();

                var nameProp = itemType.GetProperty("Name");
                var name = nameProp?.GetValue(item) as string ?? itemType.Name;

                var statusProp = itemType.GetProperty("Status");
                var statusValue = statusProp?.GetValue(item);
                var status = ConvertToSequenceItemStatus(statusValue);

                var itemsProp = itemType.GetProperty("Items");
                var isContainer = itemsProp != null;

                var node = new SequenceTreeNodeDto
                {
                    Name = name,
                    ItemType = GetFriendlyTypeName(itemType.Name),
                    Status = status,
                    IsContainer = isContainer,
                    Level = level
                };

                var iterationsProp = itemType.GetProperty("Iterations");
                var completedIterationsProp = itemType.GetProperty("CompletedIterations");
                if (iterationsProp != null && completedIterationsProp != null)
                {
                    var iterations = iterationsProp.GetValue(item);
                    var completed = completedIterationsProp.GetValue(item);
                    if (iterations != null && completed != null)
                    {
                        node.Progress = $"{completed}/{iterations}";
                    }
                }

                var descProp = itemType.GetProperty("Description");
                node.Description = descProp?.GetValue(item) as string;

                if (isContainer && itemsProp != null)
                {
                    var items = itemsProp.GetValue(item) as IEnumerable;
                    if (items != null)
                    {
                        foreach (var childItem in items)
                        {
                            if (childItem != null)
                            {
                                var childNodes = ExtractSequenceNodes(childItem, level + 1);
                                node.Children.AddRange(childNodes);
                            }
                        }
                    }
                }

                nodes.Add(node);
            }
            catch (Exception ex)
            {
                Logger.Debug($"AstroManager: Error extracting node: {ex.Message}");
            }

            return nodes;
        }

        private SequenceItemStatus ConvertToSequenceItemStatus(object? statusValue)
        {
            if (statusValue == null) return SequenceItemStatus.Pending;

            var statusString = statusValue.ToString()?.ToLowerInvariant() ?? "";

            return statusString switch
            {
                "running" or "inprogress" => SequenceItemStatus.Running,
                "finished" or "completed" or "success" => SequenceItemStatus.Completed,
                "skipped" => SequenceItemStatus.Skipped,
                "failed" or "error" => SequenceItemStatus.Failed,
                "disabled" => SequenceItemStatus.Disabled,
                _ => SequenceItemStatus.Pending
            };
        }

        private string GetFriendlyTypeName(string typeName)
        {
            return typeName
                .Replace("SequenceContainer", "")
                .Replace("Container", "")
                .Replace("SequenceItem", "")
                .Replace("Instruction", "")
                .Replace("SequentialContainer", "Sequential")
                .Replace("ParallelContainer", "Parallel")
                .Replace("DeepSkyObjectContainer", "Target")
                .Replace("TargetAreaContainer", "Area")
                .Trim();
        }

        private void CountItems(List<SequenceTreeNodeDto> nodes, ref int total, ref int completed, ref int running)
        {
            foreach (var node in nodes)
            {
                total++;
                if (node.Status == SequenceItemStatus.Completed) completed++;
                if (node.Status == SequenceItemStatus.Running) running++;

                if (node.Children.Any())
                {
                    CountItems(node.Children, ref total, ref completed, ref running);
                }
            }
        }

        #endregion
    }
}
