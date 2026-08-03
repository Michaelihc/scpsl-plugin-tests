using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BehaviorTestHarness.Harness;
using LabApi.Features.Wrappers;
using PlayerRoles;
using UnityEngine;

namespace BehaviorTestHarness.Scenarios;

public sealed class CustomizableUiSpectatorHudScenario : IBehaviorScenario
{
    private const string HudTag = "customizableuimeow.bottom-hud";
    private const string SpectatedHudTag = "customizableuimeow.spectated-bottom-hud";

    public string Name => "customizableui-spectator-hud";

    public string Description => "Verifies the CustomizableUIMeow secondary bottom HUD for the currently spectated player.";

    public IReadOnlyCollection<string> Aliases => ["cui-spectator", "spectator-hud"];

    public BehaviorTestResult Run(BehaviorScenarioContext context)
    {
        Player spectator = context.SpawnDummy("cui-spectator");
        Player target = context.SpawnDummy("cui-target");

        PreparePlayer(target, RoleTypeId.Scientist, new Vector3(2f, 1000f, 0f), context);
        PreparePlayer(spectator, RoleTypeId.Spectator, new Vector3(0f, 1000f, 0f), context);
        SetSpectatedTarget(spectator, target, context);
        SeedDistinctStats(spectator, target, context);

        context.Require(spectator.CurrentlySpectating == target, $"spectator target was not applied actual={spectator.CurrentlySpectating?.Nickname ?? "null"} expected={target.Nickname}");

        RefreshCustomizableUi(spectator, context);

        CapturedHint? ownHud = FindProviderHint(spectator, HudTag);
        context.Require(ownHud.HasValue, $"missing owner HSM hint id={HudTag} owner={spectator.Nickname}");

        CapturedHint? captured = FindProviderHint(spectator, SpectatedHudTag) ?? FindHint(spectator.Nickname, SpectatedHudTag);
        context.Require(captured.HasValue, $"missing HSM hint id={SpectatedHudTag} owner={spectator.Nickname}");
        CapturedHint ownerHint = ownHud.GetValueOrDefault();
        CapturedHint spectatedHint = captured.GetValueOrDefault();

        string ownerPlainText = StripRichText(ownerHint.Text);
        string plainText = StripRichText(spectatedHint.Text);
        context.Info($"owner hud captured owner={spectator.Nickname} id={ownerHint.Id} x={ownerHint.X:0.##} y={ownerHint.Y:0.##} text=\"{ownerPlainText}\"");
        context.Info($"spectator hud captured owner={spectator.Nickname} target={target.Nickname} id={spectatedHint.Id} x={spectatedHint.X:0.##} y={spectatedHint.Y:0.##} text=\"{plainText}\"");
        string expectedVisibleTargetName = target.Nickname.Substring(0, Math.Min(20, target.Nickname.Length));
        context.Require(ContainsIgnoreCase(plainText, expectedVisibleTargetName), $"spectator HUD did not include target nickname prefix text=\"{plainText}\" target={target.Nickname} expectedPrefix={expectedVisibleTargetName}");
        context.Require(ContainsIgnoreCase(plainText, "观战") || ContainsIgnoreCase(plainText, "Watching"), $"spectator HUD did not include a spectator label text=\"{plainText}\"");
        context.Require(ContainsIgnoreCase(ownerPlainText, "K:0") && ContainsIgnoreCase(ownerPlainText, "D:1"), $"owner HUD did not use seeded spectator K/D text=\"{ownerPlainText}\"");
        context.Require(ContainsIgnoreCase(plainText, "K:3") && ContainsIgnoreCase(plainText, "D:2"), $"spectator HUD did not use seeded target K/D text=\"{plainText}\"");
        context.Require(!string.Equals(ownerPlainText, plainText, StringComparison.Ordinal), "owner HUD and spectated-player HUD unexpectedly matched exactly");
        context.Require(spectatedHint.Y >= 1000f && spectatedHint.Y <= 1080f, $"spectator HUD y coordinate was outside expected bottom lane y={spectatedHint.Y:0.##}");

        string? screenshot = context.CaptureUiScreenshot("spectator-secondary-bottom-hud");
        if (!string.IsNullOrWhiteSpace(screenshot))
        {
            context.Info($"spectator hud screenshot path=\"{screenshot}\"");
        }

        context.Info($"manual inspection dummies left alive spectator={DummyRegistry.Describe(spectator)} target={DummyRegistry.Describe(target)}");
        return context.Pass();
    }

    private static void PreparePlayer(Player player, RoleTypeId role, Vector3 position, BehaviorScenarioContext context)
    {
        context.Info($"preparing dummy player={DummyRegistry.Describe(player)} role={role}");
        player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.AssignInventory);
        context.Info($"dummy role assigned player={DummyRegistry.Describe(player)} role={role}");
        player.Position = position;
        TryEnableGodMode(player, context);
        if (player.MaxHealth < 100f)
        {
            player.MaxHealth = 100f;
        }

        player.Health = player.MaxHealth;
        context.Info($"dummy player prepared player={DummyRegistry.Describe(player)} role={role} position={Format(position)} health={player.Health:0.##} isDummy={player.IsDummy} isReady={player.IsReady} god={ReadGodMode(player)}");
    }

    private static void TryEnableGodMode(Player player, BehaviorScenarioContext context)
    {
        try
        {
            player.IsGodModeEnabled = true;
        }
        catch (Exception ex)
        {
            context.Info($"dummy god mode could not be enabled player={DummyRegistry.Describe(player)} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static string ReadGodMode(Player player)
    {
        try
        {
            return player.IsGodModeEnabled.ToString();
        }
        catch (Exception ex)
        {
            return $"unknown({ex.GetType().Name})";
        }
    }

    private static void SetSpectatedTarget(Player spectator, Player target, BehaviorScenarioContext context)
    {
        object? roleManager = GetField(spectator.ReferenceHub, "roleManager") ?? GetProperty(spectator.ReferenceHub, "roleManager");
        object? spectatorRole = GetProperty(roleManager, "CurrentRole") ?? GetField(roleManager, "CurrentRole");
        string roleTypeName = spectatorRole?.GetType().FullName ?? "null";
        if (roleTypeName != "PlayerRoles.Spectating.SpectatorRole")
        {
            throw new InvalidOperationException($"Expected {spectator.Nickname} to have SpectatorRole, actual={roleTypeName}");
        }

        PropertyInfo? property = spectatorRole!.GetType().GetProperty(
            "SyncedSpectatedNetId",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null)
        {
            throw new InvalidOperationException("SpectatorRole.SyncedSpectatedNetId property was not found.");
        }

        property.SetValue(spectatorRole, target.ReferenceHub.netId);
        context.Info($"spectator target set spectator={DummyRegistry.Describe(spectator)} target={DummyRegistry.Describe(target)} netId={target.ReferenceHub.netId}");
    }

    private static void RefreshCustomizableUi(Player spectator, BehaviorScenarioContext context)
    {
        object? instance = GetCustomizableUiPluginInstance();
        Type? pluginType = instance?.GetType();
        object? displayService = pluginType?.GetField("_hudDisplayService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);
        MethodInfo? refreshPlayer = displayService?.GetType().GetMethod("RefreshPlayer", BindingFlags.Instance | BindingFlags.Public);

        if (refreshPlayer == null)
        {
            throw new InvalidOperationException("CustomizableUIMeow HudDisplayService.RefreshPlayer was not available.");
        }

        refreshPlayer.Invoke(displayService, new object[] { spectator, 0f });
        context.Info($"customizable ui refreshed player={DummyRegistry.Describe(spectator)}");
    }

    private static void SeedDistinctStats(Player spectator, Player target, BehaviorScenarioContext context)
    {
        object? instance = GetCustomizableUiPluginInstance();
        Type? pluginType = instance?.GetType();
        object? statsStore = pluginType?.GetField("_statsStore", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);

        MethodInfo? addPlaytime = statsStore?.GetType().GetMethod("AddPlaytime", BindingFlags.Instance | BindingFlags.Public);
        MethodInfo? recordKill = statsStore?.GetType().GetMethod("RecordKill", BindingFlags.Instance | BindingFlags.Public);
        MethodInfo? recordDeath = statsStore?.GetType().GetMethod("RecordDeath", BindingFlags.Instance | BindingFlags.Public);
        if (addPlaytime == null || recordKill == null || recordDeath == null)
        {
            throw new InvalidOperationException("CustomizableUIMeow PlayerStatsStore methods were not available.");
        }

        addPlaytime.Invoke(statsStore, new object[] { spectator, 65f });
        recordDeath.Invoke(statsStore, new object[] { spectator });

        addPlaytime.Invoke(statsStore, new object[] { target, 731f });
        for (int i = 0; i < 3; i++)
        {
            recordKill.Invoke(statsStore, new object?[] { target, spectator, RoleTypeId.ClassD });
        }

        for (int i = 0; i < 2; i++)
        {
            recordDeath.Invoke(statsStore, new object[] { target });
        }

        context.Info($"seeded distinct HUD stats spectator={DummyRegistry.Describe(spectator)} target={DummyRegistry.Describe(target)}");
    }

    private static CapturedHint? FindProviderHint(Player owner, string hintId)
    {
        object? instance = GetCustomizableUiPluginInstance();
        Type? pluginType = instance?.GetType();
        object? provider = pluginType?.GetField("_hintDisplayProvider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);
        object? activeHints = GetField(provider, "_activeHints");
        if (activeHints is not IEnumerable entries)
        {
            return null;
        }

        foreach (object entry in entries.Cast<object>())
        {
            object? key = GetProperty(entry, "Key");
            object? value = GetProperty(entry, "Value");
            object? hub = GetField(key, "Item1");
            string tagId = Convert.ToString(GetField(key, "Item2"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (!ReferenceEquals(hub, owner.ReferenceHub) || !string.Equals(tagId, hintId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            object? hint = GetProperty(value, "Hint");
            string hintText = hint == null ? string.Empty : ReadHintText(hint);
            return new CapturedHint(
                tagId,
                hintText.Length > 0 ? hintText : Convert.ToString(GetProperty(value, "Message"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ReadFloat(GetProperty(value, "X")) ?? ReadFloat(GetProperty(hint, "XCoordinate")) ?? 0f,
                ReadFloat(GetProperty(value, "Y")) ?? ReadFloat(GetProperty(hint, "YCoordinate")) ?? 0f);
        }

        return null;
    }

    private static object? GetCustomizableUiPluginInstance()
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static candidate => string.Equals(candidate.GetName().Name, "CustomizableUIMeow.LabApi", StringComparison.OrdinalIgnoreCase));
        Type? pluginType = assembly?.GetType("CustomizableUIMeow.LabApi.CustomizableUIMeowLabApiPlugin", throwOnError: false);
        return pluginType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null);
    }

    private static CapturedHint? FindHint(string ownerNickname, string hintId)
    {
        Type? displayType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly => assembly.GetType("HintServiceMeow.Core.Extension.PlayerDisplay", throwOnError: false))
            .FirstOrDefault(static type => type != null);
        FieldInfo? displayListField = displayType?.GetField("PlayerDisplayList", BindingFlags.NonPublic | BindingFlags.Static);
        if (displayListField?.GetValue(null) is not IEnumerable displays)
        {
            return null;
        }

        foreach (object display in displays.Cast<object>())
        {
            string owner = ReadDisplayOwner(display);
            if (!string.Equals(owner, ownerNickname, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            object? hintCollection = GetField(display, "hintCollection");
            if (hintCollection == null || GetProperty(hintCollection, "AllHints") is not IEnumerable hints)
            {
                continue;
            }

            foreach (object hint in hints)
            {
                if (ReadBool(GetProperty(hint, "Hide")))
                {
                    continue;
                }

                string id = Convert.ToString(GetProperty(hint, "Id"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.Equals(id, hintId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new CapturedHint(
                    id,
                    ReadHintText(hint),
                    ReadFloat(GetProperty(hint, "XCoordinate")) ?? 0f,
                    ReadFloat(GetProperty(hint, "YCoordinate")) ?? 0f);
            }
        }

        return null;
    }

    private static string ReadDisplayOwner(object display)
    {
        object? hub = GetProperty(display, "ReferenceHub");
        object? nicknameSync = hub == null ? null : GetField(hub, "nicknameSync") ?? GetProperty(hub, "nicknameSync");
        return Convert.ToString(GetField(nicknameSync, "myNick") ?? GetProperty(nicknameSync, "MyNick"), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string ReadHintText(object hint)
    {
        string? text = Convert.ToString(GetProperty(hint, "Text"), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text!;
        }

        object? content = GetProperty(hint, "Content");
        MethodInfo? getText = content?.GetType().GetMethod("GetText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        return Convert.ToString(getText?.Invoke(content, null), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static object? GetField(object? target, string name)
    {
        return target?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
    }

    private static object? GetProperty(object? target, string name)
    {
        return target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
    }

    private static bool ReadBool(object? value)
    {
        return value is bool boolean && boolean;
    }

    private static float? ReadFloat(object? value)
    {
        if (value == null)
        {
            return null;
        }

        return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string StripRichText(string value)
    {
        bool inTag = false;
        List<char> chars = new();
        foreach (char current in value ?? string.Empty)
        {
            if (current == '<')
            {
                inTag = true;
                continue;
            }

            if (current == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag)
            {
                chars.Add(current);
            }
        }

        return new string(chars.ToArray());
    }

    private static bool ContainsIgnoreCase(string value, string expected)
    {
        return value?.IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Format(Vector3 value) => $"{value.x:0.##},{value.y:0.##},{value.z:0.##}";

    private readonly struct CapturedHint
    {
        public CapturedHint(string id, string text, float x, float y)
        {
            Id = id;
            Text = text;
            X = x;
            Y = y;
        }

        public string Id { get; }

        public string Text { get; }

        public float X { get; }

        public float Y { get; }
    }
}
