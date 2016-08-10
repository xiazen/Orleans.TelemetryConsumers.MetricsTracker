﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Orleans;
using Orleans.Runtime;
using Orleans.Providers;

namespace Orleans.TelemetryConsumers.MetricsTracker
{
    public class MetricsTrackerTelemetryConsumer : IEventTelemetryConsumer, IExceptionTelemetryConsumer,
        IDependencyTelemetryConsumer, IMetricTelemetryConsumer, IRequestTelemetryConsumer
    {
        Logger logger;

        IProviderRuntime Runtime;

        SynchronizationContext SyncContext;

        // TODO: provide configuration settings for these
        int HistoryLength = 30;
        TimeSpan MeasurementInterval = TimeSpan.FromSeconds(6);

        object CountersLock = new object();
        ConcurrentDictionary<string, long> Counters;
        ConcurrentDictionary<string, ConcurrentQueue<long>> CounterHistory;

        object MetricsLock = new object();
        ConcurrentDictionary<string, double> Metrics;
        ConcurrentDictionary<string, ConcurrentQueue<double>> MetricHistory;

        object TimeSpanMetricsLock = new object();
        ConcurrentDictionary<string, TimeSpan> TimeSpanMetrics;
        ConcurrentDictionary<string, ConcurrentQueue<TimeSpan>> TimeSpanMetricHistory;

        object RequestsLock = new object();
        ConcurrentDictionary<string, MeasuredRequest> Requests;
        ConcurrentDictionary<string, ConcurrentQueue<MeasuredRequest>> RequestHistory;

        Timer SamplingTimer;
        //System.Timers.Timer SamplingTimer;

        public MetricsTrackerTelemetryConsumer(IProviderRuntime runtime)
        {
            try
            {
                Runtime = runtime;

                logger = Runtime.GetLogger(nameof(MetricsTrackerTelemetryConsumer));

                // TODO: set SyncContext
                //SyncContext = SynchronizationContext.Current;

                Counters = new ConcurrentDictionary<string, long>();
                CounterHistory = new ConcurrentDictionary<string, ConcurrentQueue<long>>();

                Metrics = new ConcurrentDictionary<string, double>();
                MetricHistory = new ConcurrentDictionary<string, ConcurrentQueue<double>>();

                TimeSpanMetrics = new ConcurrentDictionary<string, TimeSpan>();
                TimeSpanMetricHistory = new ConcurrentDictionary<string, ConcurrentQueue<TimeSpan>>();

                Requests = new ConcurrentDictionary<string, MeasuredRequest>();
                RequestHistory = new ConcurrentDictionary<string, ConcurrentQueue<MeasuredRequest>>();

                SamplingTimer = new Timer(new TimerCallback(TimedSampler), null, 1000,
                    (int)MeasurementInterval.TotalMilliseconds);

                //var context = SynchronizationContext.Current;

                //SamplingTimer = new System.Timers.Timer(MeasurementInterval.TotalMilliseconds);
                ////SamplingTimer.SynchronizingObject = context;


            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        void TimedSampler(object state)
        {
            //Task.Factory.StartNew(SampleMetrics, CancellationToken.None, 
            //    TaskCreationOptions.None, TaskScheduler.Default);

            try
            {
                var task = SampleMetrics();
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
            }
        }

        async Task SampleMetrics()
        {
            try
            {
                var snapshot = new MetricsSnapshot { Source = Runtime.SiloIdentity };

                foreach (var metric in Metrics)
                {
                    snapshot.Metrics.Add(metric.Key, metric.Value);

                    MetricHistory[metric.Key].Enqueue(metric.Value);

                    logger.Verbose("[Metric] " + metric.Key + " = " + metric.Value);
                }

                foreach (var counter in Counters)
                {
                    snapshot.Counters.Add(counter.Key, counter.Value);

                    CounterHistory[counter.Key].Enqueue(counter.Value);

                    logger.Verbose("[Counter] " + counter.Key + " = " + counter.Value);
                }

                foreach (var request in Requests)
                {
                    RequestHistory[request.Key].Enqueue(request.Value);

                    logger.Verbose("[Request] " + request.Key + " = " + request.Value);
                }

                foreach (var tsmetric in TimeSpanMetrics)
                {
                    snapshot.TimeSpanMetrics.Add(tsmetric.Key, tsmetric.Value);

                    TimeSpanMetricHistory[tsmetric.Key].Enqueue(tsmetric.Value);

                    logger.Verbose("[Time Span Metric] " + tsmetric.Key + " = " + tsmetric.Value);
                }

                TrimHistories();

                // TODO: fix to make this work
                //var metricsGrain = Runtime.GrainFactory.GetGrain<IClusterMetricsGrain>(Guid.Empty);
                //await metricsGrain.ReportSiloStatistics(snapshot);
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                // don't freak out
            }
        }

        void TrimHistories()
        {
            foreach (var metric in Metrics.Keys)
                TrimMetricsHistory(metric);

            foreach (var counter in Counters.Keys)
                TrimCounterHistory(counter);

            foreach (var tsmetric in TimeSpanMetricHistory.Keys)
                TrimTimeSpanMetricsHistory(tsmetric);

            foreach (var request in RequestHistory.Keys)
                TrimTimeSpanMetricsHistory(request);
        }

        void TrimCounterHistory(string name)
        {
            try
            {
                long counter;
                while (CounterHistory[name].Count > HistoryLength)
                    if (!CounterHistory[name].TryDequeue(out counter))
                        throw new ApplicationException("Couldn't dequeue oldest counter");
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        void TrimMetricsHistory(string name)
        {
            try
            {
                double metric;
                while (MetricHistory[name].Count > HistoryLength)
                    if (!MetricHistory[name].TryDequeue(out metric))
                        throw new ApplicationException("Couldn't dequeue oldest double metric");
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        void TrimTimeSpanMetricsHistory(string name)
        {
            try
            {
                TimeSpan metric;
                while (TimeSpanMetricHistory[name].Count > HistoryLength)
                    if (!TimeSpanMetricHistory[name].TryDequeue(out metric))
                        throw new ApplicationException("Couldn't dequeue oldest TimeSpan metric");
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        void TrimRequestsHistory(string name)
        {
            try
            {
                MeasuredRequest request;
                while (RequestHistory[name].Count > HistoryLength)
                    if (!RequestHistory[name].TryDequeue(out request))
                        throw new ApplicationException("Couldn't dequeue oldest request");
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        //string GetStreamName(MetricType type, string metric)
        //{
        //    var typeName =
        //        type == MetricType.Counter ? "Counter"
        //        : type == MetricType.Metric ? "Metric"
        //        : type == MetricType.TimeSpanMetric ? "TimeSpanMetric"
        //        : null;

        //    if (typeName == null)
        //        throw new ArgumentException("Unknown MetricType");

        //    return $"{typeName}-{metric}";
        //}

        void AddCounter(string name)
        {
            try
            {
                if (Counters.ContainsKey(name))
                    return;

                // Counters.GetOrAdd(name, 0);

                //Counters.Add(name, new List<int> { 0 });
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        //void AddMetric(string name, IList<double> values)
        void AddMetric(string name)
        {
            try
            {
                if (Metrics.ContainsKey(name))
                    return;

                //Metrics.Add(name, new List<double> { 0 });
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        void AddTimeSpanMetric(string name)
        {
            try
            {
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        void AddMeasuredRequest(string name)
        {
            try
            {
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        public void DecrementMetric(string name)
        {
            DecrementMetric(name, 1);
        }

        public void DecrementMetric(string name, double value)
        {
            try
            {
                double metric = 0;

                lock (MetricsLock)
                {
                    if (!Metrics.TryGetValue(name, out metric))
                    {
                        if (!Metrics.TryAdd(name, -value))
                            throw new ApplicationException("Couldn't add metric");

                        if (!MetricHistory.TryAdd(name, new ConcurrentQueue<double>()))
                            throw new ApplicationException("Couldn't add metric history");
                    }
                    else if (!Metrics.TryUpdate(name, metric - value, metric))
                        throw new ApplicationException("Couldn't update metric");
                }

                TrimMetricsHistory(name);
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        public void IncrementMetric(string name)
        {
            IncrementMetric(name, 1);
        }

        public void IncrementMetric(string name, double value)
        {
            try
            {
                double metric = 0;

                lock (MetricsLock)
                {
                    if (!Metrics.TryGetValue(name, out metric))
                    {
                        if (!Metrics.TryAdd(name, value))
                            throw new ApplicationException("Couldn't add metric");

                        if (!MetricHistory.TryAdd(name, new ConcurrentQueue<double>()))
                            throw new ApplicationException("Couldn't initialize metric history");
                    }
                    else if (!Metrics.TryUpdate(name, metric + value, metric))
                        throw new ApplicationException("Couldn't update metric");
                }

                TrimMetricsHistory(name);
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                //throw;
            }
        }

        public void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            //NRClient.RecordResponseTimeMetric(string.Format(
            //"{0}\\{1}", 
            //dependencyName, commandName), 
            //(long)duration.TotalMilliseconds);
        }

        public void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            //try
            //{
            //    AddMetric(metrics);
            //    AddProperties(properties);

            //    if (!Counters.ContainsKey(eventName))
            //    {
            //        AddCounter(eventName);
            //        Counters[eventName].Add(1);
            //    }
            //    else
            //        Counters[eventName].Add(Counters[eventName][Counters[eventName].Count - 1] + 1);

            //    TrimCounterHistory(eventName);
            //}
            //catch (Exception ex)
            //{
            //    logger.TrackException(ex);
            //    throw;
            //}
        }

        public void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            //try
            //{
            //    AddMetric(metrics);
            //    //NRClient.NoticeError(exception, properties);
            //}
            //catch (Exception ex)
            //{
            //    logger.TrackException(ex);
            //    throw;
            //}
        }

        public void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            //try
            //{
            //    AddProperties(properties);

            //    if (!TimeSpanMetrics.ContainsKey(name))
            //    {
            //        AddTimeSpanMetric(name);
            //        TimeSpanMetrics.Add(name, new List<TimeSpan> { value });
            //    }
            //    else
            //        TimeSpanMetrics[name].Add(value);

            //    TrimMetricsHistory(name);
            //}
            //catch (Exception ex)
            //{
            //    logger.TrackException(ex);
            //    throw;
            //}
        }

        public void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            try
            {
                AddProperties(properties);

                if (!Metrics.ContainsKey(name))
                {
                    AddMetric(name);
                    //Metrics[name].Add(value);
                }
                //else
                //    Metrics[name].Add(value);

                TrimMetricsHistory(name);
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        public void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            try
            {
                var request = new MeasuredRequest
                {
                    Name = name,
                    StartTime = startTime,
                    Duration = duration
                };

                if (!Requests.ContainsKey(name))
                {
                    AddMeasuredRequest(name);
                    //Requests[name].Add(request);
                }
                //else
                //    Requests[name].Add(request);

                TrimRequestsHistory(name);
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        private void AddMetric(IDictionary<string, double> metrics)
        {
            try
            {
                if (metrics != null)
                {
                    metrics.AsParallel().ForAll(m =>
                    {
                        if (!Metrics.ContainsKey(m.Key))
                        {
                            AddMetric(m.Key);
                            //Metrics[m.Key].Add(m.Value);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        private void AddProperties(IDictionary<string, string> properties)
        {
            try
            {
                //if (properties != null)
                //{
                //    properties.AsParallel().ForAll(p =>
                //    {
                //        //NRClient.AddCustomParameter(p.Key, p.Value);
                //    });
                //}
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);
                throw;
            }
        }

        public void Flush() { }

        public void Close()
        {
            try
            {
                if (SamplingTimer != null)
                    SamplingTimer.Dispose();
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);

                // don't rethrow on timer dispose error
            }
        }
    }
}
