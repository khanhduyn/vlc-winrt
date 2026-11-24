using VLC.Model;
using VLC.Utils;
using VLC.ViewModels;

namespace VLC.Commands.VideoPlayer
{
    public class ShowScriptSettingsCommand : AlwaysExecutableCommand
    {
        public override void Execute(object parameter)
        {
            Locator.NavigationService.Go(VLCPage.ScriptSettings);
        }
    }
}
