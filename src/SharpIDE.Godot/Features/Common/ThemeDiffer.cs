using System.Diagnostics;
using Godot;
using SharpIDE.Godot.Features.Settings;
using Environment = System.Environment;

namespace SharpIDE.Godot.Features.Common;

public static class ThemeDiffer
{
	extension(Node node)
	{
		public void DiffTheme()
		{
			var darkTheme = SetThemeExtensions.DarkTheme;
			var lightTheme = SetThemeExtensions.LightTheme;
			var themeTypes = darkTheme.GetTypeList()
				.Union(lightTheme.GetTypeList(), StringComparer.Ordinal)
				.Order(StringComparer.Ordinal);
			var dataTypes = Enum.GetValues<Theme.DataType>()
				.Where(dataType => dataType is not Theme.DataType.Max);
			var differenceCount = 0;

			foreach (var themeType in themeTypes)
			{
				foreach (var dataType in dataTypes)
				{
					var darkItems = darkTheme.GetThemeItemList(dataType, themeType)
						.ToHashSet(StringComparer.Ordinal);
					var lightItems = lightTheme.GetThemeItemList(dataType, themeType)
						.ToHashSet(StringComparer.Ordinal);

					foreach (var item in darkItems.Except(lightItems).Order(StringComparer.Ordinal))
					{
						GD.Print($"[ThemeDiffer] Light theme is missing {dataType} '{item}' for '{themeType}' (present in dark theme).");
						differenceCount++;
					}

					foreach (var item in lightItems.Except(darkItems).Order(StringComparer.Ordinal))
					{
						GD.Print($"[ThemeDiffer] Dark theme is missing {dataType} '{item}' for '{themeType}' (present in light theme).");
						differenceCount++;
					}
				}
			}

			GD.Print($"[ThemeDiffer] Theme comparison complete: {differenceCount} missing properties found.");
			Process.GetCurrentProcess().Kill();
		}
	}
}
