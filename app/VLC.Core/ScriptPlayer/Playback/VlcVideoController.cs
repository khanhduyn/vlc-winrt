using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.UI.Core;
using Windows.UI.Xaml;
using libVLCX;
using VLC.ScriptPlayer.Models;
using VLC.ViewModels;

namespace VLC.ScriptPlayer.Playback
{
    /// <summary>
    /// Hardware-accelerated video playback controller powered by libVLCX and SwapChainPanel.
    /// Provides dedicated MediaPlayer instance to prevent collisions with VLC's main playlist engine.
    /// </summary>
    public class VlcVideoController : IVideoController
    {
        private MediaPlayer _mediaPlayer;
        private readonly DispatcherTimer _positionTimer;
        private readonly CoreDispatcher _uiDispatcher;
        private readonly List<string> _accessTokens = new List<string>();

        public MediaItem CurrentMedia { get; private set; }
        public long CurrentPositionMs => _mediaPlayer != null ? _mediaPlayer.time() : 0;
        public long DurationMs => _mediaPlayer != null ? _mediaPlayer.length() : 0;
        public bool IsPlaying => _mediaPlayer != null && _mediaPlayer.isPlaying();

        public long? SegmentStartMs { get; private set; }
        public long? SegmentEndMs { get; private set; }
        public bool SegmentLoop { get; private set; }

        public event Action<string> MediaOpened;
        public event Action<string> MediaEnded;
        public event Action<string, string> MediaFailed;
        public event Action<long, long> PositionChanged;

        public VlcVideoController(CoreDispatcher uiDispatcher = null)
        {
            _uiDispatcher = uiDispatcher ?? Window.Current?.Dispatcher;
            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _positionTimer.Tick += OnPositionTimerTick;
        }

        private async Task EnsurePlayerInitializedAsync()
        {
            if (_mediaPlayer != null) return;

            // Stop any background playback in VLC main service
            Locator.PlaybackService?.Stop();

            if (Locator.PlaybackService?.Instance == null)
            {
                await Locator.PlaybackService.Initialize();
            }
            await Locator.PlaybackService.PlayerInstanceReady.Task;

            if (Locator.PlaybackService?.Instance == null)
            {
                throw new InvalidOperationException("VLC Engine instance failed to initialize.");
            }

            // Create dedicated MediaPlayer
            _mediaPlayer = new MediaPlayer(Locator.PlaybackService.Instance);

            var em = _mediaPlayer.eventManager();
            em.OnPlaying += OnVlcPlaying;
            em.OnEndReached += OnVlcEndReached;
            em.OnEncounteredError += OnVlcEncounteredError;
            em.OnTimeChanged += OnVlcTimeChanged;

            _positionTimer.Start();
        }

        public async Task LoadAndPlayAsync(MediaItem item, StorageFile storageFile = null)
        {
            if (item == null) return;
            CurrentMedia = item;
            SegmentStartMs = item.StartMs;
            SegmentEndMs = item.EndMs;
            SegmentLoop = item.Loop;

            await EnsurePlayerInitializedAsync();

            Media media = null;
            if (storageFile != null)
            {
                var token = StorageApplicationPermissions.FutureAccessList.Add(storageFile);
                _accessTokens.Add(token);
                media = new Media(Locator.PlaybackService.Instance, "winrt://" + token, FromType.FromLocation);
            }
            else if (!string.IsNullOrEmpty(item.Uri))
            {
                if (item.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    item.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    media = new Media(Locator.PlaybackService.Instance, item.Uri, FromType.FromLocation);
                }
                else
                {
                    // Resolve relative or ms-appx path
                    var packageUri = item.Uri.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase)
                        ? new Uri(item.Uri)
                        : new Uri($"ms-appx:///{item.Uri.TrimStart('/')}");

                    var file = await StorageFile.GetFileFromApplicationUriAsync(packageUri);
                    var token = StorageApplicationPermissions.FutureAccessList.Add(file);
                    _accessTokens.Add(token);
                    media = new Media(Locator.PlaybackService.Instance, "winrt://" + token, FromType.FromLocation);
                }
            }

            if (media == null)
            {
                MediaFailed?.Invoke(item.Id, "Could not resolve media URI or storage file.");
                return;
            }

            // Apply hardware decoding option
            bool hwAccel = Locator.SettingsVM != null && Locator.SettingsVM.HardwareAccelerationEnabled;
            media.addOption(hwAccel ? ":avcodec-hw=d3d11va" : ":avcodec-hw=none");

            // Apply start-time option if specified
            if (item.StartMs.HasValue && item.StartMs.Value > 0)
            {
                double startSec = item.StartMs.Value / 1000.0;
                media.addOption(string.Format(CultureInfo.InvariantCulture, ":start-time={0:F3}", startSec));
            }

            // Apply repeat option if looping without segment end
            if (item.Loop && !item.EndMs.HasValue)
            {
                media.addOption(":input-repeat=65535");
            }

            _mediaPlayer.setMedia(media);

            try
            {
                _mediaPlayer.outputDeviceSet(MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
            }
            catch { }

            int vol = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, item.Volume)) * 100);
            _mediaPlayer.setVolume(vol);

            _mediaPlayer.play();
        }

        public void Play()
        {
            if (_mediaPlayer == null) return;
            _mediaPlayer.setPause(0);
            _mediaPlayer.play();
        }

        public void Pause()
        {
            _mediaPlayer?.setPause(1);
        }

        public void Seek(long targetMs)
        {
            if (_mediaPlayer == null) return;
            var dur = DurationMs;
            var clamped = Math.Max(0, Math.Min(targetMs, dur > 0 ? dur : targetMs));
            _mediaPlayer.setTime(clamped);
        }

        public void SetSegment(long? startMs, long? endMs, bool loop)
        {
            SegmentStartMs = startMs;
            SegmentEndMs = endMs;
            SegmentLoop = loop;

            if (startMs.HasValue)
            {
                Seek(startMs.Value);
            }
        }

        public void ClearSegment()
        {
            SegmentStartMs = null;
            SegmentEndMs = null;
            SegmentLoop = false;
        }

        public void Stop()
        {
            _positionTimer.Stop();
            if (_mediaPlayer != null)
            {
                try
                {
                    _mediaPlayer.stop();
                }
                catch { }
            }
            CleanupAccessTokens();
        }

        private void OnPositionTimerTick(object sender, object e)
        {
            if (_mediaPlayer == null || !_mediaPlayer.isPlaying()) return;

            var pos = CurrentPositionMs;
            var dur = DurationMs;

            PositionChanged?.Invoke(pos, dur);
            CheckSegmentBoundary(pos);
        }

        private void CheckSegmentBoundary(long pos)
        {
            if (SegmentEndMs.HasValue && pos >= SegmentEndMs.Value)
            {
                if (SegmentLoop)
                {
                    var loopTo = SegmentStartMs ?? 0;
                    Seek(loopTo);
                }
            }
        }

        private void OnVlcPlaying()
        {
            RunOnUIThread(() =>
            {
                MediaOpened?.Invoke(CurrentMedia?.Id ?? "unknown");
            });
        }

        private void OnVlcEndReached()
        {
            RunOnUIThread(() =>
            {
                MediaEnded?.Invoke(CurrentMedia?.Id ?? "unknown");
            });
        }

        private void OnVlcEncounteredError()
        {
            RunOnUIThread(() =>
            {
                MediaFailed?.Invoke(CurrentMedia?.Id ?? "unknown", "VLC playback error encountered.");
            });
        }

        private void OnVlcTimeChanged(long timeMs)
        {
            RunOnUIThread(() =>
            {
                PositionChanged?.Invoke(timeMs, DurationMs);
                CheckSegmentBoundary(timeMs);
            });
        }

        private void CleanupAccessTokens()
        {
            foreach (var token in _accessTokens)
            {
                try
                {
                    StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
                catch { }
            }
            _accessTokens.Clear();
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
