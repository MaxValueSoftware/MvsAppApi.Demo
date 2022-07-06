using System.Threading;
using Serilog.Core;
using Serilog.Events;

namespace DemoUI.ViewModels
{
    public class ThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
                "ThreadID", Thread.CurrentThread.ManagedThreadId.ToString("D4")));
        }
    }
}