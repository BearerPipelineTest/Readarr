using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Reflection;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.HealthCheck
{
    public interface IHealthCheckService
    {
        List<HealthCheck> Results();
    }

    public class HealthCheckService : IHealthCheckService,
                                      IExecute<CheckHealthCommand>,
                                      IHandleAsync<ApplicationStartedEvent>,
                                      IHandleAsync<IEvent>
    {
        private readonly IProvideHealthCheck[] _healthChecks;
        private readonly IProvideHealthCheck[] _startupHealthChecks;
        private readonly IProvideHealthCheck[] _scheduledHealthChecks;
        private readonly Dictionary<Type, IEventDrivenHealthCheck[]> _eventDrivenHealthChecks;
        private readonly IServerSideNotificationService _serverSideNotificationService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ICacheManager _cacheManager;
        private readonly Logger _logger;

        private readonly ICached<HealthCheck> _healthCheckResults;

        public HealthCheckService(IEnumerable<IProvideHealthCheck> healthChecks,
                                  IServerSideNotificationService serverSideNotificationService,
                                  IEventAggregator eventAggregator,
                                  ICacheManager cacheManager,
                                  Logger logger)
        {
            _healthChecks = healthChecks.ToArray();
            _serverSideNotificationService = serverSideNotificationService;
            _eventAggregator = eventAggregator;
            _cacheManager = cacheManager;
            _logger = logger;

            _healthCheckResults = _cacheManager.GetCache<HealthCheck>(GetType());

            _startupHealthChecks = _healthChecks.Where(v => v.CheckOnStartup).ToArray();
            _scheduledHealthChecks = _healthChecks.Where(v => v.CheckOnSchedule).ToArray();
            _eventDrivenHealthChecks = GetEventDrivenHealthChecks();
        }

        public List<HealthCheck> Results()
        {
            return _healthCheckResults.Values.ToList();
        }

        private Dictionary<Type, IEventDrivenHealthCheck[]> GetEventDrivenHealthChecks()
        {
            return _healthChecks
                .SelectMany(h => h.GetType().GetAttributes<CheckOnAttribute>().Select(a =>
                {
                    var eventDrivenType = typeof(EventDrivenHealthCheck<>).MakeGenericType(a.EventType);
                    var eventDriven = (IEventDrivenHealthCheck)Activator.CreateInstance(eventDrivenType, h, a.Condition);

                    return Tuple.Create(a.EventType, eventDriven);
                }))
                .GroupBy(t => t.Item1, t => t.Item2)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        private void PerformHealthCheck(IProvideHealthCheck[] healthChecks, IEvent message = null, bool performServerChecks = false)
        {
            var results = new List<HealthCheck>();

            foreach (var healthCheck in healthChecks)
            {
                if (healthCheck is IProvideHealthCheckWithMessage && message != null)
                {
                    results.Add(((IProvideHealthCheckWithMessage)healthCheck).Check(message));
                }
                else
                {
                    results.Add(healthCheck.Check());
                }
            }

            if (performServerChecks)
            {
                results.AddRange(_serverSideNotificationService.GetServerChecks());
            }

            foreach (var result in results)
            {
                if (result.Type == HealthCheckResult.Ok)
                {
                    _healthCheckResults.Remove(result.Source.Name);
                }
                else
                {
                    if (_healthCheckResults.Find(result.Source.Name) == null)
                    {
                        _eventAggregator.PublishEvent(new HealthCheckFailedEvent(result));
                    }

                    _healthCheckResults.Set(result.Source.Name, result);
                }
            }

            _eventAggregator.PublishEvent(new HealthCheckCompleteEvent());
        }

        public void Execute(CheckHealthCommand message)
        {
            if (message.Trigger == CommandTrigger.Manual)
            {
                PerformHealthCheck(_healthChecks, null, true);
            }
            else
            {
                PerformHealthCheck(_scheduledHealthChecks, null, true);
            }
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            PerformHealthCheck(_startupHealthChecks, null, true);
        }

        public void HandleAsync(IEvent message)
        {
            if (message is HealthCheckCompleteEvent)
            {
                return;
            }

            IEventDrivenHealthCheck[] checks;
            if (!_eventDrivenHealthChecks.TryGetValue(message.GetType(), out checks))
            {
                return;
            }

            var filteredChecks = new List<IProvideHealthCheck>();
            var healthCheckResults = _healthCheckResults.Values.ToList();

            foreach (var eventDrivenHealthCheck in checks)
            {
                var healthCheckType = eventDrivenHealthCheck.HealthCheck.GetType();
                var previouslyFailed = healthCheckResults.Any(r => r.Source == healthCheckType);

                if (eventDrivenHealthCheck.ShouldExecute(message, previouslyFailed))
                {
                    filteredChecks.Add(eventDrivenHealthCheck.HealthCheck);
                    continue;
                }
            }

            // TODO: Add debounce
            PerformHealthCheck(filteredChecks.ToArray(), message);
        }
    }
}
