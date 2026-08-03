using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommandSystem;
using HintServiceMeow.Core.Enum;
using HintServiceMeow.Core.Models.Hints;
using HintServiceMeow.Core.Utilities;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace HsmCalibration;

public sealed class HsmCalibrationPlugin : Plugin<HsmCalibrationConfig>
{
    private const string LogPrefix = "[HsmCalibration]";
    private HsmCalibrationService? _service;
    private GameObject? _runnerObject;

    internal static HsmCalibrationPlugin? Instance { get; private set; }

    public override string Name => "HsmCalibration";

    public override string Description => "HintServiceMeow calibration outline with optional orbiting labels.";

    public override string Author => "Codex";

    public override Version Version => new(0, 1, 0);

    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);

    public override LoadPriority Priority => LoadPriority.Lowest;

    public override void Enable()
    {
        Instance = this;

        if (!Config.IsEnabled)
        {
            Logger.Info($"{LogPrefix} disabled by config.");
            return;
        }

        _service = new HsmCalibrationService(Config);
        _runnerObject = new GameObject("HSM Calibration Runner");
        UnityEngine.Object.DontDestroyOnLoad(_runnerObject);
        _runnerObject.AddComponent<HsmCalibrationBehaviour>().Initialize(_service);

        PlayerEvents.Joined += OnPlayerJoined;
        PlayerEvents.Left += OnPlayerLeft;
        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;

        if (Config.AutoStart)
        {
            StartForReadyPlayers();
        }

        Logger.Info($"{LogPrefix} enabled. Use 'hsmcal start' to show HSM calibration labels.");
    }

    public override void Disable()
    {
        PlayerEvents.Joined -= OnPlayerJoined;
        PlayerEvents.Left -= OnPlayerLeft;
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;

        _service?.StopAll();
        _service = null;

        if (_runnerObject != null)
        {
            UnityEngine.Object.Destroy(_runnerObject);
            _runnerObject = null;
        }

        Instance = null;
        Logger.Info($"{LogPrefix} disabled.");
    }

    internal string Summary()
    {
        int active = _service?.ActiveSessionCount ?? 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "active={0}, mode={1}, rectangle=({2:0.#},{3:0.#})-({4:0.#},{5:0.#}), edgeTags={6}x{7}, inset=({8:0.#},{9:0.#}), coords={10}, labels={11}, orbitMarkers={12}, orbitCenter=({13:0.#},{14:0.#}), orbitRadius=({15:0.#},{16:0.#}), orbit={17:0.##}s, interval={18:0.###}s, size={19}",
            active,
            NormalizeMode(Config.Mode),
            Config.LeftX,
            Config.TopY,
            Config.RightX,
            Config.BottomY,
            Config.HorizontalMarkerCount,
            Config.VerticalMarkerCount,
            Config.EdgeInsetX,
            Config.EdgeInsetY,
            Config.ShowCoordinates,
            Config.ShowEdgeLabels,
            Config.MarkerCount,
            Config.CenterX,
            Config.CenterY,
            Config.RadiusX,
            Config.RadiusY,
            Config.OrbitSeconds,
            Config.RefreshIntervalSeconds,
            Config.TextSize);
    }

    internal void StartForCommandSender(ICommandSender sender, bool all)
    {
        if (all || !TryGetDisplayableSender(sender, out Player? player))
        {
            StartForReadyPlayers();
            return;
        }

        _service?.Start(player!);
    }

    internal void StopForCommandSender(ICommandSender sender, bool all)
    {
        if (all || !TryGetDisplayableSender(sender, out Player? player))
        {
            _service?.StopAll();
            return;
        }

        _service?.Stop(player!);
    }

    internal void RestartActive()
    {
        _service?.RestartActive();
    }

    private void OnWaitingForPlayers()
    {
        if (Config.AutoStart)
        {
            StartForReadyPlayers();
        }
    }

    private void OnPlayerJoined(PlayerJoinedEventArgs ev)
    {
        if (Config.AutoStart)
        {
            _service?.Start(ev.Player);
            Logger.Info($"{LogPrefix} auto-start requested for joined player {ev.Player.UserId}.");
        }
    }

    private void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        _service?.Stop(ev.Player);
    }

    private void StartForReadyPlayers()
    {
        int count = 0;
        foreach (Player player in Player.ReadyList)
        {
            _service?.Start(player);
            count++;
        }

        Logger.Info($"{LogPrefix} start requested for {count} ready player(s) in {NormalizeMode(Config.Mode)} mode.");
    }

    private static bool TryGetDisplayableSender(ICommandSender sender, out Player? player)
    {
        player = Player.Get(sender);
        return IsDisplayable(player);
    }

    internal static bool IsDisplayable(Player? player)
    {
        return player?.ReferenceHub != null && !player.ReferenceHub.isLocalPlayer;
    }

    internal static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, "orbit", StringComparison.OrdinalIgnoreCase) ? "orbit" : "rectangle";
    }
}

[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class HsmCalibrationCommand : ICommand
{
    public string Command => "hsmcal";

    public string[] Aliases => new[] { "hsmcalibration", "hsmcircle" };

    public string Description => "Shows moving HintServiceMeow calibration labels.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        HsmCalibrationPlugin? plugin = HsmCalibrationPlugin.Instance;
        if (plugin == null)
        {
            response = "HsmCalibration is not enabled.";
            return false;
        }

        if (arguments.Count == 0)
        {
            response = Usage(plugin);
            return false;
        }

        string action = Arg(arguments, 0).ToLowerInvariant();
        switch (action)
        {
            case "get":
            case "status":
                response = plugin.Summary();
                return true;

            case "start":
            case "show":
                plugin.StartForCommandSender(sender, HasAll(arguments));
                response = "Started HSM calibration. " + plugin.Summary();
                return true;

            case "stop":
            case "clear":
                plugin.StopForCommandSender(sender, HasAll(arguments));
                response = "Stopped HSM calibration. " + plugin.Summary();
                return true;

            case "restart":
            case "refresh":
                plugin.RestartActive();
                response = "Restarted active HSM calibration sessions. " + plugin.Summary();
                return true;

            case "set":
                return SetValue(plugin, arguments, additive: false, out response);

            case "add":
            case "nudge":
                return SetValue(plugin, arguments, additive: true, out response);

            default:
                response = Usage(plugin);
                return false;
        }
    }

    private static bool SetValue(HsmCalibrationPlugin plugin, ArraySegment<string> arguments, bool additive, out string response)
    {
        if (arguments.Count < 3)
        {
            response = Usage(plugin);
            return false;
        }

        string field = Arg(arguments, 1).ToLowerInvariant();
        if (field == "mode")
        {
            string mode = Arg(arguments, 2).ToLowerInvariant();
            if (mode != "rectangle" && mode != "orbit")
            {
                response = "Mode must be 'rectangle' or 'orbit'.";
                return false;
            }

            plugin.Config.Mode = mode;
            plugin.RestartActive();
            response = "Updated. " + plugin.Summary();
            return true;
        }

        if (!float.TryParse(Arg(arguments, 2), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            response = "Value must be a number.";
            return false;
        }

        HsmCalibrationConfig config = plugin.Config;
        switch (field)
        {
            case "horizontal":
            case "hmarkers":
            case "hm":
                config.HorizontalMarkerCount = ClampInt(additive ? config.HorizontalMarkerCount + value : value, 2, 40);
                break;

            case "vertical":
            case "vmarkers":
            case "vm":
                config.VerticalMarkerCount = ClampInt(additive ? config.VerticalMarkerCount + value : value, 2, 30);
                break;

            case "left":
            case "leftx":
            case "lx":
                config.LeftX = additive ? config.LeftX + value : value;
                break;

            case "right":
            case "rightx":
            case "rxedge":
                config.RightX = additive ? config.RightX + value : value;
                break;

            case "top":
            case "topy":
            case "ty":
                config.TopY = additive ? config.TopY + value : value;
                break;

            case "bottom":
            case "bottomy":
            case "by":
                config.BottomY = additive ? config.BottomY + value : value;
                break;

            case "insetx":
            case "edgex":
            case "ix":
                config.EdgeInsetX = Math.Max(0f, additive ? config.EdgeInsetX + value : value);
                break;

            case "insety":
            case "edgey":
            case "iy":
                config.EdgeInsetY = Math.Max(0f, additive ? config.EdgeInsetY + value : value);
                break;

            case "labels":
            case "edgelabels":
                config.ShowEdgeLabels = value > 0f;
                break;

            case "markers":
            case "count":
                config.MarkerCount = ClampInt(additive ? config.MarkerCount + value : value, 1, 64);
                break;

            case "radiusx":
            case "rx":
                config.RadiusX = Math.Max(1f, additive ? config.RadiusX + value : value);
                break;

            case "radiusy":
            case "ry":
                config.RadiusY = Math.Max(1f, additive ? config.RadiusY + value : value);
                break;

            case "centerx":
            case "cx":
                config.CenterX = additive ? config.CenterX + value : value;
                break;

            case "centery":
            case "cy":
                config.CenterY = additive ? config.CenterY + value : value;
                break;

            case "orbit":
            case "seconds":
            case "speed":
                config.OrbitSeconds = Math.Max(1f, additive ? config.OrbitSeconds + value : value);
                break;

            case "interval":
            case "refresh":
                config.RefreshIntervalSeconds = Math.Max(0.05f, additive ? config.RefreshIntervalSeconds + value : value);
                break;

            case "size":
                config.TextSize = ClampInt(additive ? config.TextSize + value : value, 6, 80);
                break;

            case "coords":
            case "coordinates":
                config.ShowCoordinates = value > 0f;
                break;

            default:
                response = "Unknown field. Use mode, horizontal, vertical, leftX, rightX, topY, bottomY, insetX, insetY, labels, markers, radiusX, radiusY, centerX, centerY, orbit, interval, size, or coords.";
                return false;
        }

        plugin.RestartActive();
        response = "Updated. " + plugin.Summary();
        return true;
    }

    private static bool HasAll(ArraySegment<string> arguments)
    {
        for (int i = 1; i < arguments.Count; i++)
        {
            if (string.Equals(Arg(arguments, i), "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int ClampInt(float value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, (int)Math.Round(value)));
    }

    private static string Arg(ArraySegment<string> arguments, int index)
    {
        return arguments.Array![arguments.Offset + index];
    }

    private static string Usage(HsmCalibrationPlugin plugin)
    {
        return "Usage: hsmcal status | hsmcal start [all] | hsmcal stop [all] | hsmcal refresh | hsmcal set <mode|horizontal|vertical|leftX|rightX|topY|bottomY|insetX|insetY|labels|markers|radiusX|radiusY|centerX|centerY|orbit|interval|size|coords> <value> | hsmcal add <field> <delta>. Current: " +
            plugin.Summary();
    }
}

internal sealed class HsmCalibrationService
{
    private readonly HsmCalibrationConfig _config;
    private readonly Dictionary<ReferenceHub, HsmCalibrationSession> _sessions = new();

    public HsmCalibrationService(HsmCalibrationConfig config)
    {
        _config = config;
    }

    public int ActiveSessionCount => _sessions.Count;

    public void Start(Player player)
    {
        if (!HsmCalibrationPlugin.IsDisplayable(player))
        {
            return;
        }

        Stop(player);

        HsmCalibrationSession session = new(player, _config);
        session.Start();
        _sessions[player.ReferenceHub] = session;
    }

    public void Stop(Player player)
    {
        if (player.ReferenceHub == null)
        {
            return;
        }

        if (_sessions.TryGetValue(player.ReferenceHub, out HsmCalibrationSession session))
        {
            session.Stop();
            _sessions.Remove(player.ReferenceHub);
        }
    }

    public void StopAll()
    {
        foreach (HsmCalibrationSession session in _sessions.Values.ToArray())
        {
            session.Stop();
        }

        _sessions.Clear();
    }

    public void RestartActive()
    {
        Player[] players = _sessions.Values
            .Select(session => session.Player)
            .Where(HsmCalibrationPlugin.IsDisplayable)
            .ToArray();

        StopAll();

        foreach (Player player in players)
        {
            Start(player);
        }
    }

    public void Tick(float now)
    {
        foreach (HsmCalibrationSession session in _sessions.Values.ToArray())
        {
            if (!HsmCalibrationPlugin.IsDisplayable(session.Player))
            {
                session.Stop();
                _sessions.Remove(session.Player.ReferenceHub);
                continue;
            }

            session.Tick(now);
        }
    }
}

internal sealed class HsmCalibrationSession
{
    private const string GroupName = "hsm.calibration";

    private readonly HsmCalibrationConfig _config;
    private readonly PlayerDisplay _display;
    private readonly List<Hint> _markers = new();
    private readonly string _mode;
    private float _nextUpdateTime;
    private float _startedAt;

    public HsmCalibrationSession(Player player, HsmCalibrationConfig config)
    {
        Player = player;
        _config = config;
        _display = PlayerDisplay.Get(player);
        _mode = HsmCalibrationPlugin.NormalizeMode(config.Mode);
    }

    public Player Player { get; }

    public void Start()
    {
        _startedAt = Time.realtimeSinceStartup;
        _nextUpdateTime = 0f;

        if (_mode == "orbit")
        {
            StartOrbit();
            Tick(Time.realtimeSinceStartup, force: true);
            return;
        }

        StartRectangle();
        _display.ForceUpdate(useFastUpdate: true);
    }

    public void Stop()
    {
        foreach (Hint hint in _markers)
        {
            _display.RemoveHint(hint, GroupName);
        }

        _markers.Clear();
        _display.ForceUpdate(useFastUpdate: true);
    }

    public void Tick(float now)
    {
        if (_mode == "orbit")
        {
            Tick(now, force: false);
        }
    }

    private void StartOrbit()
    {
        int count = Math.Max(1, Math.Min(64, _config.MarkerCount));
        for (int i = 0; i < count; i++)
        {
            Hint hint = CreateHint($"hsmcal.orbit.{i:00}", string.Empty, 0f, _config.CenterY, Math.Max(6, _config.TextSize));
            _markers.Add(hint);
            _display.AddHint(hint, GroupName);
        }
    }

    private void StartRectangle()
    {
        foreach ((string id, string text, float x, float y, int size) in BuildRectangleMarkers())
        {
            Hint hint = CreateHint(id, text, x, y, size);
            _markers.Add(hint);
            _display.AddHint(hint, GroupName);
        }
    }

    private Hint CreateHint(string id, string text, float x, float y, int size)
    {
        return new Hint
        {
            Id = id,
            Text = text,
            XCoordinate = x,
            YCoordinate = y,
            FontSize = Math.Max(6, size),
            LineHeight = Math.Max(0f, _config.LineHeight),
            Alignment = HintAlignment.Center,
            YCoordinateAlign = HintVerticalAlign.Middle,
            SyncSpeed = HintSyncSpeed.Fast,
        };
    }

    private IEnumerable<(string Id, string Text, float X, float Y, int Size)> BuildRectangleMarkers()
    {
        float left = Math.Min(_config.LeftX, _config.RightX);
        float right = Math.Max(_config.LeftX, _config.RightX);
        float top = Math.Min(_config.TopY, _config.BottomY);
        float bottom = Math.Max(_config.TopY, _config.BottomY);
        float width = Math.Max(1f, right - left);
        float height = Math.Max(1f, bottom - top);
        int horizontalCount = Math.Max(2, Math.Min(40, _config.HorizontalMarkerCount));
        int verticalCount = Math.Max(2, Math.Min(30, _config.VerticalMarkerCount));
        float insetX = Math.Min(width * 0.45f, Math.Max(0f, _config.EdgeInsetX));
        float insetY = Math.Min(height * 0.35f, Math.Max(0f, _config.EdgeInsetY));
        float textLeft = left + insetX;
        float textRight = right - insetX;
        float textTop = top + insetY;
        float textBottom = bottom - insetY;
        float textWidth = Math.Max(1f, textRight - textLeft);
        float textHeight = Math.Max(1f, textBottom - textTop);
        int markerSize = Math.Max(6, _config.TextSize);
        int labelSize = Math.Max(6, _config.TextSize);

        for (int i = 0; i < horizontalCount; i++)
        {
            float t = horizontalCount == 1 ? 0.5f : (float)i / (horizontalCount - 1);
            float x = textLeft + (textWidth * t);
            yield return ($"hsmcal.rect.top.{i:00}", FormatEdgeMarker("T", i, x, textTop, _config.PrimaryColor), x, textTop, markerSize);
            yield return ($"hsmcal.rect.bottom.{i:00}", FormatEdgeMarker("B", i, x, textBottom, _config.PrimaryColor), x, textBottom, markerSize);
        }

        for (int i = 0; i < verticalCount; i++)
        {
            float t = (float)(i + 1) / (verticalCount + 1);
            float y = textTop + (textHeight * t);
            yield return ($"hsmcal.rect.left.{i:00}", FormatEdgeMarker("L", i, textLeft, y, _config.SecondaryColor), textLeft, y, markerSize);
            yield return ($"hsmcal.rect.right.{i:00}", FormatEdgeMarker("R", i, textRight, y, _config.SecondaryColor), textRight, y, markerSize);
        }

        if (!_config.ShowEdgeLabels)
        {
            yield break;
        }

        float labelY = textTop + Math.Min(82f, textHeight * 0.18f);
        float bottomLabelY = textBottom - Math.Min(82f, textHeight * 0.18f);
        float sideLabelOffset = Math.Min(170f, textWidth * 0.16f);
        yield return ("hsmcal.rect.label.top", FormatEdgeLabel("TOP EDGE", _config.PrimaryColor), (textLeft + textRight) / 2f, labelY, labelSize);
        yield return ("hsmcal.rect.label.bottom", FormatEdgeLabel("BOTTOM EDGE", _config.PrimaryColor), (textLeft + textRight) / 2f, bottomLabelY, labelSize);
        yield return ("hsmcal.rect.label.left", FormatEdgeLabel("LEFT EDGE", _config.SecondaryColor), textLeft + sideLabelOffset, labelY, labelSize);
        yield return ("hsmcal.rect.label.right", FormatEdgeLabel("RIGHT EDGE", _config.SecondaryColor), textRight - sideLabelOffset, labelY, labelSize);
    }

    private void Tick(float now, bool force)
    {
        if (!force && now < _nextUpdateTime)
        {
            return;
        }

        _nextUpdateTime = now + Math.Max(0.05f, _config.RefreshIntervalSeconds);

        int count = _markers.Count;
        if (count == 0)
        {
            return;
        }

        float orbitSeconds = Math.Max(1f, _config.OrbitSeconds);
        double baseAngle = ((now - _startedAt) / orbitSeconds) * Math.PI * 2d;

        for (int i = 0; i < count; i++)
        {
            double angle = baseAngle + (Math.PI * 2d * i / count);
            float x = _config.CenterX + ((float)Math.Cos(angle) * _config.RadiusX);
            float y = _config.CenterY + ((float)Math.Sin(angle) * _config.RadiusY);
            Hint hint = _markers[i];

            hint.XCoordinate = x;
            hint.YCoordinate = y;
            hint.Text = FormatMarker(i, x, y);
        }
    }

    private string FormatMarker(int index, float x, float y)
    {
        string color = index % 2 == 0 ? _config.PrimaryColor : _config.SecondaryColor;
        string prefix = string.IsNullOrWhiteSpace(_config.LabelPrefix) ? "HSM" : _config.LabelPrefix.Trim();

        if (_config.ShowCoordinates)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "<color={0}>{1} tag {2:00}</color>\n<size={3}><color=#FFFFFF>x{4:0} y{5:0}</color></size>",
                color,
                prefix,
                index,
                Math.Max(6, _config.TextSize - 4),
                x,
                y);
        }

        return string.Format(CultureInfo.InvariantCulture, "<color={0}>{1} tag {2:00}</color>", color, prefix, index);
    }

    private string FormatEdgeMarker(string edge, int index, float x, float y, string color)
    {
        if (_config.ShowCoordinates)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "<color={0}>{1}{2:00}</color>\n<size={3}><color=#FFFFFF>{4:0},{5:0}</color></size>",
                color,
                edge,
                index,
                Math.Max(6, _config.TextSize - 5),
                x,
                y);
        }

        return string.Format(CultureInfo.InvariantCulture, "<color={0}>{1}{2:00}</color>", color, edge, index);
    }

    private static string FormatEdgeLabel(string label, string color)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "<b><color={0}>{1}</color></b>",
            color,
            label);
    }
}

internal sealed class HsmCalibrationBehaviour : MonoBehaviour
{
    private HsmCalibrationService? _service;

    public void Initialize(HsmCalibrationService service)
    {
        _service = service;
    }

    private void Update()
    {
        _service?.Tick(Time.realtimeSinceStartup);
    }
}
