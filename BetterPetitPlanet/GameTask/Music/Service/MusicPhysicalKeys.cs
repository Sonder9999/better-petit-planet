using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicPhysicalKeys
{
    private readonly HashSet<string> _held = [];
    private readonly object _lock = new();
    public static readonly string[] Lanes = ["A", "S", "D", "F", "J", "K", "L"];

    public string[] ActiveKeys
    {
        get
        {
            lock (_lock)
            {
                return _held.OrderBy(x => Array.IndexOf(Lanes, x)).ToArray();
            }
        }
    }

    public bool Apply(string key, bool down, bool injected)
    {
        if (injected || !Lanes.Contains(key)) return false;
        lock (_lock)
        {
            return down ? _held.Add(key) : _held.Remove(key);
        }
    }

    public string[] Clear()
    {
        lock (_lock)
        {
            var keys = ActiveKeys;
            _held.Clear();
            return keys;
        }
    }
}
