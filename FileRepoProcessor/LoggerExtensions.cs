using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace FileRepoProcessor
{
    public static class LoggerExtensions
    {
        public static void LogInfo(
            this ILogger logger,
            string message,
            [CallerMemberName] string memberName = "")
        {
            logger.LogInformation("[{Method}] {Message}", memberName, message);
        }

        public static void LogErrorEx(
        this ILogger logger,
        Exception ex,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerLineNumber] int line = 0)
        {
            logger.LogError(ex, "[{Method}:{Line}] {Message}", memberName, line, message);
        }
    }
}