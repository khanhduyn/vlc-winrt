using System;
using System.Collections.Generic;
using System.Linq;
using VLC.ScriptPlayer.Models;

namespace VLC.ScriptPlayer.Playback
{
    /// <summary>
    /// Tracks time-based cue points and segment boundaries against the active video playback position.
    /// Emits events once per pass, supporting forward progression and loop/seek resets.
    /// </summary>
    public class CuePointTracker
    {
        private readonly IVideoController _videoController;
        private readonly List<CuePoint> _cues = new List<CuePoint>();
        private readonly HashSet<string> _firedCuesThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long _previousPositionMs = 0;
        private bool _segmentEndFired = false;

        public event Action<CuePoint> CueFired;
        public event Action<string> SegmentEnded;

        public CuePointTracker(IVideoController videoController)
        {
            _videoController = videoController ?? throw new ArgumentNullException(nameof(videoController));
            _videoController.PositionChanged += OnPositionChanged;
            _videoController.MediaOpened += OnMediaOpened;
        }

        public void SetCuePoints(IEnumerable<CuePoint> cues)
        {
            _cues.Clear();
            if (cues != null)
            {
                _cues.AddRange(cues.OrderBy(c => c.TimeMs));
            }
            ResetPass();
        }

        public void ResetPass()
        {
            _firedCuesThisPass.Clear();
            _previousPositionMs = 0;
            _segmentEndFired = false;
        }

        private void OnMediaOpened(string mediaId)
        {
            ResetPass();
        }

        private void OnPositionChanged(long currentPosMs, long durationMs)
        {
            var activeMedia = _videoController.CurrentMedia;
            if (activeMedia == null) return;

            // Detect seek backwards or loop wrap-around
            if (currentPosMs < _previousPositionMs - 500)
            {
                var cuesToUnmark = _cues
                    .Where(c => c.MediaId == activeMedia.Id && c.TimeMs > currentPosMs)
                    .Select(c => c.Id)
                    .ToList();

                foreach (var id in cuesToUnmark)
                {
                    _firedCuesThisPass.Remove(id);
                }

                _segmentEndFired = false;
            }

            _previousPositionMs = currentPosMs;

            // Check cues for current media
            foreach (var cue in _cues)
            {
                if (string.Equals(cue.MediaId, activeMedia.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (currentPosMs >= cue.TimeMs && !_firedCuesThisPass.Contains(cue.Id))
                    {
                        _firedCuesThisPass.Add(cue.Id);
                        CueFired?.Invoke(cue);
                    }
                }
            }

            // Check segment completion
            if (_videoController.SegmentEndMs.HasValue)
            {
                if (currentPosMs >= _videoController.SegmentEndMs.Value && !_segmentEndFired)
                {
                    _segmentEndFired = true;
                    SegmentEnded?.Invoke(activeMedia.Id);
                }
            }
        }
    }
}
