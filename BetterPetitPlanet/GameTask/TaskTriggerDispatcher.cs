using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask;

public sealed class TaskTriggerDispatcher
{
    private readonly ILogger<TaskTriggerDispatcher>? _logger;
    private readonly List<ITaskTrigger> _triggers = [];
    private readonly object _lock = new();

    public TaskTriggerDispatcher(ILogger<TaskTriggerDispatcher>? logger = null)
    {
        _logger = logger;
    }

    public void RegisterTrigger(ITaskTrigger trigger)
    {
        lock (_lock)
        {
            if (!_triggers.Contains(trigger))
            {
                _triggers.Add(trigger);
                _triggers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                _logger?.LogInformation("Registered trigger: {TriggerName}, Priority: {Priority}", trigger.Name, trigger.Priority);
            }
        }
    }

    public void UnregisterTrigger(ITaskTrigger trigger)
    {
        lock (_lock)
        {
            _triggers.Remove(trigger);
            _logger?.LogInformation("Unregistered trigger: {TriggerName}", trigger.Name);
        }
    }

    public void Dispatch(CaptureContent content)
    {
        List<ITaskTrigger> activeTriggers;
        lock (_lock)
        {
            activeTriggers = _triggers.Where(t => t.IsEnabled).ToList();
        }

        foreach (var trigger in activeTriggers)
        {
            try
            {
                trigger.OnCapture(content);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing trigger: {TriggerName}", trigger.Name);
            }
        }
    }
}
