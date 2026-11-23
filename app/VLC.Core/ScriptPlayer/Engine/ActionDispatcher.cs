using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using VLC.ScriptPlayer.Models;
using VLC.ScriptPlayer.Playback;
using VLC.ScriptPlayer.Storage;

namespace VLC.ScriptPlayer.Engine
{
    /// <summary>
    /// Executes atomic script actions against IVideoController, StateContext, and timers.
    /// </summary>
    public class ActionDispatcher
    {
        private readonly StateContext _context;
        private readonly IVideoController _videoController;
        private readonly ScriptLoader _scriptLoader;
        private readonly ScriptConfig _config;
        private readonly CoreDispatcher _uiDispatcher;

        public event Action<string> RequestTransition;
        public event Action<string> EventTriggered;

        public ActionDispatcher(
            StateContext context,
            IVideoController videoController,
            ScriptLoader scriptLoader,
            ScriptConfig config,
            CoreDispatcher uiDispatcher = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _videoController = videoController ?? throw new ArgumentNullException(nameof(videoController));
            _scriptLoader = scriptLoader;
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _uiDispatcher = uiDispatcher ?? Window.Current?.Dispatcher;
        }

        public async Task ExecuteActionAsync(ActionDef action)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Type))
            {
                return;
            }

            var type = action.Type.Trim().ToLowerInvariant();
            switch (type)
            {
                case "play":
                    await HandlePlayAsync(action);
                    break;

                case "pause":
                    _videoController.Pause();
                    _context.AddLog("[ACT] Video paused");
                    break;

                case "resume":
                    _videoController.Play();
                    _context.AddLog("[ACT] Video resumed");
                    break;

                case "seek":
                    var targetMs = action.GetParamLong("timeMs", 0);
                    _videoController.Seek(targetMs);
                    _context.AddLog($"[ACT] Seeked to {targetMs}ms");
                    break;

                case "setvar":
                    var varName = action.GetParamString("var");
                    if (!string.IsNullOrEmpty(varName) && action.Params.TryGetValue("val", out var val))
                    {
                        _context.SetVariable(varName, RuleEvaluator.ExtractValue(val));
                    }
                    break;

                case "incrementvar":
                    var incVar = action.GetParamString("var");
                    var delta = action.GetParamDouble("delta", 1.0);
                    if (!string.IsNullOrEmpty(incVar))
                    {
                        _context.IncrementVariable(incVar, delta);
                    }
                    break;

                case "starttimer":
                    HandleStartTimer(action);
                    break;

                case "stoptimer":
                    HandleStopTimer(action);
                    break;

                case "transition":
                    var targetState = action.GetParamString("state");
                    if (!string.IsNullOrEmpty(targetState))
                    {
                        RequestTransition?.Invoke(targetState);
                    }
                    break;

                case "hudmessage":
                case "hudprompt":
                    var text = action.GetParamString("text", "");
                    var duration = (int)action.GetParamLong("durationSec", 0);
                    _context.SetHudPrompt(text, duration);
                    _context.AddLog($"[HUD] \"{text}\" ({duration}s)");
                    break;

                case "log":
                    var logMsg = action.GetParamString("text", "");
                    _context.AddLog($"[USER] {logMsg}");
                    break;

                default:
                    _context.AddLog($"[WARN] Unknown action type: {action.Type}");
                    break;
            }
        }

        private async Task HandlePlayAsync(ActionDef action)
        {
            var mediaId = action.GetParamString("mediaId");
            if (string.IsNullOrEmpty(mediaId))
            {
                _videoController.Play();
                _context.AddLog("[ACT] Video play current");
                return;
            }

            if (_config.Media.TryGetValue(mediaId, out var mediaItem))
            {
                _context.ActiveMediaId = mediaId;
                _context.ResetCues();

                var storageFile = _scriptLoader != null ? await _scriptLoader.ResolveMediaFileAsync(mediaItem.Uri) : null;
                await _videoController.LoadAndPlayAsync(mediaItem, storageFile);
                _context.AddLog($"[ACT] Playing media '{mediaId}' ({mediaItem.Uri})");
            }
            else
            {
                _context.AddLog($"[WARN] Media ID '{mediaId}' not found in configuration");
            }
        }

        private void HandleStartTimer(ActionDef action)
        {
            var timerId = action.GetParamString("timerId");
            var durationMs = action.GetParamLong("durationMs", 1000);

            if (string.IsNullOrEmpty(timerId)) return;

            RunOnUIThread(() =>
            {
                // Stop existing if any
                if (_context.ActiveTimers.TryGetValue(timerId, out var existing))
                {
                    existing.Stop();
                    _context.ActiveTimers.Remove(timerId);
                }

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(durationMs)
                };

                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _context.ActiveTimers.Remove(timerId);
                    _context.AddLog($"[TIMER] Fired: {timerId}");
                    EventTriggered?.Invoke($"timer:{timerId}");
                };

                _context.ActiveTimers[timerId] = timer;
                timer.Start();
                _context.AddLog($"[ACT] Started timer '{timerId}' for {durationMs}ms");
            });
        }

        private void HandleStopTimer(ActionDef action)
        {
            var timerId = action.GetParamString("timerId");
            if (string.IsNullOrEmpty(timerId)) return;

            RunOnUIThread(() =>
            {
                if (_context.ActiveTimers.TryGetValue(timerId, out var timer))
                {
                    timer.Stop();
                    _context.ActiveTimers.Remove(timerId);
                    _context.AddLog($"[ACT] Stopped timer '{timerId}'");
                }
            });
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
