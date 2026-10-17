using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>Data-only color grammar. Never invokes WPF color converters or loads ICC profiles.</summary>
internal static class MusicSkinColor
{
    // Only framework constants are read; input can select a name, never a type, property or URI.
    private static readonly IReadOnlyDictionary<string, Color> NamedColors = typeof(Colors)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(property => property.PropertyType == typeof(Color))
        .ToDictionary(property => property.Name, property => (Color)property.GetValue(null)!, StringComparer.OrdinalIgnoreCase);

    internal static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (value[0] != '#') return NamedColors.TryGetValue(value, out color);
        int digits = value.Length - 1;
        if (digits is not (3 or 4 or 6 or 8)) return false;
        uint packed = 0;
        foreach (char character in value.AsSpan(1))
        {
            int digit = character is >= '0' and <= '9' ? character - '0' :
                character is >= 'A' and <= 'F' ? character - 'A' + 10 :
                character is >= 'a' and <= 'f' ? character - 'a' + 10 : -1;
            if (digit < 0) return false;
            packed = (packed << 4) | (uint)digit;
        }
        color = digits <= 4
            ? Color.FromArgb(digits == 4 ? (byte)((packed >> 12) * 17) : (byte)255,
                (byte)(((packed >> 8) & 15) * 17), (byte)(((packed >> 4) & 15) * 17), (byte)((packed & 15) * 17))
            : Color.FromArgb(digits == 8 ? (byte)(packed >> 24) : (byte)255,
                (byte)((packed >> 16) & 255), (byte)((packed >> 8) & 255), (byte)(packed & 255));
        return true;
    }
}
