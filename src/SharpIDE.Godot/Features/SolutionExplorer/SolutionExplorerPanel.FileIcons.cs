using Godot;
using SharpIDE.Application.Features.SolutionDiscovery;

namespace SharpIDE.Godot.Features.SolutionExplorer;

public partial class SolutionExplorerPanel
{

}

public static class SolutionExplorerExtensions
{
	extension(TreeItem fileItem)
	{
		public void SetIconsForFileExtension(SharpIdeFile file)
		{
			var (icon, overlayIcon) = FileIconHelper.GetIconForFileExtension(file.Extension);
			fileItem.SetIcon(0, icon);
			// Set even if null, to support renaming files
			fileItem.SetIconOverlay(0, overlayIcon);
		}

		public void SetWarningIcon()
		{
			var icon = FileIconHelper.WarningIcon;
			fileItem.SetIcon(0, icon);
			fileItem.SetIconOverlay(0, null);
		}
	}
}
