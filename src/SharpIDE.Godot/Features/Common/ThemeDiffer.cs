using System.Diagnostics;
using Godot;
using SharpIDE.Godot.Features.Settings;

namespace SharpIDE.Godot.Features.Common;

public static class ThemeDiffer
{
	private const string DarkThemePath = "res://Resources/DarkTheme.tres";
	private const string LightThemePath = "res://Resources/LightTheme.tres";

	extension(Node node)
	{
		[Obsolete]
		public void DiffTheme()
		{
			return;
			var darkTheme = SetThemeExtensions.DarkTheme;
			var lightTheme = SetThemeExtensions.LightTheme;
			var themeTypes = darkTheme.GetTypeList()
				.Union(lightTheme.GetTypeList(), StringComparer.Ordinal)
				.Order(StringComparer.Ordinal);
			var dataTypes = Enum.GetValues<Theme.DataType>()
				.Where(dataType => dataType is not Theme.DataType.Max);
			var missingItemCount = 0;
			var differingValueCount = 0;
			var remediationCount = 0;

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
						missingItemCount++;
						if (dataType is Theme.DataType.Color)
						{
							GD.Print($"[ThemeDiffer] Light theme is missing {dataType} '{item}' for '{themeType}' (present in dark theme).");
							continue;
						}

						CopyThemeItem(darkTheme, lightTheme, dataType, item, themeType);
						remediationCount++;
						GD.Print($"[ThemeDiffer] Light theme was missing {dataType} '{item}' for '{themeType}'; copied it from dark theme.");
					}

					foreach (var item in lightItems.Except(darkItems).Order(StringComparer.Ordinal))
					{
						missingItemCount++;
						if (dataType is Theme.DataType.Color)
						{
							GD.Print($"[ThemeDiffer] Dark theme is missing {dataType} '{item}' for '{themeType}' (present in light theme).");
							continue;
						}

						CopyThemeItem(lightTheme, darkTheme, dataType, item, themeType);
						remediationCount++;
						GD.Print($"[ThemeDiffer] Dark theme was missing {dataType} '{item}' for '{themeType}'; copied it from light theme.");
					}

					if (dataType is Theme.DataType.Color)
					{
						continue;
					}

					foreach (var item in darkItems.Intersect(lightItems).Order(StringComparer.Ordinal))
					{
						if (ShouldIgnoreDifference(dataType, item, themeType))
						{
							continue;
						}

						if (dataType is Theme.DataType.Stylebox)
						{
							var result = RemediateStyleBox(darkTheme, lightTheme, item, themeType);
							differingValueCount += result;
							remediationCount += result;
							continue;
						}

						var darkValue = darkTheme.GetThemeItem(dataType, item, themeType);
						var lightValue = lightTheme.GetThemeItem(dataType, item, themeType);
						if (darkValue.Equals(lightValue))
						{
							continue;
						}

						differingValueCount++;
						remediationCount++;
						lightTheme.SetThemeItem(dataType, item, themeType, darkValue);
						GD.Print($"[ThemeDiffer] Light {themeType}/{dataType}/{item} differed: light={lightValue}, dark={darkValue}; updated light to match dark.");
					}
				}
			}

			SaveTheme(darkTheme, DarkThemePath);
			SaveTheme(lightTheme, LightThemePath);
			GD.Print($"[ThemeDiffer] Theme comparison complete: {missingItemCount} missing items, {differingValueCount} differing non-color values, {remediationCount} remediations.");
			Process.GetCurrentProcess().Kill();
		}
	}

	private static void CopyThemeItem(Theme source, Theme target, Theme.DataType dataType, string item, string themeType)
	{
		if (dataType is Theme.DataType.Stylebox)
		{
			var styleBox = source.GetStylebox(item, themeType);
			target.SetStylebox(item, themeType, (StyleBox)styleBox.Duplicate(true));
			return;
		}

		target.SetThemeItem(dataType, item, themeType, source.GetThemeItem(dataType, item, themeType));
	}

	private static int RemediateStyleBox(Theme darkTheme, Theme lightTheme, string item, string themeType)
	{
		var darkStyleBox = darkTheme.GetStylebox(item, themeType);
		var lightStyleBox = lightTheme.GetStylebox(item, themeType);

		if (darkStyleBox.GetType() != lightStyleBox.GetType())
		{
			var replacement = (StyleBox)darkStyleBox.Duplicate(true);
			CopyColorProperties(lightStyleBox, replacement);
			lightTheme.SetStylebox(item, themeType, replacement);
			GD.Print($"[ThemeDiffer] Light {themeType}/Stylebox/{item} used {lightStyleBox.GetType().Name}, dark used {darkStyleBox.GetType().Name}; replaced light structure while preserving matching color properties.");
			return 1;
		}

		StyleBox? remediatedStyleBox = null;
		var differenceCount = 0;
		foreach (var property in darkStyleBox.GetPropertyList())
		{
			var propertyType = (Variant.Type)property["type"].AsInt64();
			var usage = (PropertyUsageFlags)property["usage"].AsInt64();
			if (propertyType is Variant.Type.Color || !usage.HasFlag(PropertyUsageFlags.Storage))
			{
				continue;
			}

			var propertyName = property["name"].AsStringName();
			var darkValue = darkStyleBox.Get(propertyName);
			var lightValue = lightStyleBox.Get(propertyName);
			if (darkValue.Equals(lightValue))
			{
				continue;
			}

			remediatedStyleBox ??= (StyleBox)lightStyleBox.Duplicate(true);
			remediatedStyleBox.Set(propertyName, darkValue);
			differenceCount++;
			GD.Print($"[ThemeDiffer] Light {themeType}/Stylebox/{item}/{propertyName} differed: light={lightValue}, dark={darkValue}; updated light to match dark.");
		}

		if (remediatedStyleBox is not null)
		{
			lightTheme.SetStylebox(item, themeType, remediatedStyleBox);
		}

		return differenceCount;
	}

	private static void CopyColorProperties(StyleBox source, StyleBox target)
	{
		var targetProperties = target.GetPropertyList()
			.Where(property => (Variant.Type)property["type"].AsInt64() is Variant.Type.Color)
			.Select(property => property["name"].AsStringName().ToString())
			.ToHashSet(StringComparer.Ordinal);

		foreach (var property in source.GetPropertyList())
		{
			if ((Variant.Type)property["type"].AsInt64() is not Variant.Type.Color)
			{
				continue;
			}

			var propertyName = property["name"].AsStringName();
			if (targetProperties.Contains(propertyName.ToString()))
			{
				target.Set(propertyName, source.Get(propertyName));
			}
		}
	}

	private static bool ShouldIgnoreDifference(Theme.DataType dataType, string item, string themeType)
	{
		return dataType is Theme.DataType.Constant
			   && themeType == "ThemeInfo"
			   && item == "IsLight1OrDark2";
	}

	private static void SaveTheme(Theme theme, string path)
	{
		var error = ResourceSaver.Save(theme, path);
		if (error is not Error.Ok)
		{
			GD.PrintErr($"[ThemeDiffer] Failed to save '{path}': {error}.");
		}
	}
}
