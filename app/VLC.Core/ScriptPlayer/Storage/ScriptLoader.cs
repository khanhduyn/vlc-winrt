using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Newtonsoft.Json;
using VLC.ScriptPlayer.Models;

namespace VLC.ScriptPlayer.Storage
{
    /// <summary>
    /// Loads interactive scripts and resolves media assets across App Package, LocalAppData, USB drives, and LAN HTTP endpoints.
    /// </summary>
    public class ScriptLoader
    {
        public StorageFolder CurrentScriptFolder { get; private set; }

        /// <summary>
        /// Loads the embedded default sample script from ms-appx:///Assets/sample_script.json
        /// </summary>
        public async Task<ScriptConfig> LoadPackagedScriptAsync(string relativePath = "Assets/sample_script.json")
        {
            var uri = new Uri($"ms-appx:///{relativePath.TrimStart('/')}");
            var file = await StorageFile.GetFileFromApplicationUriAsync(uri);
            CurrentScriptFolder = Package.Current.InstalledLocation;
            return await DeserializeFileAsync(file);
        }

        /// <summary>
        /// Loads a script from ApplicationData.Current.LocalFolder
        /// </summary>
        public async Task<ScriptConfig> LoadLocalScriptAsync(string fileName = "script.json")
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var file = await localFolder.GetFileAsync(fileName);
            CurrentScriptFolder = localFolder;
            return await DeserializeFileAsync(file);
        }

        /// <summary>
        /// Scans connected USB flash drives (via RemovableDevices) for script.json
        /// </summary>
        public async Task<(ScriptConfig Config, StorageFolder Folder)> LoadFromUsbAsync(string scriptFileName = "script.json")
        {
            try
            {
                var removableRoot = KnownFolders.RemovableDevices;
                var drives = await removableRoot.GetFoldersAsync();

                foreach (var drive in drives)
                {
                    try
                    {
                        var item = await drive.TryGetItemAsync(scriptFileName);
                        if (item is StorageFile file)
                        {
                            CurrentScriptFolder = drive;
                            var config = await DeserializeFileAsync(file);
                            return (config, drive);
                        }

                        // Check inside a subfolder named 'xbox' or 'media'
                        var subfolders = await drive.GetFoldersAsync();
                        foreach (var sub in subfolders)
                        {
                            var subItem = await sub.TryGetItemAsync(scriptFileName);
                            if (subItem is StorageFile subFile)
                            {
                                CurrentScriptFolder = sub;
                                var config = await DeserializeFileAsync(subFile);
                                return (config, sub);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore permission/read issues on individual drives
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScriptLoader] USB scan error: {ex.Message}");
            }

            return (null, null);
        }

        /// <summary>
        /// Downloads script configuration over HTTP / HTTPS from a LAN or cloud server.
        /// </summary>
        public async Task<ScriptConfig> LoadFromHttpAsync(string url)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                var json = await client.GetStringAsync(url);
                CurrentScriptFolder = null;
                return JsonConvert.DeserializeObject<ScriptConfig>(json);
            }
        }

        /// <summary>
        /// Attempts to resolve a MediaItem URI to a StorageFile if on USB or local storage.
        /// </summary>
        public async Task<StorageFile> ResolveMediaFileAsync(string mediaUri)
        {
            if (string.IsNullOrWhiteSpace(mediaUri)) return null;

            // Check if absolute URL (http, https, ms-appx)
            if (mediaUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                mediaUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                mediaUri.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase) ||
                mediaUri.StartsWith("ms-appdata://", StringComparison.OrdinalIgnoreCase))
            {
                return null; // Let MediaPlayer / VLC handle standard URI
            }

            // Check in CurrentScriptFolder
            if (CurrentScriptFolder != null)
            {
                try
                {
                    var cleanPath = mediaUri.Replace('/', '\\').TrimStart('\\');
                    var item = await CurrentScriptFolder.TryGetItemAsync(cleanPath);
                    if (item is StorageFile sf)
                    {
                        return sf;
                    }
                }
                catch
                {
                    // Fall back
                }
            }

            return null;
        }

        /// <summary>
        /// Loads a script directly from a StorageFile, validating structure and establishing base folder.
        /// </summary>
        public async Task<ScriptConfig> LoadFromFileAsync(StorageFile file, StorageFolder scriptFolder = null)
        {
            if (file == null) return null;
            var config = await DeserializeFileAsync(file);
            if (config == null || config.States == null)
            {
                throw new InvalidDataException("Invalid or empty script configuration");
            }
            CurrentScriptFolder = scriptFolder ?? await TryGetParentFolderAsync(file);
            return config;
        }

        private static async Task<StorageFolder> TryGetParentFolderAsync(StorageFile file)
        {
            try
            {
                return await file.GetParentAsync();
            }
            catch
            {
                return null;
            }
        }

        private static async Task<ScriptConfig> DeserializeFileAsync(StorageFile file)
        {
            using (var stream = await file.OpenStreamForReadAsync())
            using (var reader = new StreamReader(stream))
            {
                var json = await reader.ReadToEndAsync();
                return JsonConvert.DeserializeObject<ScriptConfig>(json);
            }
        }
    }
}
