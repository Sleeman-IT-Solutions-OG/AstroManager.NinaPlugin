namespace AstroManager.NinaPlugin.Services.Commands.Abstractions
{
    /// <summary>
    /// Result of a command execution
    /// </summary>
    public readonly struct CommandResult
    {
        public bool Success { get; }
        public string Message { get; }

        public CommandResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public static CommandResult Ok(string message) => new(true, message);
        public static CommandResult Fail(string message) => new(false, message);

        public void Deconstruct(out bool success, out string message)
        {
            success = Success;
            message = Message;
        }

        /// <summary>
        /// Converts to the legacy tuple format for compatibility
        /// </summary>
        public (bool Success, string Message) ToTuple() => (Success, Message);
    }
}
