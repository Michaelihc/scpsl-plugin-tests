using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using Mirror;
using NetworkManagerUtils.Dummies;

namespace BehaviorTestHarness.Harness;

public sealed class DummyRegistry
{
    private readonly HashSet<int> _ownedPlayerIds = [];
    private readonly string _nicknamePrefix;

    public DummyRegistry(string nicknamePrefix)
    {
        _nicknamePrefix = string.IsNullOrWhiteSpace(nicknamePrefix) ? "BT" : nicknamePrefix.Trim();
    }

    public Player Spawn(string label, BehaviorTestLog log)
    {
        string nickname = $"{_nicknamePrefix}-{label}-{Guid.NewGuid():N}";
        if (nickname.Length > 32)
        {
            nickname = nickname.Substring(0, 32);
        }

        ReferenceHub hub = DummyUtils.SpawnDummy(nickname)
            ?? throw new InvalidOperationException("DummyUtils.SpawnDummy returned null. The server player prefab is not ready.");

        NormalizeDummyIdentity(hub, nickname, log);

        Player player = Player.Get(hub)
            ?? throw new InvalidOperationException("LabAPI could not wrap the spawned dummy ReferenceHub.");

        _ownedPlayerIds.Add(player.PlayerId);
        log.Info($"dummy player spawned role={label} player={Describe(player)}");
        return player;
    }

    private static void NormalizeDummyIdentity(ReferenceHub hub, string nickname, BehaviorTestLog log)
    {
        try
        {
            hub.nicknameSync.MyNick = nickname;
            hub.authManager.UserId = "ID_Dummy";
        }
        catch (Exception ex)
        {
            log.Info($"dummy identity normalization skipped player={nickname} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    public bool IsOwned(Player player)
    {
        return player.IsDummy && (_ownedPlayerIds.Contains(player.PlayerId) || player.Nickname.StartsWith(_nicknamePrefix + "-", StringComparison.OrdinalIgnoreCase));
    }

    public int DestroyOwnedDummies(BehaviorTestLog log)
    {
        List<Player> owned = Player.DummyList.Where(IsOwned).ToList();
        foreach (Player player in owned)
        {
            string description = Describe(player);
            if (player.GameObject != null)
            {
                NetworkServer.Destroy(player.GameObject);
            }

            _ownedPlayerIds.Remove(player.PlayerId);
            log.Info($"dummy player destroyed player={description}");
        }

        return owned.Count;
    }

    public static string Describe(Player player) => $"{player.Nickname}#{player.PlayerId}";
}
