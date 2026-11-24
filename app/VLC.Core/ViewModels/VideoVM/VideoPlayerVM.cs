/**********************************************************************
 * VLC for WinRT
 **********************************************************************
 * Copyright © 2013-2014 VideoLAN and Authors
 *
 * Licensed under GPLv2+ and MPLv2
 * Refer to COPYING file of the official project for license
 **********************************************************************/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using VLC.Commands;
using VLC.Helpers;
using VLC.Model.Video;
using VLC.Utils;
using Windows.Storage;
using libVLCX;
using Windows.Graphics.Display;
using VLC.Commands.VideoPlayer;
using VLC.Model;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Projection = libVLCX.Projection;
using Windows.Foundation.Metadata;
using System.Diagnostics;
using Windows.Storage.Search;
using Windows.UI.Xaml;
using VLC.ScriptPlayer.Engine;
using VLC.ScriptPlayer.Models;
using VLC.ScriptPlayer.Playback;
using VLC.ScriptPlayer.Storage;

namespace VLC.ViewModels.VideoVM
{
    public class VideoPlayerVM : BindableBase
    {
        #region events
        public event EventHandler<bool> PlayerControlVisibilityChangeRequested;
        public event EventHandler PlayerControlVisibilityExtendCurrentRequested;
        #endregion
        #region private props
        private VideoItem _currentVideo;

        private VLCSurfaceZoom currentSurfaceZoom = VLCSurfaceZoom.SURFACE_BEST_FIT;
        private bool isVideoPlayerOptionsPanelVisible;
        private List<VLCSurfaceZoom> zooms;

        private bool _isLoadingSubtitle;
        private string _loadingSubtitleText;
        public bool PlayerControlVisibility { get; private set; } = true;
        #endregion

        #region private fields
        #endregion

        #region public props
        public VideoItem CurrentVideo
        {
            get { return _currentVideo; }
            set { SetProperty(ref _currentVideo, value); }
        }

        public VLCSurfaceZoom CurrentSurfaceZoom
        {
            get
            {
                return currentSurfaceZoom;
            }
            set
            {
                SetProperty(ref currentSurfaceZoom, value);
                ChangeSurfaceZoom(value);
            }
        }

        public bool IsVideoPlayerOptionsPanelVisible
        {
            get { return isVideoPlayerOptionsPanelVisible; }
            set { SetProperty(ref isVideoPlayerOptionsPanelVisible, value); }
        }

        public ActionCommand ToggleIsVideoPlayerOptionsPanelVisible { get; private set; } = new ActionCommand(() =>
        {
            Locator.NavigationService.Go(VLCPage.VideoPlayerOptionsPanel);
            Locator.VideoPlayerVm.IsVideoPlayerOptionsPanelVisible = false;
        });

        public SurfaceZoomToggleCommand SurfaceZoomToggleCommand { get; private set; } = new SurfaceZoomToggleCommand();

        public InitPiPCommand InitPiPCommand { get; private set; } = new InitPiPCommand();

        public bool IsCompactOverlaySupported {
            get { return ApiInformation.IsEnumNamedValuePresent("Windows.UI.ViewManagement.ApplicationViewMode", "CompactOverlay") && ApplicationView.GetForCurrentView().IsViewModeSupported(ApplicationViewMode.CompactOverlay); }
        }

        public DownloadSubtitleCommand DownloadSubtitleCommand { get; private set; } = new DownloadSubtitleCommand();

        public ShowSubtitlesSettingsCommand ShowSubtitlesSettingsCommand { get; private set; } = new ShowSubtitlesSettingsCommand();
        public ShowAudioTracksSettingsCommand ShowAudioTracksSettingsCommand { get; private set; } = new ShowAudioTracksSettingsCommand();
        public ShowChaptersSettingsCommand ShowChaptersSettingsCommand { get; private set; } = new ShowChaptersSettingsCommand();
        public ShowScriptSettingsCommand ShowScriptSettingsCommand { get; private set; } = new ShowScriptSettingsCommand();
        public OpenScriptCommand OpenScriptCommand { get; private set; } = new OpenScriptCommand();
        public ActionCommand UnloadScriptCommand { get; private set; }
        public bool IsLoadingSubtitle { get { return _isLoadingSubtitle; } set { SetProperty(ref _isLoadingSubtitle, value); } }
        public string LoadingSubtitleText { get { return _loadingSubtitleText; } set { SetProperty(ref _loadingSubtitleText, value); } }
        public ActionCommand PlayPauseCommand { get; } = new ActionCommand(() => Locator.MediaPlaybackViewModel.PlaybackService.Pause());
        public ActionCommand GoBackCommand { get; } = new ActionCommand(() => Locator.MediaPlaybackViewModel.GoBack.Execute(null));
        public ActionCommand MuteCommand { get; } = new ActionCommand(() => Locator.MediaPlaybackViewModel.ChangeVolumeCommand.Execute("mute"));
        public ActionCommand ToggleFullscreenCommand { get; } = new ActionCommand(AppViewHelper.ToggleFullscreen);
        public ActionCommand ZoomCommand { get; } = new ActionCommand(() => Locator.VideoPlayerVm.ToggleIsVideoPlayerOptionsPanelVisible.Execute(null));
        public ActionCommand IncreaseSpeedCommand { get; } = new ActionCommand(() => Locator.MediaPlaybackViewModel.ChangePlaybackSpeedRateCommand.Execute("faster"));
        public ActionCommand DecreaseSpeedCommand { get; } = new ActionCommand(() => Locator.MediaPlaybackViewModel.ChangePlaybackSpeedRateCommand.Execute("slower"));
        public ActionCommand ResetSpeedCommand { get; } = new ActionCommand(() => Locator.MediaPlaybackViewModel.ChangePlaybackSpeedRateCommand.Execute("reset"));

        private ScriptConfig _loadedScript;
        private StorageFile _loadedScriptFile;
        private StorageFolder _loadedScriptFolder;
        private ScriptLoader _loadedScriptLoader;
        private bool _isScriptActive = false;
        private bool _isScriptDebugHudVisible = false;
        private bool _isScriptSwitchingMedia = false;

        public ScriptConfig LoadedScript
        {
            get => _loadedScript;
            private set => SetProperty(ref _loadedScript, value);
        }

        public StorageFile LoadedScriptFile
        {
            get => _loadedScriptFile;
            private set => SetProperty(ref _loadedScriptFile, value);
        }

        public StorageFolder LoadedScriptFolder
        {
            get => _loadedScriptFolder;
            private set => SetProperty(ref _loadedScriptFolder, value);
        }

        public bool HasLoadedScript => LoadedScript != null;

        public string LoadedScriptDisplayName
        {
            get
            {
                if (LoadedScript == null) return "No script loaded";
                var title = !string.IsNullOrWhiteSpace(LoadedScript.Title) ? LoadedScript.Title : "Untitled Script";
                var fileName = LoadedScriptFile != null ? LoadedScriptFile.Name : "";
                return !string.IsNullOrEmpty(fileName) ? $"{title} ({fileName})" : title;
            }
        }

        public string ScriptStatusText
        {
            get
            {
                if (!HasLoadedScript) return "No script loaded";
                return IsScriptActive ? "Active" : "Loaded (Inactive)";
            }
        }

        public bool IsScriptActive
        {
            get => _isScriptActive;
            set
            {
                if (_isScriptActive == value) return;
                if (value)
                {
                    if (HasLoadedScript)
                    {
                        var _ = ActivateScriptAsync();
                    }
                }
                else
                {
                    DeactivateScript();
                }
            }
        }

        public bool IsScriptDebugHudVisible
        {
            get => _isScriptDebugHudVisible;
            set => SetProperty(ref _isScriptDebugHudVisible, value);
        }

        public bool IsScriptSwitchingMedia
        {
            get => _isScriptSwitchingMedia;
            set => SetProperty(ref _isScriptSwitchingMedia, value);
        }

        private PlaybackServiceVideoController _scriptVideoController;
        private CuePointTracker _scriptCueTracker;
        private StateContext _scriptStateContext;
        private ActionDispatcher _scriptActionDispatcher;
        private FsmEngine _scriptFsmEngine;

        public FsmEngine ActiveFsmEngine => _scriptFsmEngine;
        public StateContext ActiveStateContext => _scriptStateContext;
        public PlaybackServiceVideoController ActiveVideoController => _scriptVideoController;

        public event Action<string> ScriptHudPromptChanged;
        public event Action<string> ScriptLogAdded;
        public event Action ScriptStateUpdated;
        public event Action<long, long> ScriptPositionUpdated;
        public event Action ScriptActivated;
        public event Action ScriptDeactivated;

        #endregion

        #region public fields

        public List<VLCSurfaceZoom> Zooms
        {
            get
            {
                if (zooms == null || !zooms.Any())
                {
                    zooms = Enum.GetValues(typeof(VLCSurfaceZoom)).Cast<VLCSurfaceZoom>().ToList();
                }
                return zooms;
            }
        }
        #endregion

        #region constructors
        public VideoPlayerVM()
        {
            UnloadScriptCommand = new ActionCommand(UnloadScript);
            Locator.MediaPlaybackViewModel.PlaybackService.Playback_MediaSet += PlaybackService_Playback_MediaSet;
        }

        private void PlaybackService_Playback_MediaFileNotFound(IMediaItem media)
        {
            if (!(media is VideoItem))
                return;

            (media as VideoItem).IsAvailable = false;
            Locator.MediaLibrary.UpdateVideo(media as VideoItem);
        }
        #endregion

        #region methods
        public void OnNavigatedTo()
        {
            // If no playback was ever started, ContinueIndexing can be null
            // If we navigate back and forth to the main page, we also don't want to 
            // re-mark the task as completed.
            PlayerControlVisibility = true;
            Locator.MediaLibrary.ContinueIndexing = new TaskCompletionSource<bool>();
            DeviceHelper.PrivateDisplayCall(true);
            Locator.Slideshow.IsPaused = true;
            if (Locator.SettingsVM.ForceLandscape && DeviceHelper.GetDeviceType() != DeviceTypeEnum.Xbox)
            {
                DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;
            }

            if (Locator.PlaybackService.CurrentPlaybackMedia is VideoItem)
                Task.Run(async () => await UpdateCurrentVideo(Locator.PlaybackService.CurrentPlaybackMedia as VideoItem));

            Locator.MediaPlaybackViewModel.PlaybackService.Playback_MediaFileNotFound += PlaybackService_Playback_MediaFileNotFound;

            var media = Locator.PlaybackService.CurrentPlaybackMedia;
            // this could be a stream instead of a videoitem..
            if(media != null)
                Task.Run(() => Locator.MediaPlaybackViewModel.SetMediaTransportControlsInfo(media.Name, (media as VideoItem)?.PictureUri));
        }

        public void OnNavigatedFrom()
        {
            UnloadScript();
            _lastAutoLoadedScriptVideoPath = null;

            if (Locator.MediaLibrary.ContinueIndexing != null && !Locator.MediaLibrary.ContinueIndexing.Task.IsCompleted)
            {
                Locator.MediaLibrary.ContinueIndexing.TrySetResult(true);
            }
            Locator.VideoPlayerVm.IsVideoPlayerOptionsPanelVisible = false;
            Locator.Slideshow.IsPaused = false;
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.None;
            DeviceHelper.PrivateDisplayCall(false);
            LoadingSubtitleText = string.Empty;

            Locator.MediaPlaybackViewModel.PlaybackService.Playback_MediaFileNotFound -= PlaybackService_Playback_MediaFileNotFound;
        }

        public async Task<bool> TryUseSubtitleFromFolder()
        {
            // Trying to get the path of the current video
            string videoPath;
            if (CurrentVideo.File != null)
            {
                videoPath = CurrentVideo.File.Path;
            }
            else if (!string.IsNullOrEmpty(CurrentVideo.Path))
            {
                videoPath = CurrentVideo.Path;
            }
            else return false;

            string subtitlesFolderPath; // assuming subtitle folder is the same as video folder for autoloading
            string fileNameWithoutExtensions;
            try
            {
                subtitlesFolderPath = System.IO.Path.GetDirectoryName(videoPath);
                fileNameWithoutExtensions = System.IO.Path.GetFileNameWithoutExtension(videoPath);
            }
            catch
            {
                return false;
            }
            try
            {
                // Since we checked Video Libraries capability and SD Card compatibility, and DLNA discovery
                // I think WinRT will let us create a StorageFolder instance of the parent folder of the file we're playing
                // Unfortunately, if the video is opened via a filepicker AND that the video is in an unusual folder, like C:/randomfolder/
                // This might now work, hence the try catch                
                var storageFolderParent = await StorageFolder.GetFolderFromPathAsync(subtitlesFolderPath);
                var subs = await LocateSubtitlesFile(storageFolderParent, fileNameWithoutExtensions);
                if(subs != null)
                {
                    Locator.MediaPlaybackViewModel.OpenSubtitleCommand.Execute(subs);
                    return true;
                }
            }
            catch
            {
                // Folder is not accessible cause outside of the sandbox
                // OR
                // File doesn't exist
            }

            // last chance: external device search
            try
            {
                var externalDevices = KnownFolders.RemovableDevices;

                // Get the first child folder, which represents the SD card, USB hard drive or other external device.
                var sdCard = (await externalDevices.GetFoldersAsync()).FirstOrDefault();
                if (sdCard == null) return false; // no external device

                var subtitlesFolder = await LocateSubtitlesFolder(sdCard, subtitlesFolderPath);
                if (subtitlesFolder == null) return false;

                var subtitlesFile = await LocateSubtitlesFile(subtitlesFolder, fileNameWithoutExtensions);
                if (subtitlesFile == null) return false;

                Locator.MediaPlaybackViewModel.OpenSubtitleCommand.Execute(subtitlesFile);
                return true;
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// returns first match for autoload feature. Go manually if you want a specific one out of several subs with same name
        /// </summary>
        async Task<StorageFile> LocateSubtitlesFile(StorageFolder subtitlesFolder, string fileNameWithoutExtensions)
        {
            var queryResult = subtitlesFolder.CreateFileQueryWithOptions(new QueryOptions(CommonFileQuery.OrderByName, VLCFileExtensions.SubtitleExtensions));
            var matches = await queryResult.GetFilesAsync();
            return matches.FirstOrDefault(file => file.DisplayName.Equals(fileNameWithoutExtensions));
        }

        async Task<StorageFolder> LocateSubtitlesFolder(StorageFolder startFolder, string subtitlesFolderPath)
        {
            var query = startFolder.CreateItemQuery();
            query.ApplyNewQueryOptions(new QueryOptions(CommonFileQuery.DefaultQuery, VLCFileExtensions.SubtitleExtensions));
            var items = await query.GetItemsAsync();
            return items.FirstOrDefault(i => i.Path.Equals(subtitlesFolderPath)) as StorageFolder;
        }

        public void ChangeSurfaceZoom(VLCSurfaceZoom desiredZoom)
        {
            var playbackService = Locator.PlaybackService;
            var screenWidth = App.RootPage.SwapChainPanel.ActualWidth;
            var screenHeight = App.RootPage.SwapChainPanel.ActualHeight;
            
            var videoTrack = playbackService.CurrentMedia?.tracks()?.FirstOrDefault(x => x.type() == TrackType.Video);

            if (videoTrack == null)
                return;
            
            switch (desiredZoom)
            {
                case VLCSurfaceZoom.SURFACE_BEST_FIT:
                    playbackService.VideoAspectRatio = string.Empty;
                    playbackService.VideoScale = 0;
                    break;
                case VLCSurfaceZoom.SURFACE_FIT_SCREEN:
                    var videoW = videoTrack.width();
                    var videoH = videoTrack.height();
                    
                    if (videoTrack.sarNum() != videoTrack.sarDen())
                        videoW = videoW * videoTrack.sarNum() / videoTrack.sarDen();

                    var ar = videoW / (float)videoH;
                    var dar = screenWidth / screenHeight;

                    float scale;
                    if (dar >= ar)
                        scale = (float)screenWidth / videoW; /* horizontal */
                    else
                        scale = (float)screenHeight / videoH; /* vertical */

                    playbackService.VideoScale = scale;
                    playbackService.VideoAspectRatio = string.Empty;
                    break;
                case VLCSurfaceZoom.SURFACE_FILL:
                    playbackService.VideoScale = 0;
                    playbackService.VideoAspectRatio = $"{screenWidth}:{screenHeight}";
                    break;
                case VLCSurfaceZoom.SURFACE_16_9:
                    playbackService.VideoAspectRatio = "16:9";
                    playbackService.VideoScale = 0;
                    break;
                case VLCSurfaceZoom.SURFACE_4_3:
                    playbackService.VideoAspectRatio = "4:3";
                    playbackService.VideoScale = 0;
                    break;
                case VLCSurfaceZoom.SURFACE_ORIGINAL:
                    playbackService.VideoAspectRatio = string.Empty;
                    playbackService.VideoScale = 1;
                    break;
                case VLCSurfaceZoom.SURFACE_2_35_1:
                    playbackService.VideoScale = 0;
                    playbackService.VideoAspectRatio = "21:9"; /* equals 2.35:1 */
                    break;
            }
        }

        public void RequestChangeControlBarVisibility(bool visibility)
        {
            PlayerControlVisibilityChangeRequested?.Invoke(this, visibility);
        }

        public void RequestExtendCurrentControlBarVisibility()
        {
            PlayerControlVisibilityExtendCurrentRequested?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        #region events

        private async void PlaybackService_Playback_MediaSet(IMediaItem media)
        {
            if (!(media is VideoItem))
                return;

            var video = (VideoItem) media;

            await UpdateCurrentVideo(video);    
        }

        private async Task UpdateCurrentVideo(VideoItem video)
        {
            await DispatchHelper.InvokeInUIThread(CoreDispatcherPriority.Normal, () =>
            {
                Locator.VideoPlayerVm.CurrentVideo = video;
                if (video != null)
                    AppViewHelper.SetTitleBarTitle(video.Name);
            });
            if (video != null)
            {
                await TryUseSubtitleFromFolder();
                await TryLoadScriptFromFolderAsync();

                var currentVideoTrackid = Locator.PlaybackService.VideotrackId;
                if (currentVideoTrackid != -1)
                {
                    var currentMediaTrack = Locator.PlaybackService.CurrentMedia?.tracks()?
                        .FirstOrDefault(t => t.id() == currentVideoTrackid);
                    Is3DVideo = currentMediaTrack?.projection() == Projection.Equirectangular;
                }
            }
        }

        #region script handling methods

        private string _lastAutoLoadedScriptVideoPath = null;

        public async Task<bool> TryLoadScriptFromFolderAsync()
        {
            string videoPath;
            if (CurrentVideo?.File != null)
            {
                videoPath = CurrentVideo.File.Path;
            }
            else if (!string.IsNullOrEmpty(CurrentVideo?.Path))
            {
                videoPath = CurrentVideo.Path;
            }
            else return false;

            if (string.Equals(_lastAutoLoadedScriptVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            _lastAutoLoadedScriptVideoPath = videoPath;

            UnloadScript();

            string folderPath;
            string fileNameWithoutExtension;
            try
            {
                folderPath = System.IO.Path.GetDirectoryName(videoPath);
                fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(videoPath);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(fileNameWithoutExtension)) return false;

            // 1. Try CurrentVideo.File.GetParentAsync()
            try
            {
                if (CurrentVideo.File != null)
                {
                    var parent = await CurrentVideo.File.GetParentAsync();
                    if (parent != null)
                    {
                        var scriptFile = await LocateScriptFile(parent, fileNameWithoutExtension);
                        if (scriptFile != null)
                        {
                            var loaded = await LoadScriptFileAsync(scriptFile, parent);
                            if (loaded)
                            {
                                ToastHelper.Basic($"Script loaded (inactive): {LoadedScriptDisplayName}");
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Try StorageFolder.GetFolderFromPathAsync(folderPath)
            try
            {
                if (!string.IsNullOrEmpty(folderPath))
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    if (folder != null)
                    {
                        var scriptFile = await LocateScriptFile(folder, fileNameWithoutExtension);
                        if (scriptFile != null)
                        {
                            var loaded = await LoadScriptFileAsync(scriptFile, folder);
                            if (loaded)
                            {
                                ToastHelper.Basic($"Script loaded (inactive): {LoadedScriptDisplayName}");
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Scan all removable devices
            try
            {
                var removableRoot = KnownFolders.RemovableDevices;
                var drives = await removableRoot.GetFoldersAsync();
                foreach (var drive in drives)
                {
                    try
                    {
                        var scriptFolder = await LocateFolderRecursive(drive, folderPath);
                        var searchFolder = scriptFolder ?? drive;
                        var scriptFile = await LocateScriptFile(searchFolder, fileNameWithoutExtension);
                        if (scriptFile != null)
                        {
                            var loaded = await LoadScriptFileAsync(scriptFile, searchFolder);
                            if (loaded)
                            {
                                ToastHelper.Basic($"Script loaded (inactive): {LoadedScriptDisplayName}");
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private async Task<StorageFile> LocateScriptFile(StorageFolder folder, string fileNameWithoutExtension)
        {
            if (folder == null || string.IsNullOrEmpty(fileNameWithoutExtension)) return null;
            try
            {
                var item = await folder.TryGetItemAsync($"{fileNameWithoutExtension}.json");
                if (item is StorageFile exactFile)
                {
                    return exactFile;
                }

                var queryResult = folder.CreateFileQueryWithOptions(new QueryOptions(CommonFileQuery.OrderByName, VLCFileExtensions.ScriptExtensions));
                var files = await queryResult.GetFilesAsync();
                return files.FirstOrDefault(f => f.DisplayName.Equals(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase)
                                              || f.Name.Equals($"{fileNameWithoutExtension}.json", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private async Task<StorageFolder> LocateFolderRecursive(StorageFolder startFolder, string targetFolderPath)
        {
            if (startFolder == null || string.IsNullOrEmpty(targetFolderPath)) return null;
            try
            {
                if (string.Equals(startFolder.Path, targetFolderPath, StringComparison.OrdinalIgnoreCase))
                    return startFolder;

                var query = startFolder.CreateItemQuery();
                query.ApplyNewQueryOptions(new QueryOptions(CommonFileQuery.DefaultQuery, VLCFileExtensions.ScriptExtensions));
                var items = await query.GetItemsAsync();
                return items.FirstOrDefault(i => i is StorageFolder && string.Equals(i.Path, targetFolderPath, StringComparison.OrdinalIgnoreCase)) as StorageFolder;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> LoadScriptFileAsync(StorageFile file, StorageFolder parentFolder = null)
        {
            if (file == null) return false;

            DeactivateScript();

            try
            {
                var loader = new ScriptLoader();
                var config = await loader.LoadFromFileAsync(file, parentFolder);
                if (config == null || config.States == null)
                {
                    ToastHelper.Basic("Failed to load script: Invalid script configuration.");
                    return false;
                }

                await DispatchHelper.InvokeInUIThread(CoreDispatcherPriority.Normal, () =>
                {
                    LoadedScript = config;
                    LoadedScriptFile = file;
                    LoadedScriptFolder = loader.CurrentScriptFolder ?? parentFolder;
                    _loadedScriptLoader = loader;
                    _isScriptActive = false; // Always loaded inactive!

                    OnPropertyChanged(nameof(LoadedScript));
                    OnPropertyChanged(nameof(LoadedScriptFile));
                    OnPropertyChanged(nameof(LoadedScriptFolder));
                    OnPropertyChanged(nameof(HasLoadedScript));
                    OnPropertyChanged(nameof(LoadedScriptDisplayName));
                    OnPropertyChanged(nameof(ScriptStatusText));
                    OnPropertyChanged(nameof(IsScriptActive));
                });

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load script file: {ex.Message}");
                ToastHelper.Basic($"Failed to load script: {ex.Message}");
                return false;
            }
        }

        public void UnloadScript()
        {
            DeactivateScript();

            var _ = DispatchHelper.InvokeInUIThread(CoreDispatcherPriority.Normal, () =>
            {
                LoadedScript = null;
                LoadedScriptFile = null;
                LoadedScriptFolder = null;
                _loadedScriptLoader = null;
                _isScriptActive = false;

                OnPropertyChanged(nameof(LoadedScript));
                OnPropertyChanged(nameof(LoadedScriptFile));
                OnPropertyChanged(nameof(LoadedScriptFolder));
                OnPropertyChanged(nameof(HasLoadedScript));
                OnPropertyChanged(nameof(LoadedScriptDisplayName));
                OnPropertyChanged(nameof(ScriptStatusText));
                OnPropertyChanged(nameof(IsScriptActive));
            });
        }

        public async Task ActivateScriptAsync()
        {
            if (!HasLoadedScript) return;
            if (_isScriptActive) return;

            try
            {
                var dispatcher = Window.Current?.Dispatcher ?? App.Dispatcher;
                _scriptVideoController = new PlaybackServiceVideoController(Locator.PlaybackService, dispatcher);
                _scriptVideoController.PositionChanged += OnScriptPositionChanged;

                string videoPath = CurrentVideo?.File?.Path ?? CurrentVideo?.Path ?? "current";
                string baseName = "current";
                try
                {
                    baseName = System.IO.Path.GetFileNameWithoutExtension(videoPath);
                }
                catch { }

                _scriptVideoController.AttachToCurrentPlayback(baseName, videoPath);

                _scriptCueTracker = new CuePointTracker(_scriptVideoController);

                _scriptStateContext = new StateContext(dispatcher);
                _scriptStateContext.HudPromptChanged += (prompt) => ScriptHudPromptChanged?.Invoke(prompt);
                _scriptStateContext.LogAdded += (log) => ScriptLogAdded?.Invoke(log);
                _scriptStateContext.StateChanged += (oldState, newState) => ScriptStateUpdated?.Invoke();
                _scriptStateContext.VariableChanged += (k, v) => ScriptStateUpdated?.Invoke();

                _scriptActionDispatcher = new ActionDispatcher(_scriptStateContext, _scriptVideoController, _loadedScriptLoader, LoadedScript, dispatcher);

                _scriptFsmEngine = new FsmEngine(LoadedScript, _scriptStateContext, _scriptVideoController, _scriptCueTracker, _scriptActionDispatcher, dispatcher);

                await DispatchHelper.InvokeInUIThread(CoreDispatcherPriority.Normal, () =>
                {
                    _isScriptActive = true;
                    OnPropertyChanged(nameof(IsScriptActive));
                    OnPropertyChanged(nameof(ScriptStatusText));
                    ScriptActivated?.Invoke();
                });

                await _scriptFsmEngine.StartAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to activate script: {ex.Message}");
                ToastHelper.Basic($"Failed to activate script: {ex.Message}");
                DeactivateScript();
            }
        }

        public void DeactivateScript()
        {
            if (!_isScriptActive && _scriptFsmEngine == null) return;

            _scriptFsmEngine?.Detach();
            _scriptFsmEngine = null;

            _scriptStateContext?.ClearTimers();
            _scriptStateContext = null;
            _scriptActionDispatcher = null;

            if (_scriptVideoController != null)
            {
                _scriptVideoController.PositionChanged -= OnScriptPositionChanged;
                _scriptVideoController.Detach();
                _scriptVideoController = null;
            }
            _scriptCueTracker = null;

            _isScriptActive = false;

            var _ = DispatchHelper.InvokeInUIThread(CoreDispatcherPriority.Normal, () =>
            {
                OnPropertyChanged(nameof(IsScriptActive));
                OnPropertyChanged(nameof(ScriptStatusText));
                ScriptDeactivated?.Invoke();
            });
        }

        private void OnScriptPositionChanged(long pos, long dur)
        {
            ScriptPositionUpdated?.Invoke(pos, dur);
        }

        #endregion

        public bool Is3DVideo { get; private set; }

        public void OnPlayerControlVisibilityChanged(bool visibility)
        {
            PlayerControlVisibility = visibility;
        }

        #endregion
    }
}
