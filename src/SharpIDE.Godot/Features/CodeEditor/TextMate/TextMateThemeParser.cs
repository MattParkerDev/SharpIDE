using System.Text.Json;
using System.Xml.Linq;
using Godot;

namespace SharpIDE.Godot.Features.CodeEditor.TextMate;

/// <summary>
/// Parser for TextMate theme files in two formats:
/// - VS Code JSON (tokenColors array with scope and settings)
/// - Classic .tmTheme XML plist format
/// </summary>
public static class TextMateThemeParser
{
    public static TextMateTheme ParseFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Theme file not found: {filePath}");
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".json" => ParseJsonTheme(filePath),
            ".tmtheme" => ParsePlistTheme(filePath),
            _ => throw new NotSupportedException($"Unsupported theme file format: {ext}")
        };
    }

    /// <summary>
    /// Parses a VS Code JSON theme file.
    /// Expected format: { "name": "...", "type": "dark", "tokenColors": [...] }
    /// </summary>
    private static TextMateTheme ParseJsonTheme(string filePath)
    {
        try
        {
            using var file = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(file);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? "Unknown"
                : Path.GetFileNameWithoutExtension(filePath);

            var themeType = root.TryGetProperty("type", out var typeEl)
                ? typeEl.GetString()?.ToLowerInvariant() ?? "dark"
                : "dark";

            var rules = new List<TextMateTokenRule>();

            if (root.TryGetProperty("tokenColors", out var tokenColorsEl) && tokenColorsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tokenEl in tokenColorsEl.EnumerateArray())
                {
                    var rule = ParseJsonTokenRule(tokenEl);
                    if (rule != null)
                    {
                        rules.Add(rule);
                    }
                }
            }

            return new TextMateTheme
            {
                Name = name,
                ThemeType = themeType,
                TokenRules = rules
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to parse JSON theme: {ex.Message}");
            throw;
        }
    }

    private static TextMateTokenRule? ParseJsonTokenRule(JsonElement tokenEl)
    {
        // Extract scope(s)
        string[] scopes = [];
        if (tokenEl.TryGetProperty("scope", out var scopeEl))
        {
            scopes = scopeEl.ValueKind == JsonValueKind.String
                ? new[] { scopeEl.GetString() ?? "" }
                : scopeEl.ValueKind == JsonValueKind.Array
                    ? tokenEl.EnumerateArray()
                        .Select(s => s.GetString() ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray()
                    : [];
        }

        if (scopes.Length == 0)
        {
            return null;
        }

        // Extract settings (foreground, background, fontStyle)
        Color? fg = null;
        Color? bg = null;
        string? fontStyle = null;

        if (tokenEl.TryGetProperty("settings", out var settingsEl))
        {
            if (settingsEl.TryGetProperty("foreground", out var fgEl))
            {
                var fgStr = fgEl.GetString();
                fg = ParseColor(fgStr);
            }

            if (settingsEl.TryGetProperty("background", out var bgEl))
            {
                var bgStr = bgEl.GetString();
                bg = ParseColor(bgStr);
            }

            if (settingsEl.TryGetProperty("fontStyle", out var fsEl))
            {
                fontStyle = fsEl.GetString();
            }
        }

        // Only create rule if we have at least a foreground color
        if (fg == null)
        {
            return null;
        }

        return new TextMateTokenRule
        {
            Scopes = scopes,
            Foreground = fg,
            Background = bg,
            FontStyle = fontStyle
        };
    }

    /// <summary>
    /// Parses a classic TextMate .tmTheme XML plist file.
    /// Expected format: plist > dict with key "settings" > array of dicts with scope and settings
    /// </summary>
    private static TextMateTheme ParsePlistTheme(string filePath)
    {
        try
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root;

            if (root?.Name.LocalName != "plist")
            {
                throw new InvalidOperationException("Not a valid plist file");
            }

            var dictEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "dict");
            if (dictEl == null)
            {
                throw new InvalidOperationException("No root dict found in plist");
            }

            var name = Path.GetFileNameWithoutExtension(filePath);
            var themeType = "dark";
            var rules = new List<TextMateTokenRule>();

            // Parse top-level dict for name and theme type
            var elements = dictEl.Elements().ToList();
            for (int i = 0; i < elements.Count - 1; i += 2)
            {
                var keyEl = elements[i];
                var valueEl = elements[i + 1];

                if (keyEl.Name.LocalName == "key")
                {
                    var keyText = keyEl.Value;
                    if (keyText == "name" && valueEl.Name.LocalName == "string")
                    {
                        name = valueEl.Value;
                    }
                    else if (keyText == "settings" && valueEl.Name.LocalName == "array")
                    {
                        // Parse the settings array (list of token rules)
                        rules = ParsePlistSettingsArray(valueEl);
                    }
                }
            }

            return new TextMateTheme
            {
                Name = name,
                ThemeType = themeType,
                TokenRules = rules
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Failed to parse plist theme: {ex.Message}");
            throw;
        }
    }

    private static List<TextMateTokenRule> ParsePlistSettingsArray(XElement arrayEl)
    {
        var rules = new List<TextMateTokenRule>();

        foreach (var dictEl in arrayEl.Elements().Where(e => e.Name.LocalName == "dict"))
        {
            var rule = ParsePlistTokenRule(dictEl);
            if (rule != null)
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    private static TextMateTokenRule? ParsePlistTokenRule(XElement dictEl)
    {
        string[] scopes = [];
        Color? fg = null;
        Color? bg = null;
        string? fontStyle = null;

        var elements = dictEl.Elements().ToList();
        for (int i = 0; i < elements.Count - 1; i += 2)
        {
            var keyEl = elements[i];
            var valueEl = elements[i + 1];

            if (keyEl.Name.LocalName == "key")
            {
                var keyText = keyEl.Value;

                if (keyText == "scope" && valueEl.Name.LocalName == "string")
                {
                    // Scope can be comma-separated
                    var scopeStr = valueEl.Value;
                    scopes = scopeStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .ToArray();
                }
                else if (keyText == "settings" && valueEl.Name.LocalName == "dict")
                {
                    // Parse settings dict
                    var settingElements = valueEl.Elements().ToList();
                    for (int j = 0; j < settingElements.Count - 1; j += 2)
                    {
                        var settingKeyEl = settingElements[j];
                        var settingValueEl = settingElements[j + 1];

                        if (settingKeyEl.Name.LocalName == "key")
                        {
                            var settingKey = settingKeyEl.Value;
                            if (settingKey == "foreground" && settingValueEl.Name.LocalName == "string")
                            {
                                fg = ParseColor(settingValueEl.Value);
                            }
                            else if (settingKey == "background" && settingValueEl.Name.LocalName == "string")
                            {
                                bg = ParseColor(settingValueEl.Value);
                            }
                            else if (settingKey == "fontStyle" && settingValueEl.Name.LocalName == "string")
                            {
                                fontStyle = settingValueEl.Value;
                            }
                        }
                    }
                }
            }
        }

        if (scopes.Length == 0 || fg == null)
        {
            return null;
        }

        return new TextMateTokenRule
        {
            Scopes = scopes,
            Foreground = fg,
            Background = bg,
            FontStyle = fontStyle
        };
    }

    /// <summary>
    /// Parses a color string in hex format (#RRGGBB or #RGB).
    /// Returns null if the string is not a valid color.
    /// </summary>
    private static Color? ParseColor(string? colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr))
        {
            return null;
        }

        try
        {
            // Remove leading # if present
            var hex = colorStr.StartsWith("#") ? colorStr[1..] : colorStr;

            // Handle #RGB shorthand
            if (hex.Length == 3)
            {
                hex = new string(hex.SelectMany(c => new[] { c, c }).ToArray());
            }

            // Parse hex as Color
            if (hex.Length >= 6)
            {
                var r = int.Parse(hex[0..2], System.Globalization.NumberStyles.HexNumber) / 255f;
                var g = int.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber) / 255f;
                var b = int.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber) / 255f;
                var a = hex.Length >= 8
                    ? int.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber) / 255f
                    : 1f;

                return new Color(r, g, b, a);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
