using MudBlazor;

namespace SharpIDE.Photino;

public static class AppThemeProvider
{
	public static MudTheme GetTheme()
	{
		var theme = new MudTheme();
		theme.Typography.H5.FontSize = "1.4rem";
		theme.Typography.H5.FontWeight = "500";
		theme.Typography.H6.FontSize = "1.1rem";
		return theme;
	}
}
