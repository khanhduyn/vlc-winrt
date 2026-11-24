using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.UI.Core;
using Windows.UI.Xaml;
using libVLCX;
using VLC.ScriptPlayer.Models;
using VLC.Services.RunTime;
using VLC.ViewModels;

namespace VLC.ScriptPlayer.Playback
{
    /// <summary>
    /// Playback adapter integrating interactive scripts directly into the active VideoPlayerPage playback.
    /// Wraps VLC's PlaybackService to allow scripts to observe and control the currently playing movie
    /// without requiring a separate or secondary MediaPlayer instance.
    /// </summary>
    public class PlaybackServiceVideoController : IVideoController
    {
        private readonly PlaybackService _playbackService;
        private readonly CoreDispatcher _uiDispatcher;
        private readonly DispatcherTimer _positionTimer;
        private readonly List<string> _accessTokens = new List<string>();

        private volatile int _mediaGeneration = 0;
        private bool _mediaOpenedFiredForCurrent = false;

        public MediaItem CurrentMedia { get; private set; }
        public long CurrentPositionMs => _playbackService != null ? _playbackService.Time : 0;
        public long DurationMs => _playbackService != null ? _playbackService.Length : 0;
        public bool IsPlaying => _playbackService != null && _playbackService.IsPlaying;

        public long? SegmentStartMs { get; private set; }
        public long? SegmentEndMs { get; private set; }
        public bool SegmentLoop { get; private set; }

        public event Action<string> MediaOpened;
        public event Action<string> MediaEnded;
        public event Action<string, string> MediaFailed;
        public event Action<long, long> PositionChanged;

        public PlaybackServiceVideoController(PlaybackService playbackService, CoreDispatcher uiDispatcher = null)
        {
            _playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
            _uiDispatcher = uiDispatcher ?? Window.Current?.Dispatcher;

            _playbackService.Playback_MediaPlaying += OnPlaybackMediaPlaying;
            _playbackService.Playback_MediaEndReached += OnPlaybackMediaEndReached;
            _playbackService.Playback_MediaFailed += OnPlaybackMediaFailed;
            _playbackService.Playback_MediaTimeChanged += OnPlaybackMediaTimeChanged;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _positionTimer.Tick += OnPositionTimerTick;
            _positionTimer.Start();
        }

        /// <summary>
        /// Binds the controller to the currently running movie in VideoPlayerPage without restarting playback.
        /// </summary>
        public void AttachToCurrentPlayback(string mediaId, string uri)
        {
            CurrentMedia = new MediaItem
            {
                Id = !string.IsNullOrEmpty(mediaId) ? mediaId : "current",
                Uri = uri ?? string.Empty
            };
            _mediaOpenedFiredForCurrent = true;

            if (!_positionTimer.IsEnabled)
            {
                _positionTimer.Start();
            }

            RunOnUIThread(() =>
            {
                MediaOpened?.Invoke(CurrentMedia.Id);
                PositionChanged?.Invoke(CurrentPositionMs, DurationMs);
            });
        }

        public async Task LoadAndPlayAsync(MediaItem item, StorageFile storageFile = null)
        {
            if (item == null) return;

            Interlocked.Increment(ref _mediaGeneration);
            _mediaOpenedFiredForCurrent = false;

            CurrentMedia = item;
            SegmentStartMs = item.StartMs;
            SegmentEndMs = item.EndMs;
            SegmentLoop = item.Loop;

            string mrl = null;
            FromType fromType = FromType.FromLocation;

            if (storageFile != null)
            {
                var token = StorageApplicationPermissions.FutureAccessList.Add(storageFile);
                _accessTokens.Add(token);
                mrl = "winrt://" + token;
            }
            else if (!string.IsNullOrEmpty(item.Uri))
            {
                if (item.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    item.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    mrl = item.Uri;
                }
                else
                {
                    var packageUri = item.Uri.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase)
                        ? new Uri(item.Uri)
                        : new Uri($"ms-appx:///{item.Uri.TrimStart('/')}");

                    var file = await StorageFile.GetFileFromApplicationUriAsync(packageUri);
                    var token = StorageApplicationPermissions.FutureAccessList.Add(file);
                    _accessTokens.Add(token);
                    mrl = "winrt://" + token;
                }
            }

            if (string.IsNullOrEmpty(mrl))
            {
                MediaFailed?.Invoke(item.Id, "Could not resolve media URI or storage file.");
                return;
            }

            int vol = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, item.Volume)) * 100);

            if (Locator.VideoPlayerVm != null)
            {
                Locator.VideoPlayerVm.IsScriptSwitchingMedia = true;
            }
            try
            {
                await _playbackService.PlayScriptMediaAsync(mrl, fromType, item.StartMs, item.Loop && !item.EndMs.HasValue, vol);
            }
            finally
            {
                if (Locator.VideoPlayerVm != null)
                {
                    Locator.VideoPlayerVm.IsScriptSwitchingMedia = false;
                }
            }

            if (!_positionTimer.IsEnabled)
            {
                _positionTimer.Start();
            }
        }

        public void Play()
        {
            if (_playbackService == null) return;
            _playbackService.SetPause(false);
            _playbackService.Play();
        }

        public void Pause()
        {
            _playbackService?.SetPause(true);
        }

        public void Seek(long targetMs)
        {
            if (_playbackService == null) return;
            var dur = DurationMs;
            var clamped = Math.Max(0, Math.Min(targetMs, dur > 0 ? dur : targetMs));
            _playbackService.Time = clamped;
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
            Interlocked.Increment(ref _mediaGeneration);
            _positionTimer.Stop();
            _playbackService?.Stop();
            CleanupAccessTokens();
        }

        /// <summary>
        /// Detaches script monitoring and cleanup without stopping the active movie playback.
        /// </summary>
        public void Detach()
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= OnPositionTimerTick;

            if (_playbackService != null)
            {
                _playbackService.Playback_MediaPlaying -= OnPlaybackMediaPlaying;
                _playbackService.Playback_MediaEndReached -= OnPlaybackMediaEndReached;
                _playbackService.Playback_MediaFailed -= OnPlaybackMediaFailed;
                _playbackService.Playback_MediaTimeChanged -= OnPlaybackMediaTimeChanged;
            }

            CleanupAccessTokens();
        }

        private void OnPlaybackMediaPlaying()
        {
            int gen = _mediaGeneration;
            RunOnUIThread(() =>
            {
                if (gen != _mediaGeneration) return;
                if (!_mediaOpenedFiredForCurrent)
                {
                    _mediaOpenedFiredForCurrent = true;
                    MediaOpened?.Invoke(CurrentMedia?.Id ?? "current");
                }
            });
        }

        private void OnPlaybackMediaEndReached()
        {
            int gen = _mediaGeneration;
            RunOnUIThread(() =>
            {
                if (gen != _mediaGeneration) return;
                MediaEnded?.Invoke(CurrentMedia?.Id ?? "current");
            });
        }

        private void OnPlaybackMediaFailed()
        {
            int gen = _mediaGeneration;
            RunOnUIThread(() =>
            {
                if (gen != _mediaGeneration) return;
                MediaFailed?.Invoke(CurrentMedia?.Id ?? "current", "VLC playback error encountered.");
            });
        }

        private void OnPlaybackMediaTimeChanged(long timeMs)
        {
            int gen = _mediaGeneration;
            RunOnUIThread(() =>
            {
                if (gen != _mediaGeneration) return;
                PositionChanged?.Invoke(timeMs, DurationMs);
                CheckSegmentBoundary(timeMs);
            });
        }

        private void OnPositionTimerTick(object sender, object e)
        {
            if (_playbackService == null || !_playbackService.IsPlaying) return;

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
