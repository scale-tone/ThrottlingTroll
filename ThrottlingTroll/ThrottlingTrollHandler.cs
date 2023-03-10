using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ThrottlingTroll
{
    /// <summary>
    /// Implements egress throttling
    /// </summary>
    public class ThrottlingTrollHandler : DelegatingHandler
    {
        private const int DefaultDelayInSeconds = 5;

        private readonly ThrottlingTroll _troll;
        private bool _propagateToIngress;

        private readonly Func<LimitExceededResult, HttpRequestProxy, HttpResponseProxy, CancellationToken, Task> _responseFabric;

        /// <summary>
        /// Use this ctor when manually creating <see cref="HttpClient"/> instances. 
        /// </summary>
        /// <param name="counterStore">Implementation of <see cref="ICounterStore"/></param>
        /// <param name="config">Throttling configuration</param>
        /// <param name="log">Logging utility</param>
        /// <param name="innerHttpMessageHandler">Instance of <see cref="HttpMessageHandler"/> to use as inner handler. When null, a default <see cref="HttpClientHandler"/> instance will be created.</param>
        public ThrottlingTrollHandler
        (
            ThrottlingTrollEgressConfig config,
            Action<LogLevel, string> log = null,
            HttpMessageHandler innerHttpMessageHandler = null

        ) : this(null, config, log, innerHttpMessageHandler)
        {
        }

        /// <summary>
        /// Use this ctor when manually creating <see cref="HttpClient"/> instances. 
        /// </summary>
        /// <param name="counterStore">Implementation of <see cref="ICounterStore"/></param>
        /// <param name="config">Throttling configuration</param>
        /// <param name="log">Logging utility</param>
        /// <param name="innerHttpMessageHandler">Instance of <see cref="HttpMessageHandler"/> to use as inner handler. When null, a default <see cref="HttpClientHandler"/> instance will be created.</param>
        public ThrottlingTrollHandler
        (
            ICounterStore counterStore,
            ThrottlingTrollEgressConfig config,
            Action<LogLevel, string> log = null,
            HttpMessageHandler innerHttpMessageHandler = null

        ) : this(null, counterStore, config, log, innerHttpMessageHandler)
        {
        }

        /// <summary>
        /// Use this ctor when manually creating <see cref="HttpClient"/> instances. 
        /// </summary>
        /// <param name="responseFabric">Routine for generating custom HTTP responses and/or controlling built-in retries</param>
        /// <param name="counterStore">Implementation of <see cref="ICounterStore"/></param>
        /// <param name="config">Throttling configuration</param>
        /// <param name="log">Logging utility</param>
        /// <param name="innerHttpMessageHandler">Instance of <see cref="HttpMessageHandler"/> to use as inner handler. When null, a default <see cref="HttpClientHandler"/> instance will be created.</param>
        public ThrottlingTrollHandler
        (
            Func<LimitExceededResult, HttpRequestProxy, HttpResponseProxy, CancellationToken, Task> responseFabric,
            ICounterStore counterStore,
            ThrottlingTrollEgressConfig config,
            Action<LogLevel, string> log = null,
            HttpMessageHandler innerHttpMessageHandler = null

        ) : base(innerHttpMessageHandler ?? new HttpClientHandler())
        {
            this._troll = new ThrottlingTroll(log, counterStore ?? new MemoryCacheCounterStore(), async () => config);
            this._propagateToIngress = config.PropagateToIngress;
            this._responseFabric = responseFabric;
        }

        /// <summary>
        /// Use this ctor in DI container initialization (with <see cref="HttpClientBuilderExtensions.AddHttpMessageHandler"/>)
        /// </summary>
        internal ThrottlingTrollHandler(ThrottlingTrollOptions options)
        {
            this._responseFabric = options.ResponseFabric;

            this._troll = new ThrottlingTroll(options.Log, options.CounterStore, async () =>
            {
                var config = await options.GetConfigFunc();

                var egressConfig = config as ThrottlingTrollEgressConfig;
                if (egressConfig != null)
                {
                    // Need to also get this flag from config
                    this._propagateToIngress = egressConfig.PropagateToIngress;
                }

                return config;

            }, options.IntervalToReloadConfigInSeconds);
        }

        /// <summary>
        /// Handles egress throttling.
        /// </summary>
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestProxy = new HttpRequestProxy(request);
            HttpResponseMessage response;
            int retryCount = 0;

            while (true)
            {
                // Decoupling from SynchronizationContext just in case
                var isExceededResult = Task.Run(() =>
                {
                    return this._troll.IsExceededAsync(new HttpRequestProxy(request));
                })
                .Result;

                if (isExceededResult == null)
                {
                    // Just making the call as normal
                    response = base.Send(request, cancellationToken);
                }
                else
                {
                    // Creating the TooManyRequests response
                    response = this.CreateRetryAfterResponse(request, isExceededResult);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && this._responseFabric != null)
                {
                    // Using custom response fabric
                    var responseProxy = new HttpResponseProxy(response, retryCount++);

                    // Decoupling from SynchronizationContext just in case
                    Task.Run(() =>
                    {
                        return this._responseFabric(isExceededResult, requestProxy, responseProxy, cancellationToken);
                    })
                    .Wait();

                    if (responseProxy.ShouldRetryEgressRequest)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        Thread.Sleep(this.GetRetryAfterTimeSpan(response.Headers));

                        cancellationToken.ThrowIfCancellationRequested();

                        // Retrying the call
                        continue;
                    }

                    response = responseProxy.EgressResponse;
                }

                break;
            }

            this.PropagateToIngressIfNeeded(response);

            return response;
        }

        /// <summary>
        /// Handles egress throttling.
        /// </summary>
        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestProxy = new HttpRequestProxy(request);
            HttpResponseMessage response;
            int retryCount = 0;

            while (true)
            {
                var isExceededResult = await this._troll.IsExceededAsync(requestProxy);

                if (isExceededResult == null)
                {
                    // Just making the call as normal
                    response = await base.SendAsync(request, cancellationToken);
                }
                else
                {
                    // Creating the TooManyRequests response
                    response = this.CreateRetryAfterResponse(request, isExceededResult);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && this._responseFabric != null)
                {
                    // Using custom response fabric
                    var responseProxy = new HttpResponseProxy(response, retryCount++);

                    await this._responseFabric(isExceededResult, requestProxy, responseProxy, cancellationToken);

                    if (responseProxy.ShouldRetryEgressRequest)
                    {
                        await Task.Delay(this.GetRetryAfterTimeSpan(response.Headers), cancellationToken);

                        // Retrying the call
                        continue;
                    }

                    response = responseProxy.EgressResponse;
                }

                break;
            }

            this.PropagateToIngressIfNeeded(response);

            return response;
        }

        private void PropagateToIngressIfNeeded(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests && this._propagateToIngress)
            {
                // Propagating TooManyRequests response up to ThrottlingTrollMiddleware
                throw new ThrottlingTrollTooManyRequestsException
                {
                    RetryAfterHeaderValue = response.Headers.RetryAfter.ToString()
                };
            }
        }

        private TimeSpan GetRetryAfterTimeSpan(HttpResponseHeaders headers)
        {
            if (headers?.RetryAfter?.Delta != null)
            {
                return headers.RetryAfter.Delta.Value;
            }
            else if (headers?.RetryAfter?.Date != null)
            {
                return headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            }

            return TimeSpan.FromSeconds(DefaultDelayInSeconds);
        }

        private HttpResponseMessage CreateRetryAfterResponse(HttpRequestMessage request, LimitExceededResult limitExceededResult)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

            if (DateTime.TryParse(limitExceededResult.RetryAfterHeaderValue, out var retryAfterDateTime))
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfterDateTime);
                response.Content = new StringContent($"Retry after {limitExceededResult.RetryAfterHeaderValue}");
            }
            else if (int.TryParse(limitExceededResult.RetryAfterHeaderValue, out int retryAfterInSeconds))
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterInSeconds));
                response.Content = new StringContent($"Retry after {retryAfterInSeconds} seconds");
            }

            response.RequestMessage = request;

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            this._troll.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Extension methods for configuring ThrottlingTrollHandler
    /// </summary>
    public static class ThrottlingTrollHandlerExtensions
    {
        /// <summary>
        /// Appends <see cref="ThrottlingTrollHandler"/> to the given HttpClient.
        /// Optionally allows to configure options.
        /// </summary>
        public static IHttpClientBuilder AddThrottlingTrollMessageHandler(this IHttpClientBuilder builder, Action<ThrottlingTrollOptions> options = null)
        {
            var opt = new ThrottlingTrollOptions();

            if (options != null)
            {
                options(opt);
            }

            return builder.AddHttpMessageHandler(serviceProvider =>
            {
                if (opt.GetConfigFunc == null)
                {
                    if (opt.Config == null)
                    {
                        // Trying to read config from settings
                        var config = serviceProvider.GetService<IConfiguration>();

                        var section = config?.GetSection(ConfigSectionName);

                        var throttlingTrollConfig = section?.Get<ThrottlingTrollEgressConfig>();

                        if (throttlingTrollConfig == null)
                        {
                            throw new InvalidOperationException($"Failed to initialize ThrottlingTroll. Settings section '{ConfigSectionName}' not found or cannot be deserialized.");
                        }

                        opt.GetConfigFunc = async () => throttlingTrollConfig;
                    }
                    else
                    {
                        opt.GetConfigFunc = async () => opt.Config;
                    }
                }

                if (opt.Log == null)
                {
                    var logger = serviceProvider.GetService<ILogger<ThrottlingTroll>>();

                    opt.Log = logger == null ? null : (l, s) => logger.Log(l, s);
                }

                if (opt.CounterStore == null)
                {
                    opt.CounterStore = serviceProvider.GetOrCreateThrottlingTrollCounterStore();
                }

                return new ThrottlingTrollHandler(opt);
            });
        }

        private static ICounterStore GetOrCreateThrottlingTrollCounterStore(this IServiceProvider serviceProvider)
        {
            var counterStore = serviceProvider.GetService<ICounterStore>();

            if (counterStore == null)
            {
                var redis = serviceProvider.GetService<IConnectionMultiplexer>();

                if (redis != null)
                {
                    counterStore = new RedisCounterStore(redis);
                }
                else
                {
                    var distributedCache = serviceProvider.GetService<IDistributedCache>();

                    if (distributedCache != null)
                    {
                        counterStore = new DistributedCacheCounterStore(distributedCache);
                    }
                    else
                    {
                        // Defaulting to MemoryCacheCounterStore
                        counterStore = new MemoryCacheCounterStore();
                    }
                }
            }

            return counterStore;
        }

        private const string ConfigSectionName = "ThrottlingTrollEgress";
    }
}