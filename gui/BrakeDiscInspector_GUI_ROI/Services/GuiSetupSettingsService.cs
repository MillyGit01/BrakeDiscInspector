using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using BrakeDiscInspector_GUI_ROI.Properties;

namespace BrakeDiscInspector_GUI_ROI.Services
{
    public static class GuiSetupSettingsService
    {
        private const string AccentBrushKey = "UI.Brush.Accent";
        private const string AccentColorKey = "UI.Color.Accent";

        private static readonly string[] FontFamilyKeys =
        {
            "UI.FontFamily.Body",
            "UI.FontFamily.Header"
        };

        private static readonly string[] FontSizeKeys =
        {
            "UI.FontSize.WindowTitle",
            "UI.FontSize.SectionTitle",
            "UI.FontSize.GroupHeader",
            "UI.FontSize.ControlLabel",
            "UI.FontSize.ControlText",
            "UI.FontSize.ButtonText",
            "UI.FontSize.CheckBox"
        };

        private static readonly string[] BrushKeys =
        {
            "UI.Brush.Foreground",
            AccentBrushKey,
            "UI.Brush.ButtonForeground",
            "UI.Brush.ButtonBackground",
            "UI.Brush.ButtonBackgroundHover",
            "UI.Brush.GroupHeaderForeground"
        };

        // Stored at %LOCALAPPDATA%\BrakeDiscInspector\gui_setup.json for easy diagnostics.
        public static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector",
            "gui_setup.json");

        public static string GetSettingsPath() => ConfigPath;

        public static GuiSetupSettings LoadOrDefault()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    return new GuiSetupSettings();
                }

                var json = File.ReadAllText(ConfigPath);
                var settings = JsonSerializer.Deserialize<GuiSetupSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return settings ?? new GuiSetupSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[gui-setup] Failed to load settings from '{ConfigPath}': {ex}");
                return new GuiSetupSettings();
            }
        }

        public static void Save(GuiSetupSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[gui-setup] Failed to save settings to '{ConfigPath}': {ex}");
            }
        }

        public static GuiSetupSettings CaptureCurrent(Window? window)
        {
            var settings = new GuiSetupSettings
            {
                Theme = Settings.Default.ThemePreference
            };

            var fontFamilies = new Dictionary<string, string>();
            foreach (var key in FontFamilyKeys)
            {
                if (TryFindResource(window, key, out var value) && value is FontFamily family)
                {
                    fontFamilies[key] = family.Source;
                }
            }

            if (fontFamilies.Count > 0)
            {
                settings.FontFamilies = fontFamilies;
            }

            var fontSizes = new Dictionary<string, double>();
            foreach (var key in FontSizeKeys)
            {
                if (TryFindResource(window, key, out var value))
                {
                    if (value is double size)
                    {
                        fontSizes[key] = size;
                    }
                    else if (value is float floatSize)
                    {
                        fontSizes[key] = floatSize;
                    }
                }
            }

            if (fontSizes.Count > 0)
            {
                settings.FontSizes = fontSizes;
            }

            var brushes = new Dictionary<string, string>();
            foreach (var key in BrushKeys)
            {
                if (TryFindResource(window, key, out var value))
                {
                    if (value is SolidColorBrush brush)
                    {
                        brushes[key] = brush.Color.ToString();
                    }
                    else if (value is Color color)
                    {
                        brushes[key] = color.ToString();
                    }
                }
            }

            if (brushes.Count > 0)
            {
                settings.Brushes = brushes;
            }

            if (TryFindResource(window, AccentColorKey, out var accentValue) && accentValue is Color accentColor)
            {
                settings.Accent = accentColor.ToString();
            }
            else if (TryFindResource(window, AccentBrushKey, out var accentBrush) && accentBrush is SolidColorBrush accent)
            {
                settings.Accent = accent.Color.ToString();
            }
            else if (settings.Brushes != null && settings.Brushes.TryGetValue(AccentBrushKey, out var accentHex))
            {
                settings.Accent = accentHex;
            }

            return settings;
        }

        public static void Apply(GuiSetupSettings settings)
        {
            if (settings == null || Application.Current == null)
            {
                return;
            }

            if (settings.FontFamilies != null)
            {
                foreach (var pair in settings.FontFamilies)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        SetResourceValue(pair.Key, new FontFamily(pair.Value));
                    }
                }
            }

            if (settings.FontSizes != null)
            {
                foreach (var pair in settings.FontSizes)
                {
                    SetResourceValue(pair.Key, pair.Value);
                }
            }

            if (settings.Brushes != null)
            {
                foreach (var pair in settings.Brushes)
                {
                    if (TryParseColor(pair.Value, out var color))
                    {
                        if (string.Equals(pair.Key, AccentBrushKey, StringComparison.OrdinalIgnoreCase))
                        {
                            SetAccentResources(color);
                        }
                        else
                        {
                            SetBrushResource(pair.Key, color);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.Accent) && TryParseColor(settings.Accent, out var accentColor))
            {
                SetAccentResources(accentColor);
            }
        }

        private static bool TryFindResource(Window? window, string key, out object? value)
        {
            value = null;
            if (window != null)
            {
                var fromWindow = window.TryFindResource(key);
                if (fromWindow != null)
                {
                    value = fromWindow;
                    return true;
                }
            }

            if (Application.Current != null)
            {
                var fromApp = Application.Current.TryFindResource(key);
                if (fromApp != null)
                {
                    value = fromApp;
                    return true;
                }
            }

            return false;
        }

        private static void SetBrushResource(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }

            SetResourceValue(key, brush);
        }

        private static void SetAccentResources(Color color)
        {
            SetResourceValue(AccentColorKey, color);
            SetBrushResource(AccentBrushKey, color);
        }

        private static void SetResourceValue(string key, object value)
        {
            if (Application.Current.Resources.Contains(key))
            {
                Application.Current.Resources[key] = value;
            }
            else
            {
                Application.Current.Resources[key] = value;
            }
        }

        private static bool TryParseColor(string value, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                var converted = ColorConverter.ConvertFromString(value);
                if (converted is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}
