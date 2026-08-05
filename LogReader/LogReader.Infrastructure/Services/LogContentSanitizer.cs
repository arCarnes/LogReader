namespace LogReader.Infrastructure.Services;

using System.Text;

internal static class LogContentSanitizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsControl(character) || character == '\t')
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
            builder.Append('\uFFFD');
        }

        return builder?.ToString() ?? value;
    }
}
