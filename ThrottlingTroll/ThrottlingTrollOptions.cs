
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThrottlingTroll
{
    /// <summary>
    /// Options for programmatic configuration
    /// </summary>
    public class ThrottlingTrollOptions
    {
        /// <summary>
        /// Static instance of <see cref="ThrottlingTrollConfig"/>
        /// </summary>
        public ThrottlingTrollConfig Config { get; set; }

        /// <summary>
        /// <see cref="ThrottlingTrollConfig"/> getter. Use this to load throttling rules from some storage.
        /// </summary>
        public Func<Task<ThrottlingTrollConfig>> GetConfigFunc { get; set; }

        /// <summary>
        /// When set to > 0, <see cref="GetConfigFunc"/> will be called periodically with this interval.
        /// This allows you to dynamically change throttling rules and limits without restarting the service.
        /// </summary>
        public int IntervalToReloadConfigInSeconds { get; set; }

        /// <summary>
        /// <see cref="ICounterStore"/> implementation, to store counters in.
        /// </summary>
        public ICounterStore CounterStore { get; set; }

        /// <summary>
        /// Logging utility to use
        /// </summary>
        public Action<LogLevel, string> Log { get; set; }

        /// <summary>
        /// Custom response creation routine.
        /// Takes <see cref="LimitExceededResult"/> (represents the Rule that was exceeded),
        /// <see cref="HttpRequestProxy"/> (provides info about the ongoing request), 
        /// <see cref="HttpResponseProxy"/> (which should be customized by your code) and
        /// <see cref="CancellationToken"/> (which indicates that the request was aborted from outside)
        /// </summary>
        public Func<LimitExceededResult, HttpRequestProxy, HttpResponseProxy, CancellationToken, Task> ResponseFabric { get; set; }
    }
}