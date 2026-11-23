using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using VLC.ScriptPlayer.Models;
using VLC.ScriptPlayer.Playback;

namespace VLC.ScriptPlayer.Engine
{
    /// <summary>
    /// Event-Condition-Action Finite State Machine engine governing playback, user interaction, and logic branching.
    /// </summary>
    public class FsmEngine
    {
        private readonly ScriptConfig _config;
        private readonly StateContext _context;
        private readonly IVideoController _videoController;
        private readonly CuePointTracker _cueTracker;
        private readonly ActionDispatcher _dispatcher;
        private readonly CoreDispatcher _uiDispatcher;
        private readonly Queue<string> _eventQueue = new Queue<string>();
        private bool _isProcessingQueue = false;

        public ScriptConfig Config => _config;
        public StateContext Context => _context;
        public IVideoController Video => _videoController;

        public FsmEngine(
            ScriptConfig config,
            StateContext context,
            IVideoController videoController,
            CuePointTracker cueTracker,
            ActionDispatcher dispatcher,
            CoreDispatcher uiDispatcher = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _videoController = videoController ?? throw new ArgumentNullException(nameof(videoController));
            _cueTracker = cueTracker ?? throw new ArgumentNullException(nameof(cueTracker));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _uiDispatcher = uiDispatcher ?? Window.Current?.Dispatcher;

            // Wire dispatcher events
            _dispatcher.RequestTransition += OnRequestTransition;
            _dispatcher.EventTriggered += OnDispatcherEventTriggered;

            // Wire playback / cue events
            _cueTracker.CueFired += OnCueFired;
            _cueTracker.SegmentEnded += OnSegmentEnded;

            _videoController.MediaOpened += OnMediaOpened;
            _videoController.MediaEnded += OnMediaEnded;
            _videoController.MediaFailed += OnMediaFailed;
        }

        public void Detach()
        {
            if (_dispatcher != null)
            {
                _dispatcher.RequestTransition -= OnRequestTransition;
                _dispatcher.EventTriggered -= OnDispatcherEventTriggered;
            }
            if (_cueTracker != null)
            {
                _cueTracker.CueFired -= OnCueFired;
                _cueTracker.SegmentEnded -= OnSegmentEnded;
            }
            if (_videoController != null)
            {
                _videoController.MediaOpened -= OnMediaOpened;
                _videoController.MediaEnded -= OnMediaEnded;
                _videoController.MediaFailed -= OnMediaFailed;
            }
            _eventQueue.Clear();
        }

        private void OnDispatcherEventTriggered(string evt) => PostEvent(evt);
        private void OnCueFired(CuePoint cue) => PostEvent($"cue:{cue.Id}");
        private void OnSegmentEnded(string mediaId) => PostEvent("segment:ended");
        private void OnMediaOpened(string mediaId) => PostEvent("media:opened");
        private void OnMediaEnded(string mediaId) => PostEvent("media:ended");
        private void OnMediaFailed(string mediaId, string err) => PostEvent("media:failed");

        public async Task StartAsync()
        {
            _context.InitializeVariables(_config.Variables);
            _cueTracker.SetCuePoints(_config.Cues);

            _context.AddLog($"[FSM] Engine started: \"{_config.Title}\" (v{_config.Version})");

            var startState = !string.IsNullOrEmpty(_config.InitialState) ? _config.InitialState : "Start";
            await TransitionToStateAsync(startState);
        }

        public void PostEvent(string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName)) return;

            RunOnUIThread(() =>
            {
                _eventQueue.Enqueue(eventName);
                ProcessEventQueue();
            });
        }

        private async void ProcessEventQueue()
        {
            if (_isProcessingQueue) return;
            _isProcessingQueue = true;

            try
            {
                while (_eventQueue.Count > 0)
                {
                    var evt = _eventQueue.Dequeue();
                    await HandleEventInternalAsync(evt);
                }
            }
            finally
            {
                _isProcessingQueue = false;
            }
        }

        private async Task HandleEventInternalAsync(string eventName)
        {
            _context.LastEvent = eventName;
            _context.AddLog($"[EVT] Process: {eventName} (State: {_context.CurrentState})");

            // 1. Check current state rules
            if (_config.States.TryGetValue(_context.CurrentState, out var currentStateDef))
            {
                if (await EvaluateRulesAsync(currentStateDef.Rules, eventName))
                {
                    return;
                }
            }

            // 2. Check global rules if not handled by state
            if (_config.GlobalRules != null && _config.GlobalRules.Count > 0)
            {
                await EvaluateRulesAsync(_config.GlobalRules, eventName);
            }
        }

        private async Task<bool> EvaluateRulesAsync(List<RuleDef> rules, string eventName)
        {
            if (rules == null || rules.Count == 0) return false;

            foreach (var rule in rules)
            {
                if (RuleEvaluator.MatchesEvent(rule, eventName))
                {
                    if (RuleEvaluator.EvaluateConditions(rule.Conditions, _context))
                    {
                        _context.AddLog($"[RULE] Matched rule for event '{eventName}'");

                        // Execute actions
                        if (rule.Actions != null)
                        {
                            foreach (var action in rule.Actions)
                            {
                                await _dispatcher.ExecuteActionAsync(action);
                            }
                        }

                        // State transition if defined
                        if (!string.IsNullOrEmpty(rule.NextState))
                        {
                            await TransitionToStateAsync(rule.NextState);
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        public async Task TransitionToStateAsync(string targetState)
        {
            if (string.IsNullOrWhiteSpace(targetState)) return;

            if (!_config.States.TryGetValue(targetState, out var newStateDef))
            {
                _context.AddLog($"[ERROR] Target state '{targetState}' does not exist!");
                return;
            }

            // Execute onExit of current state
            if (_config.States.TryGetValue(_context.CurrentState, out var oldStateDef))
            {
                if (oldStateDef.OnExit != null)
                {
                    foreach (var action in oldStateDef.OnExit)
                    {
                        await _dispatcher.ExecuteActionAsync(action);
                    }
                }
            }

            _context.SetState(targetState);

            if (newStateDef.OnEnter != null)
            {
                foreach (var action in newStateDef.OnEnter)
                {
                    await _dispatcher.ExecuteActionAsync(action);
                }
            }
        }

        private async void OnRequestTransition(string state)
        {
            await TransitionToStateAsync(state);
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
