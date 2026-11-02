using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

// The SDK's console logger writes every level, including a DEBUG line per
// request from RegionFinder and AWSSDKUtils. Route it through
// System.Diagnostics instead, admit warnings and errors only, and forward
// them to the application's JSON log under the "Amazon" category.
public static class AwsSdkLogging
{
    public const string Category = "Amazon";

    public sealed class Listener : TraceListener
    {
        volatile ILogger logger;

        public Listener() : base(Category)
        {
            Filter = new EventTypeFilter(SourceLevels.Warning);
        }

        public override bool IsThreadSafe => true;

        // Messages written before the host's logger factory exists are dropped.
        public void Attach(ILoggerFactory factory) => logger = factory.CreateLogger(Category);

        static LogLevel LevelOf(TraceEventType eventType) => eventType switch
        {
            TraceEventType.Critical => LogLevel.Critical,
            TraceEventType.Error => LogLevel.Error,
            TraceEventType.Warning => LogLevel.Warning,
            TraceEventType.Information => LogLevel.Information,
            TraceEventType.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };

        bool Admits(TraceEventCache eventCache, string source, TraceEventType eventType, int id) =>
            Filter == null || Filter.ShouldTrace(eventCache, source, eventType, id, null, null, null, null);

        void Forward(TraceEventType eventType, string message, Exception exception)
        {
            var target = logger;
            if (target == null) return;
            target.Log(LevelOf(eventType), exception, "{AwsSdkMessage}", message);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
        {
            if (Admits(eventCache, source, eventType, id)) Forward(eventType, message, null);
        }

        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
        {
            if (Admits(eventCache, source, eventType, id)) Forward(eventType, args == null ? format : string.Format(format, args), null);
        }

        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, object data)
        {
            if (Admits(eventCache, source, eventType, id)) Forward(eventType, data?.ToString(), data as Exception);
        }

        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id, params object[] data)
        {
            if (!Admits(eventCache, source, eventType, id)) return;
            data ??= Array.Empty<object>();
            var exception = data.OfType<Exception>().FirstOrDefault();
            var message = new StringBuilder();
            foreach (var item in data.Where(x => x is not Exception))
            {
                if (message.Length > 0) message.Append(' ');
                message.Append(item);
            }
            Forward(eventType, message.ToString(), exception);
        }

        public override void Write(string message) { }
        public override void WriteLine(string message) { }
    }

    public static Listener Configure()
    {
        var listener = new Listener();
        Amazon.AWSConfigs.AddTraceListener(Category, listener);
        Amazon.AWSConfigs.LoggingConfig.LogTo = Amazon.LoggingOptions.SystemDiagnostics;
        Amazon.AWSConfigs.LoggingConfig.LogResponses = Amazon.ResponseLoggingOption.Never;
        Amazon.AWSConfigs.LoggingConfig.LogMetrics = false;
        return listener;
    }
}
