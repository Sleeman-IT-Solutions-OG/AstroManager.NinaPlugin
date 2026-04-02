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
    /// Handles focuser-related commands: MoveFocuser, StopFocuser
    /// Note: Autofocus is handled separately in the main plugin due to complex dependencies
    /// </summary>
    public class FocuserCommandHandler : ICommandHandler
    {
        private readonly IFocuserMediator _focuserMediator;
        private readonly object _focuserMoveLock = new();
        private CancellationTokenSource? _focuserMoveCts;

        public FocuserCommandHandler(IFocuserMediator focuserMediator)
        {
            _focuserMediator = focuserMediator;
        }

        public bool CanHandle(RemoteCommandType commandType)
        {
            return commandType switch
            {
                RemoteCommandType.MoveFocuser => true,
                RemoteCommandType.StopFocuser => true,
                _ => false
            };
        }

        public async Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token)
        {
            return command.CommandType switch
            {
                RemoteCommandType.MoveFocuser => await ExecuteMoveFocuserAsync(command.Parameters, token),
                RemoteCommandType.StopFocuser => await ExecuteStopFocuserAsync(),
                _ => CommandResult.Fail($"Unhandled command type: {command.CommandType}")
            };
        }

        private class FocuserParameters
        {
            public int Position { get; set; }
        }

        private async Task<CommandResult> ExecuteMoveFocuserAsync(string? parameters, CancellationToken token)
        {
            Logger.Info("AstroManager: Executing MOVE FOCUSER");
            try
            {
                var info = _focuserMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Focuser is not connected");

                if (string.IsNullOrEmpty(parameters))
                    return CommandResult.Fail("No position provided");

                var focusParams = JsonSerializer.Deserialize<FocuserParameters>(parameters);
                if (focusParams == null)
                    return CommandResult.Fail("Invalid focuser parameters");

                CancellationTokenSource localCts;
                CancellationTokenSource? oldCts;
                lock (_focuserMoveLock)
                {
                    oldCts = _focuserMoveCts;
                    _focuserMoveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    localCts = _focuserMoveCts;
                }
                try { oldCts?.Cancel(); } catch { }

                try
                {
                    await _focuserMediator.MoveFocuser(focusParams.Position, localCts.Token);
                    return CommandResult.Ok($"Focuser moved to position {focusParams.Position}");
                }
                catch (OperationCanceledException)
                {
                    return CommandResult.Fail("Focuser move cancelled");
                }
                finally
                {
                    lock (_focuserMoveLock)
                    {
                        if (ReferenceEquals(_focuserMoveCts, localCts))
                            _focuserMoveCts = null;
                    }
                    localCts.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to move focuser: {ex.Message}");
                return CommandResult.Fail($"Failed to move focuser: {ex.Message}");
            }
        }

        private async Task<CommandResult> ExecuteStopFocuserAsync()
        {
            Logger.Info("AstroManager: Executing STOP FOCUSER");
            try
            {
                var info = _focuserMediator.GetInfo();
                if (!info.Connected)
                    return CommandResult.Fail("Focuser is not connected");

                CancellationTokenSource? ctsToCancel;
                lock (_focuserMoveLock)
                {
                    ctsToCancel = _focuserMoveCts;
                    _focuserMoveCts = null;
                }
                try { ctsToCancel?.Cancel(); } catch { }

                _ = TryHaltFocuserAsync();

                return CommandResult.Ok("Focuser stop requested");
            }
            catch (Exception ex)
            {
                Logger.Error($"AstroManager: Failed to stop focuser: {ex.Message}");
                return CommandResult.Fail($"Failed to stop focuser: {ex.Message}");
            }
        }

        private async Task TryHaltFocuserAsync()
        {
            try
            {
                var t = _focuserMediator.GetType();
                var method = t.GetMethod("Halt") ?? t.GetMethod("Stop") ?? t.GetMethod("Abort") ?? t.GetMethod("AbortMove") ?? t.GetMethod("StopMove");
                if (method != null)
                {
                    method.Invoke(_focuserMediator, Array.Empty<object>());
                    Logger.Info($"AstroManager: Focuser halt invoked via {method.Name}()");
                    return;
                }

                var info = _focuserMediator.GetInfo();
                if (info.Connected && info.IsMoving)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _focuserMediator.MoveFocuser(info.Position, CancellationToken.None);
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"AstroManager: TryHaltFocuserAsync failed: {ex.Message}");
            }
        }
    }
}
