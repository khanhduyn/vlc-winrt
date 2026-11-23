using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using VLC.Model;
using VLC.ScriptPlayer.Engine;
using VLC.ScriptPlayer.Models;
using VLC.ScriptPlayer.Playback;
using VLC.ScriptPlayer.Storage;
using VLC.ViewModels;

namespace VLC.UI.Views.ScriptPlayer
{
    public sealed partial class ScriptPlayerPage : Page
    {
        public static ScriptPlayerPage CurrentInstance { get; private set; }

        private VlcVideoController _videoController;
        private CuePointTracker _cueTracker;
        private ScriptLoader _scriptLoader;
        private StateContext _context;
        private ActionDispatcher _actionDispatcher;
        private FsmEngine _fsmEngine;
        private ScriptConfig _config;

        private readonly DispatcherTimer _inputTimer;
        private GamepadButtons _previousButtons = GamepadButtons.None;
        private bool _prevLeftTriggerPressed = false;
        private bool _prevRightTriggerPressed = false;

        private readonly ObservableCollection<string> _logItems = new ObservableCollection<string>();

        public ScriptPlayerPage()
        {
            this.InitializeComponent();

            HudLogList.ItemsSource = _logItems;

            _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60 Hz
            _inputTimer.Tick += OnInputPollTick;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            CurrentInstance = this;

            // Make background SwapChainPanel visible and start composition animation
            if (App.RootPage?.SwapChainPanel != null)
            {
                App.RootPage.SwapChainPanel.Visibility = Visibility.Visible;
                App.RootPage.StartCompositionAnimationOnSwapChain(false);
            }

            // Disable VLC global keyboard listener so shortcuts don't collide
            if (Locator.MainVM?.KeyboardListenerService != null)
            {
                Locator.MainVM.KeyboardListenerService.CanListen = false;
            }

            _videoController = new VlcVideoController(Dispatcher);
            _videoController.PositionChanged += OnPositionChanged;

            _cueTracker = new CuePointTracker(_videoController);
            _scriptLoader = new ScriptLoader();

            _inputTimer.Start();
            Window.Current.CoreWindow.KeyDown += OnCoreWindowKeyDown;

            await InitializeAndLoadScriptAsync();
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            Window.Current.CoreWindow.KeyDown -= OnCoreWindowKeyDown;
            _inputTimer.Stop();

            _fsmEngine?.Detach();
            _fsmEngine = null;

            _videoController?.Stop();
            _context?.ClearTimers();

            // Re-enable VLC global keyboard listener
            if (Locator.MainVM?.KeyboardListenerService != null)
            {
                Locator.MainVM.KeyboardListenerService.CanListen = true;
            }

            // Collapse SwapChainPanel when leaving player page
            if (App.RootPage?.SwapChainPanel != null)
            {
                App.RootPage.SwapChainPanel.Visibility = Visibility.Collapsed;
            }

            CurrentInstance = null;
            base.OnNavigatingFrom(e);
        }

        public void Pause()
        {
            _videoController?.Pause();
        }

        private async Task InitializeAndLoadScriptAsync(bool forceUsbScan = false)
        {
            try
            {
                // Detach and clean up any previous instance before reloading
                _fsmEngine?.Detach();
                _fsmEngine = null;
                _videoController?.Stop();
                _context?.ClearTimers();

                _logItems.Add($"[{DateTime.Now:HH:mm:ss}] Scanning for script configurations...");

                ScriptConfig loadedConfig = null;

                // 1. Check USB if requested or available
                var (usbConfig, usbFolder) = await _scriptLoader.LoadFromUsbAsync();
                if (usbConfig != null)
                {
                    loadedConfig = usbConfig;
                    _logItems.Add($"[{DateTime.Now:HH:mm:ss}] Loaded script from USB: {usbFolder?.Name}");
                }

                // 2. Fall back to local app data if not found on USB
                if (loadedConfig == null && !forceUsbScan)
                {
                    try
                    {
                        loadedConfig = await _scriptLoader.LoadLocalScriptAsync("script.json");
                        if (loadedConfig != null)
                        {
                            _logItems.Add($"[{DateTime.Now:HH:mm:ss}] Loaded script from LocalAppData: script.json");
                        }
                    }
                    catch { }
                }

                // 3. Fall back to packaged sample script
                if (loadedConfig == null)
                {
                    loadedConfig = await _scriptLoader.LoadPackagedScriptAsync("Assets/sample_script.json");
                    _logItems.Add($"[{DateTime.Now:HH:mm:ss}] Loaded packaged sample script.");
                }

                _config = loadedConfig;
                HudScriptTitle.Text = $"\"{_config.Title}\" (v{_config.Version})";

                _context = new StateContext(Dispatcher);
                _context.LogAdded += OnContextLogAdded;
                _context.HudPromptChanged += OnHudPromptChanged;
                _context.VariableChanged += (k, v) => UpdateVariableDisplay();
                _context.StateChanged += (o, n) => UpdateFsmDisplay();

                _actionDispatcher = new ActionDispatcher(_context, _videoController, _scriptLoader, _config, Dispatcher);
                _fsmEngine = new FsmEngine(_config, _context, _videoController, _cueTracker, _actionDispatcher, Dispatcher);

                await _fsmEngine.StartAsync();
                UpdateFsmDisplay();
                UpdateVariableDisplay();
            }
            catch (Exception ex)
            {
                _logItems.Add($"[{DateTime.Now:HH:mm:ss}] [ERROR] Script load failed: {ex.Message}");
            }
        }

        private void OnInputPollTick(object sender, object e)
        {
            if (Gamepad.Gamepads.Count == 0) return;

            var gamepad = Gamepad.Gamepads[0];
            var reading = gamepad.GetCurrentReading();
            var currentButtons = reading.Buttons;

            // Debug HUD Toggle Shortcut: View + Menu (Select + Start)
            if (currentButtons.HasFlag(GamepadButtons.View) && currentButtons.HasFlag(GamepadButtons.Menu))
            {
                if (!_previousButtons.HasFlag(GamepadButtons.View) || !_previousButtons.HasFlag(GamepadButtons.Menu))
                {
                    ToggleDebugHud();
                }
            }

            // Reload script shortcut: Left Thumbstick Click
            if (CheckButtonDown(currentButtons, _previousButtons, GamepadButtons.LeftThumbstick))
            {
                _ = InitializeAndLoadScriptAsync(forceUsbScan: false);
            }

            // USB scan shortcut: Right Thumbstick Click
            if (CheckButtonDown(currentButtons, _previousButtons, GamepadButtons.RightThumbstick))
            {
                _ = InitializeAndLoadScriptAsync(forceUsbScan: true);
            }

            // Standard buttons edge detection (handled exclusively here for gamepads to prevent double-dispatch)
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.A, "input:A");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.B, "input:B");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.X, "input:X");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.Y, "input:Y");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.DPadUp, "input:DPadUp");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.DPadDown, "input:DPadDown");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.DPadLeft, "input:DPadLeft");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.DPadRight, "input:DPadRight");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.LeftShoulder, "input:LeftShoulder");
            CheckAndEmitButton(currentButtons, _previousButtons, GamepadButtons.RightShoulder, "input:RightShoulder");

            // Analog triggers as binary edges (> 0.5 threshold)
            bool ltPressed = reading.LeftTrigger > 0.5;
            if (ltPressed && !_prevLeftTriggerPressed) _fsmEngine?.PostEvent("input:LeftTrigger");
            _prevLeftTriggerPressed = ltPressed;

            bool rtPressed = reading.RightTrigger > 0.5;
            if (rtPressed && !_prevRightTriggerPressed) _fsmEngine?.PostEvent("input:RightTrigger");
            _prevRightTriggerPressed = rtPressed;

            _previousButtons = currentButtons;
        }

        private static bool CheckButtonDown(GamepadButtons current, GamepadButtons previous, GamepadButtons target)
        {
            return current.HasFlag(target) && !previous.HasFlag(target);
        }

        private void CheckAndEmitButton(GamepadButtons current, GamepadButtons previous, GamepadButtons target, string eventName)
        {
            if (CheckButtonDown(current, previous, target))
            {
                _fsmEngine?.PostEvent(eventName);
            }
        }

        private void OnCoreWindowKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            // Keyboard keys only; Gamepad* keys omitted to avoid double-firing with OnInputPollTick
            switch (args.VirtualKey)
            {
                case VirtualKey.H:
                    ToggleDebugHud();
                    break;
                case VirtualKey.Space:
                case VirtualKey.Enter:
                    _fsmEngine?.PostEvent("input:A");
                    break;
                case VirtualKey.Escape:
                case VirtualKey.B:
                    _fsmEngine?.PostEvent("input:B");
                    break;
                case VirtualKey.X:
                    _fsmEngine?.PostEvent("input:X");
                    break;
                case VirtualKey.Y:
                    _fsmEngine?.PostEvent("input:Y");
                    break;
                case VirtualKey.Up:
                    _fsmEngine?.PostEvent("input:DPadUp");
                    break;
                case VirtualKey.Down:
                    _fsmEngine?.PostEvent("input:DPadDown");
                    break;
                case VirtualKey.Left:
                    _fsmEngine?.PostEvent("input:DPadLeft");
                    break;
                case VirtualKey.Right:
                    _fsmEngine?.PostEvent("input:DPadRight");
                    break;
                case VirtualKey.R:
                    _ = InitializeAndLoadScriptAsync(false);
                    break;
                case VirtualKey.U:
                    _ = InitializeAndLoadScriptAsync(true);
                    break;
            }
        }

        private void ToggleDebugHud()
        {
            DebugHud.Visibility = DebugHud.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnExitClicked(object sender, RoutedEventArgs e)
        {
            if (!Locator.NavigationService.GoBack_Default())
            {
                Locator.NavigationService.Go(Locator.SettingsVM.HomePage);
            }
        }

        private void OnPositionChanged(long currentPosMs, long durationMs)
        {
            var curTs = TimeSpan.FromMilliseconds(currentPosMs);
            var durTs = TimeSpan.FromMilliseconds(durationMs);

            HudTimeCode.Text = $"Time: {curTs:mm\\:ss\\.fff} / {durTs:mm\\:ss\\.fff}";
            HudMediaId.Text = $"Media: {_videoController?.CurrentMedia?.Id ?? "None"}";

            if (durationMs > 0)
            {
                double pct = (double)currentPosMs / durationMs * 100.0;
                HudPlaybackProgress.Value = Math.Max(0.0, Math.Min(100.0, pct));
            }
            else
            {
                HudPlaybackProgress.Value = 0;
            }
        }

        private void OnContextLogAdded(string logMessage)
        {
            _logItems.Add(logMessage);
            if (_logItems.Count > 100)
            {
                _logItems.RemoveAt(0);
            }
            try
            {
                HudLogList.ScrollIntoView(_logItems[_logItems.Count - 1]);
            }
            catch { }
        }

        private void OnHudPromptChanged(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                PromptContainer.Visibility = Visibility.Collapsed;
            }
            else
            {
                PromptText.Text = prompt;
                PromptContainer.Visibility = Visibility.Visible;
            }
        }

        private void UpdateFsmDisplay()
        {
            if (_context == null) return;
            HudCurrentState.Text = $"State: {_context.CurrentState}";
            HudLastEvent.Text = $"Last Event: {_context.LastEvent}";
            HudActiveTimers.Text = $"Active Timers: {_context.ActiveTimers.Count}";
        }

        private void UpdateVariableDisplay()
        {
            if (_context == null) return;
            var sb = new StringBuilder();
            foreach (var kvp in _context.Variables)
            {
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");
            }
            HudVariables.Text = sb.Length > 0 ? sb.ToString() : "(No variables)";
        }
    }
}
