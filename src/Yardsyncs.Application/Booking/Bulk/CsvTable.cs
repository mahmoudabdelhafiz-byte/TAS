using System.Text;

namespace Yardsyncs.Application.Booking.Bulk;

/// <summary>
/// Minimal RFC 4180 CSV reader: quoted fields, doubled quotes, line breaks inside quotes, BOM.
/// The delimiter (comma, semicolon or tab) is detected from the header line, because Excel
/// in Arabic and European locales saves CSV with semicolons.
/// </summary>
public static class CsvTable
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var delimiter = DetectDelimiter(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        char[] candidates = [',', ';', '\t'];
        return candidates.OrderByDescending(d => header.Count(c => c == d)).First();
    }
}
