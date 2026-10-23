using System.Text;

namespace MeetingRoomBooking.Api.Features.Text;

/// <summary>Names people type: organisation and meeting room names.</summary>
public static class NameText
{
    /// <summary>
    /// Trimmed, with every run of white space turned into one space, or null
    /// when the result is shorter than <paramref name="minimumLength"/>,
    /// longer than <paramref name="maximumLength"/> or contains control or
    /// formatting characters (which could hide or reorder text in a list).
    /// </summary>
    public static string? Normalize(
        string? value,
        int minimumLength,
        int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            var category = char.GetUnicodeCategory(character);

            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format)
            {
                return null;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var normalized = builder.ToString();

        return normalized.Length < minimumLength || normalized.Length > maximumLength
            ? null
            : normalized;
    }
}
