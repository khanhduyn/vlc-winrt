using System;
using System.Threading.Tasks;
using Windows.Storage;
using VLC.ScriptPlayer.Models;

namespace VLC.ScriptPlayer.Playback
{
    /// <summary>
    /// Decoupled interface contract for hardware-accelerated video playback and segment tracking.
    /// </summary>
    public interface IVideoController
    {
        MediaItem CurrentMedia { get; }
        long CurrentPositionMs { get; }
        long DurationMs { get; }
        bool IsPlaying { get; }

        long? SegmentStartMs { get; }
        long? SegmentEndMs { get; }
        bool SegmentLoop { get; }

        event Action<string> MediaOpened;
        event Action<string> MediaEnded;
        event Action<string, string> MediaFailed;
        event Action<long, long> PositionChanged;

        Task LoadAndPlayAsync(MediaItem item, StorageFile storageFile = null);
        void Play();
        void Pause();
        void Seek(long targetMs);
        void SetSegment(long? startMs, long? endMs, bool loop);
        void ClearSegment();
        void Stop();
    }
}
