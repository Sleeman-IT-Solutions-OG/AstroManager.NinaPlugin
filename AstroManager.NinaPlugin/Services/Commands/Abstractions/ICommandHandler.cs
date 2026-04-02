using Shared.Model.DTO.Client;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin.Services.Commands.Abstractions
{
    /// <summary>
    /// Interface for handling remote commands from AstroManager API
    /// </summary>
    public interface ICommandHandler
    {
        /// <summary>
        /// Checks if this handler can process the given command type
        /// </summary>
        bool CanHandle(RemoteCommandType commandType);

        /// <summary>
        /// Executes the command and returns success status with message
        /// </summary>
        Task<CommandResult> ExecuteAsync(RemoteCommandDto command, CancellationToken token);
    }
}
