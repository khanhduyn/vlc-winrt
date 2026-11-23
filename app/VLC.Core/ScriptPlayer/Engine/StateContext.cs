using System;
using System.Collections.Generic;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace VLC.ScriptPlayer.Engine
{
    /// <summary>
    /// Thread-safe runtime state store containing variables, timers, current FSM state, and telemetry logs.
    /// </summary>
    public class StateContext
    {
        private readonly object _lock = new object();
        private readonly CoreDispatcher _uiDispatcher;

        public string CurrentState { get; set; } = string.Empty;
        public string PreviousState { get; set; } = string.Empty;
        public string ActiveMediaId { get; set; } = string.Empty;
        public string LastEvent { get; set; } = string.Empty;
        public string HudPrompt { get; private set; } = string.Empty;

        public Dictionary<string, object> Variables { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DispatcherTimer> ActiveTimers { get; } = new Dictionary<string, DispatcherTimer>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FiredCuesThisPass { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> LogHistory { get; } = new List<string>();

        public event Action<string, string> StateChanged;
        public event Action<string, object> VariableChanged;
        public event Action<string> LogAdded;
        public event Action<string> HudPromptChanged;

        public StateContext(CoreDispatcher uiDispatcher = null)
        {
            _uiDispatcher = uiDispatcher ?? Window.Current?.Dispatcher;
        }

        public void InitializeVariables(Dictionary<string, object> initialVars)
        {
            lock (_lock)
            {
                Variables.Clear();
                if (initialVars != null)
                {
                    foreach (var kvp in initialVars)
                    {
                        Variables[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        public void SetState(string newState)
        {
            string oldState;
            lock (_lock)
            {
                oldState = CurrentState;
                PreviousState = oldState;
                CurrentState = newState;
                AddLog($"[FSM] State: {oldState} -> {newState}");
            }
            StateChanged?.Invoke(oldState, newState);
        }

        public void SetVariable(string name, object value)
        {
            lock (_lock)
            {
                Variables[name] = value;
                AddLog($"[VAR] {name} = {value}");
            }
            VariableChanged?.Invoke(name, value);
        }

        public object GetVariable(string name)
        {
            lock (_lock)
            {
                if (Variables.TryGetValue(name, out var val))
                {
                    return val;
                }
                return null;
            }
        }

        public void IncrementVariable(string name, double delta)
        {
            lock (_lock)
            {
                double current = 0.0;
                if (Variables.TryGetValue(name, out var val) && val != null)
                {
                    if (val is double d) current = d;
                    else if (val is int i) current = i;
                    else if (val is long l) current = l;
                    else double.TryParse(val.ToString(), out current);
                }
                double updated = current + delta;
                Variables[name] = updated;
                AddLog($"[VAR] {name} += {delta} (now {updated})");
                VariableChanged?.Invoke(name, updated);
            }
        }

        public void SetHudPrompt(string prompt, int durationSeconds = 0)
        {
            HudPrompt = prompt ?? string.Empty;
            HudPromptChanged?.Invoke(HudPrompt);

            if (durationSeconds > 0 && !string.IsNullOrEmpty(HudPrompt))
            {
                RunOnUIThread(() =>
                {
                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(durationSeconds)
                    };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        if (HudPrompt == prompt)
                        {
                            HudPrompt = string.Empty;
                            HudPromptChanged?.Invoke(string.Empty);
                        }
                    };
                    timer.Start();
                });
            }
        }

        public void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            lock (_lock)
            {
                LogHistory.Add(line);
                if (LogHistory.Count > 150)
                {
                    LogHistory.RemoveAt(0);
                }
            }
            LogAdded?.Invoke(line);
        }

        public void ClearTimers()
        {
            RunOnUIThread(() =>
            {
                lock (_lock)
                {
                    foreach (var timer in ActiveTimers.Values)
                    {
                        timer.Stop();
                    }
                    ActiveTimers.Clear();
                }
            });
        }

        public void ResetCues()
        {
            lock (_lock)
            {
                FiredCuesThisPass.Clear();
            }
        }

        private void RunOnUIThread(Action action)
        {
            if (_uiDispatcher == null || _uiDispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                var _ = _uiDispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
            }
        }
    }
}
