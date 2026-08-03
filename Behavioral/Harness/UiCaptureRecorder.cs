using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace BehaviorTestHarness.Harness;

internal sealed class UiCaptureRecorder : IDisposable
{
    private const string HsmPlayerDisplayTypeName = "HintServiceMeow.Core.Utilities.PlayerDisplay";

    private readonly BehaviorTestHarnessConfig _config;
    private readonly object _gate = new();
    private string? _scenarioName;
    private string? _scenarioDirectory;
    private string? _latestJsonPath;
    private int _sequence;
    private bool _hsmUnavailableLogged;

    public UiCaptureRecorder(BehaviorTestHarnessConfig config)
    {
        _config = config;
    }

    public void Enable()
    {
    }

    public void Dispose()
    {
        ClearScenario();
    }

    public void BeginScenario(string scenarioName, BehaviorTestLog log)
    {
        if (!_config.CaptureHsmUi)
        {
            return;
        }

        lock (_gate)
        {
            _scenarioName = Sanitize(scenarioName);
            _sequence = 0;
            _latestJsonPath = null;
            _scenarioDirectory = Path.Combine(_config.UiCaptureOutputDirectory, _scenarioName);
            Directory.CreateDirectory(_scenarioDirectory);
        }
    }

    public void EndScenario(BehaviorTestLog log)
    {
        if (!_config.CaptureHsmUi)
        {
            ClearScenario();
            return;
        }

        string? jsonPath = CaptureSnapshot("final", log);
        if (_config.RenderHsmUiScreenshots && jsonPath != null)
        {
            RenderSnapshot(jsonPath, "final", log);
        }

        ClearScenario();
    }

    public string? RenderLatest(string label, BehaviorTestLog log)
    {
        string? jsonPath = CaptureSnapshot(label, log);
        if (jsonPath == null)
        {
            return null;
        }

        return _config.RenderHsmUiOnManualCapture ? RenderSnapshot(jsonPath, label, log) : jsonPath;
    }

    private string? CaptureSnapshot(string label, BehaviorTestLog log)
    {
        if (!_config.CaptureHsmUi)
        {
            log.Info("ui capture skipped reason=hsm-capture-disabled");
            return null;
        }

        string? scenarioName;
        string? scenarioDirectory;
        int sequence;

        lock (_gate)
        {
            scenarioName = _scenarioName;
            scenarioDirectory = _scenarioDirectory;
            sequence = ++_sequence;
        }

        if (string.IsNullOrWhiteSpace(scenarioName) || string.IsNullOrWhiteSpace(scenarioDirectory))
        {
            log.Info("ui capture skipped reason=no-active-scenario");
            return null;
        }

        IReadOnlyList<CapturedHintEntry> entries = ReadHsmEntries(log);
        if (entries.Count == 0)
        {
            log.Info("ui capture skipped reason=no-hsm-snapshot");
            return null;
        }

        string jsonPath = Path.Combine(scenarioDirectory!, $"{scenarioName}.{sequence:000}.{Sanitize(label)}.json");
        File.WriteAllText(jsonPath, SerializeSnapshot(scenarioName!, label, entries), Encoding.UTF8);

        lock (_gate)
        {
            _latestJsonPath = jsonPath;
        }

        log.Info($"ui snapshot captured source=hsm scenario={scenarioName} label={Sanitize(label)} entries={entries.Count} path=\"{jsonPath}\"");
        return jsonPath;
    }

    private IReadOnlyList<CapturedHintEntry> ReadHsmEntries(BehaviorTestLog log)
    {
        Type? displayType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(HsmPlayerDisplayTypeName, throwOnError: false))
            .FirstOrDefault(type => type != null);

        if (displayType == null)
        {
            LogHsmUnavailable(log, "hsm-player-display-type-not-found");
            return Array.Empty<CapturedHintEntry>();
        }

        FieldInfo? displayListField = displayType.GetField("PlayerDisplayList", BindingFlags.NonPublic | BindingFlags.Static);
        if (displayListField?.GetValue(null) is not IEnumerable displayEnumerable)
        {
            LogHsmUnavailable(log, "hsm-player-display-list-not-found");
            return Array.Empty<CapturedHintEntry>();
        }

        List<object> displays = displayEnumerable.Cast<object>().ToList();
        for (int i = 0; i < displays.Count; i++)
        {
            IReadOnlyList<CapturedHintEntry> entries = ReadDisplayEntries(displays[i], i);
            if (entries.Count > 0)
            {
                return entries;
            }
        }

        return Array.Empty<CapturedHintEntry>();
    }

    private void LogHsmUnavailable(BehaviorTestLog log, string reason)
    {
        if (_hsmUnavailableLogged)
        {
            return;
        }

        log.Info($"ui capture waiting reason={reason}");
        _hsmUnavailableLogged = true;
    }

    private static IReadOnlyList<CapturedHintEntry> ReadDisplayEntries(object display, int displayIndex)
    {
        object? hintCollection = GetField(display, "hintCollection");
        if (hintCollection == null || GetProperty(hintCollection, "AllHints") is not IEnumerable hints)
        {
            return Array.Empty<CapturedHintEntry>();
        }

        List<CapturedHintEntry> captured = new();
        string owner = ReadDisplayOwner(display, displayIndex);
        int sequence = 0;

        foreach (object hint in hints)
        {
            if (ReadBool(GetProperty(hint, "Hide")))
            {
                continue;
            }

            CapturedHintEntry? entry = ReadHintEntry(hint, owner, sequence++);
            if (entry.HasValue)
            {
                captured.Add(entry.Value);
            }
        }

        return captured;
    }

    private static string ReadDisplayOwner(object display, int displayIndex)
    {
        object? hub = GetProperty(display, "ReferenceHub");
        object? nicknameSync = hub == null ? null : GetField(hub, "nicknameSync") ?? GetProperty(hub, "nicknameSync");
        string? nickname = Convert.ToString(GetField(nicknameSync, "myNick") ?? GetField(nicknameSync, "MyNick") ?? GetProperty(nicknameSync, "MyNick"), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(nickname) ? $"hsm.display.{displayIndex}" : $"hsm.player.{nickname}";
    }

    private static CapturedHintEntry? ReadHintEntry(object hint, string owner, int sequence)
    {
        string text = ReadHintText(hint);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string id = Convert.ToString(GetProperty(hint, "Id"), CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Convert.ToString(GetProperty(hint, "Guid"), CultureInfo.InvariantCulture) ?? $"hint.{sequence}";
        }

        float x = ReadNullableFloat(GetProperty(hint, "XCoordinate"))
            ?? ReadNullableFloat(GetProperty(hint, "TargetX"))
            ?? 0f;
        float y = ReadNullableFloat(GetProperty(hint, "YCoordinate"))
            ?? ReadNullableFloat(GetProperty(hint, "TargetY"))
            ?? 540f;

        return new CapturedHintEntry(
            owner,
            id,
            text,
            x,
            y,
            NormalizeAlignment(GetProperty(hint, "Alignment")),
            NormalizeVerticalAlign(GetProperty(hint, "YCoordinateAlign")),
            ReadInt(GetProperty(hint, "FontSize")),
            ReadInt(GetProperty(hint, "Priority")),
            null,
            false,
            sequence);
    }

    private static string ReadHintText(object hint)
    {
        string? text = Convert.ToString(GetProperty(hint, "Text"), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text!;
        }

        object? content = GetProperty(hint, "Content");
        MethodInfo? getText = content?.GetType().GetMethod("GetText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        return Convert.ToString(getText?.Invoke(content, null), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private string? RenderSnapshot(string jsonPath, string label, BehaviorTestLog log)
    {
        string? scenarioDirectory;
        string scenarioName;

        lock (_gate)
        {
            scenarioDirectory = _scenarioDirectory;
            scenarioName = _scenarioName ?? "unknown";
        }

        if (string.IsNullOrWhiteSpace(scenarioDirectory))
        {
            return null;
        }

        string outputPath = Path.Combine(scenarioDirectory!, scenarioName + "." + Sanitize(label) + ".png");
        return RenderImage(jsonPath, outputPath, log) ? outputPath : null;
    }

    private bool RenderImage(string jsonPath, string outputPath, BehaviorTestLog? log)
    {
        try
        {
            if (!Directory.Exists(_config.UiHarnessPath))
            {
                log?.Error($"ui screenshot render failed reason=ui-harness-path-not-found path=\"{_config.UiHarnessPath}\"");
                return false;
            }

            string rendererPath = Path.Combine(_config.UiHarnessPath, "render-image.js");
            if (!File.Exists(rendererPath))
            {
                log?.Error($"ui screenshot render failed reason=ui-renderer-not-found path=\"{rendererPath}\"");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            ProcessStartInfo startInfo = new()
            {
                FileName = _config.NodeExecutable,
                WorkingDirectory = _config.UiHarnessPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Arguments = $"render-image.js --input \"{jsonPath}\" --output \"{outputPath}\" --viewport {_config.UiCaptureViewport}";

            using Process process = Process.Start(startInfo)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                log?.Error("ui screenshot render failed error=renderer-timeout");
                return false;
            }

            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                log?.Info($"ui screenshot rendered path=\"{outputPath}\" viewport={_config.UiCaptureViewport}");
                return true;
            }

            log?.Error($"ui screenshot render failed exit={process.ExitCode} stdout={Trim(stdout)} stderr={Trim(stderr)}");
            return false;
        }
        catch (Exception ex)
        {
            log?.Error($"ui screenshot render failed error={ex.GetBaseException().Message}");
            return false;
        }
    }

    private void ClearScenario()
    {
        lock (_gate)
        {
            _scenarioName = null;
            _scenarioDirectory = null;
            _latestJsonPath = null;
            _sequence = 0;
        }
    }

    private static string SerializeSnapshot(string scenarioName, string label, IReadOnlyList<CapturedHintEntry> entries)
    {
        StringBuilder builder = new();
        builder.Append("{\n");
        WriteProperty(builder, 1, "scenario", scenarioName, comma: true);
        WriteProperty(builder, 1, "label", label, comma: true);
        WriteProperty(builder, 1, "capturedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), comma: true);
        builder.Append("  \"entries\": [\n");
        for (int i = 0; i < entries.Count; i++)
        {
            CapturedHintEntry entry = entries[i];
            builder.Append("    {\n");
            WriteProperty(builder, 3, "id", entry.Id, comma: true);
            WriteProperty(builder, 3, "owner", entry.Owner, comma: true);
            WriteProperty(builder, 3, "system", "hsm", comma: true);
            WriteProperty(builder, 3, "text", entry.Text, comma: true);
            WriteNumberProperty(builder, 3, "x", entry.X, comma: true);
            WriteNumberProperty(builder, 3, "y", entry.Y, comma: true);
            WriteProperty(builder, 3, "alignment", entry.Alignment, comma: true);
            WriteProperty(builder, 3, "verticalAlign", entry.VerticalAlign, comma: true);
            WriteNumberProperty(builder, 3, "textSize", entry.TextSize, comma: true);
            WriteNumberProperty(builder, 3, "priority", entry.Priority, comma: true);
            WriteProperty(builder, 3, "color", entry.Color, comma: true);
            WriteBoolProperty(builder, 3, "preserveLowercase", entry.PreserveLowercase, comma: true);
            WriteNumberProperty(builder, 3, "sequence", entry.Sequence, comma: false);
            builder.Append("    }").Append(i + 1 == entries.Count ? "\n" : ",\n");
        }

        builder.Append("  ]\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    private static void WriteProperty(StringBuilder builder, int indent, string name, string? value, bool comma)
    {
        builder.Append(' ', indent * 2)
            .Append('"').Append(name).Append("\": ")
            .Append(value == null ? "null" : "\"" + EscapeJson(value) + "\"")
            .Append(comma ? ",\n" : "\n");
    }

    private static void WriteNumberProperty(StringBuilder builder, int indent, string name, float? value, bool comma)
    {
        builder.Append(' ', indent * 2)
            .Append('"').Append(name).Append("\": ")
            .Append(value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "null")
            .Append(comma ? ",\n" : "\n");
    }

    private static void WriteBoolProperty(StringBuilder builder, int indent, string name, bool value, bool comma)
    {
        builder.Append(' ', indent * 2)
            .Append('"').Append(name).Append("\": ")
            .Append(value ? "true" : "false")
            .Append(comma ? ",\n" : "\n");
    }

    private static object? GetProperty(object? instance, string name)
    {
        return instance?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    }

    private static object? GetField(object? instance, string name)
    {
        return instance?.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
    }

    private static float? ReadNullableFloat(object? value)
    {
        return value == null ? null : Convert.ToSingle(value, CultureInfo.InvariantCulture);
    }

    private static int ReadInt(object? value)
    {
        return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool ReadBool(object? value)
    {
        return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static string NormalizeAlignment(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant() ?? "center";
        return text == "left" || text == "right" ? text : "center";
    }

    private static string NormalizeVerticalAlign(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant() ?? "middle";
        return text == "top" || text == "bottom" ? text : "middle";
    }

    private static string EscapeJson(string value)
    {
        StringBuilder builder = new(value.Length + 16);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string((value ?? "capture").Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }

    private static string Trim(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 400 ? value : value.Substring(0, 400);
    }

    private readonly struct CapturedHintEntry
    {
        public CapturedHintEntry(
            string owner,
            string id,
            string text,
            float x,
            float y,
            string alignment,
            string verticalAlign,
            int textSize,
            int priority,
            string? color,
            bool preserveLowercase,
            int sequence)
        {
            Owner = owner;
            Id = id;
            Text = text;
            X = x;
            Y = y;
            Alignment = alignment;
            VerticalAlign = verticalAlign;
            TextSize = textSize;
            Priority = priority;
            Color = color;
            PreserveLowercase = preserveLowercase;
            Sequence = sequence;
        }

        public string Owner { get; }

        public string Id { get; }

        public string Text { get; }

        public float X { get; }

        public float Y { get; }

        public string Alignment { get; }

        public string VerticalAlign { get; }

        public int TextSize { get; }

        public int Priority { get; }

        public string? Color { get; }

        public bool PreserveLowercase { get; }

        public int Sequence { get; }
    }
}
