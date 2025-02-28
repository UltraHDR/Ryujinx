using Avalonia.Utilities;
using System;
using System.Text;

namespace Ryujinx.Ava.UI.Helper
{
    using AvaLogger = Avalonia.Logging.Logger;
    using AvaLogLevel = Avalonia.Logging.LogEventLevel;
    using RyuLogger = Ryujinx.Common.Logging.Logger;
    using RyuLogClass = Ryujinx.Common.Logging.LogClass;

    internal class LoggerAdapter : Avalonia.Logging.ILogSink
    {
        public static void Register()
        {
            AvaLogger.Sink = new LoggerAdapter();
        }

        private static RyuLogger.Log? GetLog(AvaLogLevel level)
        {
            return level switch
            {
                AvaLogLevel.Verbose => RyuLogger.Trace,
                AvaLogLevel.Debug => RyuLogger.Debug,
                AvaLogLevel.Information => RyuLogger.Info,
                AvaLogLevel.Warning => RyuLogger.Warning,
                AvaLogLevel.Error => RyuLogger.Error,
                AvaLogLevel.Fatal => RyuLogger.Notice,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
        }

        public bool IsEnabled(AvaLogLevel level, string area)
        {
            return GetLog(level) != null;
        }

        public void Log(AvaLogLevel level, string area, object source, string messageTemplate)
        {
            GetLog(level)?.PrintMsg(RyuLogClass.Ui, Format(area, messageTemplate, source, null));
        }

        public void Log<T0>(AvaLogLevel level, string area, object source, string messageTemplate, T0 propertyValue0)
        {
            GetLog(level)?.PrintMsg(RyuLogClass.Ui, Format(area, messageTemplate, source, new object[] { propertyValue0 }));
        }

        public void Log<T0, T1>(AvaLogLevel level, string area, object source, string messageTemplate, T0 propertyValue0,  T1 propertyValue1)
        {
            GetLog(level)?.PrintMsg(RyuLogClass.Ui, Format(area, messageTemplate, source, new object[] { propertyValue0, propertyValue1 }));
        }

        public void Log<T0, T1, T2>(AvaLogLevel level, string area, object source, string messageTemplate, T0 propertyValue0, T1 propertyValue1, T2 propertyValue2)
        {
            GetLog(level)?.PrintMsg(RyuLogClass.Ui, Format(area, messageTemplate, source, new object[] { propertyValue0, propertyValue1, propertyValue2 }));
        }

        public void Log(AvaLogLevel level, string area, object source, string messageTemplate, params object[] propertyValues)
        {
            GetLog(level)?.PrintMsg(RyuLogClass.Ui, Format(area, messageTemplate, source, propertyValues));
        }

        private static string Format(string area, string template, object source, object[] v)
        {
            var result = new StringBuilder();
            var r = new CharacterReader(template.AsSpan());
            var i = 0;

            result.Append('[');
            result.Append(area);
            result.Append("] ");

            while (!r.End)
            {
                var c = r.Take();

                if (c != '{')
                {
                    result.Append(c);
                }
                else
                {
                    if (r.Peek != '{')
                    {
                        result.Append('\'');
                        result.Append(i < v.Length ? v[i++] : null);
                        result.Append('\'');
                        r.TakeUntil('}');
                        r.Take();
                    }
                    else
                    {
                        result.Append('{');
                        r.Take();
                    }
                }
            }

            if (source != null)
            {
                result.Append(" (");
                result.Append(source.GetType().Name);
                result.Append(" #");
                result.Append(source.GetHashCode());
                result.Append(')');
            }

            return result.ToString();
        }
    }
}