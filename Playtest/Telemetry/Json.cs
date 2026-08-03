using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlaytestHarness.Telemetry;

/// <summary>
/// Minimal hand-rolled JSON writer. Deliberate choice over a serializer dependency: the server's
/// Managed folder ships no Newtonsoft.Json, and referencing the bundled System.Text.Json from a
/// net48 plugin drags in binding-redirect headaches. Output scope: strings, bools, numbers,
/// IDictionary&lt;string, object?&gt;, IEnumerable, null. That is all the telemetry schema needs.
/// </summary>
public static class Json
{
    public static string Serialize(object? value)
    {
        StringBuilder sb = new(256);
        WriteValue(sb, value);
        return sb.ToString();
    }

    public static void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        sb.Append('{');
        bool first = true;
        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            WriteString(sb, pair.Key);
            sb.Append(':');
            WriteValue(sb, pair.Value);
        }

        sb.Append('}');
    }

    public static void WriteValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case float f:
                // Non-finite floats (provider NaN distance on failure paths) are not valid JSON
                // tokens — degrade to null so one bad value never corrupts the JSONL stream.
                sb.Append(float.IsNaN(f) || float.IsInfinity(f)
                    ? "null"
                    : f.ToString("0.###", CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(double.IsNaN(d) || double.IsInfinity(d)
                    ? "null"
                    : d.ToString("0.###", CultureInfo.InvariantCulture));
                break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            case decimal m:
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                break;
            case IEnumerable<KeyValuePair<string, object?>> dict:
                WriteObject(sb, dict);
                break;
            case IDictionary rawDict:
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in rawDict)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(sb, entry.Key?.ToString() ?? "null");
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }

                sb.Append('}');
                break;
            }

            case IEnumerable seq:
            {
                sb.Append('[');
                bool first = true;
                foreach (object? item in seq)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteValue(sb, item);
                }

                sb.Append(']');
                break;
            }

            default:
                // Unknown types degrade to their string representation rather than throwing mid-run.
                WriteString(sb, value.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteString(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
