using Serilog.Context;

namespace Augmentor;

internal static class LoggerExtensions
{
    public static ILogger Use(this ILogger logger, string name, object value)
    {
        return new Decorator(logger, name, value);
    }

    private class Decorator(ILogger logger, string name, object value) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return logger.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logger.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            using (LogContext.PushProperty(name, value, true))
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
