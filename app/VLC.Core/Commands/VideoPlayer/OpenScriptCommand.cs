using System;
using System.Diagnostics;
using Windows.Storage;
using Windows.Storage.Pickers;
using VLC.Helpers;
using VLC.Model;
using VLC.Utils;
using VLC.ViewModels;

namespace VLC.Commands.VideoPlayer
{
    public class OpenScriptCommand : AlwaysExecutableCommand
    {
        public async override void Execute(object parameter)
        {
            if (parameter is StorageFile file)
            {
                await Locator.VideoPlayerVm.LoadScriptFileAsync(file, parentFolder: null);
            }
            else
            {
                try
                {
                    App.OpenFilePickerReason = OpenFilePickerReason.OnOpeningScript;
                    var picker = new FileOpenPicker
                    {
                        ViewMode = PickerViewMode.List,
                        SuggestedStartLocation = PickerLocationId.VideosLibrary
                    };

                    foreach (var item in VLCFileExtensions.ScriptExtensions)
                    {
                        picker.FileTypeFilter.Add(item);
                    }

                    StorageFile pickedFile = await picker.PickSingleFileAsync();
                    if (pickedFile != null)
                    {
                        await Locator.VideoPlayerVm.LoadScriptFileAsync(pickedFile, parentFolder: null);
                    }
                    else
                    {
                        LogHelper.Log("Cancelled opening script");
                    }
                    App.OpenFilePickerReason = OpenFilePickerReason.Null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to pick script file: {ex.Message}");
                    App.OpenFilePickerReason = OpenFilePickerReason.Null;
                }
            }
        }
    }
}
