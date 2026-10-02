using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ContextMole.Documents;

public sealed partial class DocumentExtractionRegistry
{
    // Excel dates are numeric serials interpreted by the workbook date system and cell style.
    // Keep the raw stored value beside the canonical date: we do not evaluate any formulas.
    // https://support.microsoft.com/en-us/excel/date-systems-in-excel
    private sealed class SpreadsheetDateStyles(WorkbookPartContext context)
    {
        private readonly CellFormat[] _formats = context.Styles?.CellFormats?.Elements<CellFormat>().ToArray() ?? [];
        private readonly Dictionary<uint, string> _custom = context.Styles?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(value => value.NumberFormatId?.Value is not null && value.FormatCode?.Value is not null)
            .GroupBy(value => value.NumberFormatId!.Value).ToDictionary(group => group.Key, group => group.First().FormatCode!.Value!) ?? [];
        private readonly bool _date1904 = context.Date1904;

        public string Format(Cell cell, string raw)
        {
            if (cell.CellValue is null || cell.DataType?.Value is { } type && type != CellValues.Number ||
                (cell.StyleIndex?.Value ?? 0) >= _formats.Length ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || !double.IsFinite(serial)) return raw;
            var formatId = _formats[(int)(cell.StyleIndex?.Value ?? 0)].NumberFormatId?.Value ?? 0;
            var (date, time, elapsed) = FormatKind(formatId);
            if (!date && !time) return raw;
            if (serial < 0) return raw; // Excel's negative-date rendering is not portable.
            string canonical;
            try
            {
                if (elapsed)
                {
                    var duration = TimeSpan.FromDays(serial);
                    canonical = $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
                }
                else
                {
                    // Serial 60 is Excel's deliberately retained fictitious leap day.
                    if (date && !_date1904 && Math.Floor(serial) == 60)
                        canonical = "1900-02-29 (Excel 1900 leap-day)" + (time ? "T" + TimePart(serial) : "");
                    else if (date)
                    {
                        if (!_date1904 && serial < 1) return raw;
                        var epoch = _date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 31);
                        var value = epoch.AddDays(!_date1904 && serial >= 60 ? serial - 1 : serial);
                        canonical = value.ToString(time ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                    else canonical = TimePart(serial);
                }
            }
            catch (ArgumentException) { return raw; }
            catch (OverflowException) { return raw; }
            return $"{canonical} (Excel serial: {raw})";
        }

        private (bool Date, bool Time, bool Elapsed) FormatKind(uint id)
        {
            if (id is >= 14 and <= 17) return (true, false, false);
            if (id is >= 18 and <= 21 or 45 or 47) return (false, true, false);
            if (id == 22) return (true, true, false);
            if (id == 46) return (false, true, true);
            if (!_custom.TryGetValue(id, out var code)) return (false, false, false);
            // Multiple/conditional sections may select a numeric alternative. Leave them raw rather
            // than guessing which section Excel would display.
            if (code.Contains(';') || Regex.IsMatch(code, @"\[[<>=]", RegexOptions.CultureInvariant)) return (false, false, false);
            var tokens = new StringBuilder();
            var elapsed = false;
            for (var index = 0; index < code.Length; index++)
            {
                var character = char.ToLowerInvariant(code[index]);
                if (character == ';') break; // Nonnegative numbers use the first section.
                if (character == '"') { while (++index < code.Length && code[index] != '"') { } continue; }
                if (character is '\\' or '_' or '*') { index++; continue; }
                if (character == '[')
                {
                    var end = code.IndexOf(']', index + 1); if (end < 0) return (false, false, false);
                    var bracket = code[(index + 1)..end].ToLowerInvariant();
                    if (bracket.Length > 0 && bracket.All(value => value is 'h' or 'm' or 's')) elapsed = true;
                    index = end; continue;
                }
                tokens.Append(character);
            }
            var pattern = tokens.ToString();
            var time = elapsed || pattern.Contains('h') || pattern.Contains('s') || pattern.Contains("am/pm", StringComparison.Ordinal) || pattern.Contains("a/p", StringComparison.Ordinal);
            var datePattern = pattern.Replace("am/pm", "", StringComparison.Ordinal).Replace("a/p", "", StringComparison.Ordinal);
            var date = datePattern.Contains('y') || datePattern.Contains('d') || datePattern.Contains("mmm", StringComparison.Ordinal) || datePattern.Contains('m') && !time;
            return (date, time, elapsed);
        }
        private static string TimePart(double serial) => DateTime.MinValue.AddDays(serial - Math.Floor(serial))
            .ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
    private sealed record WorkbookPartContext(Stylesheet? Styles, bool Date1904);
}
