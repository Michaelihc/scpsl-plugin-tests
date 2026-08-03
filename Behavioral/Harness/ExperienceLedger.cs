using System.Collections.Generic;
using LabApi.Features.Wrappers;

namespace BehaviorTestHarness.Harness;

public sealed class ExperienceLedger
{
    private readonly Dictionary<int, int> _experienceByPlayerId = [];

    public int Get(Player player)
    {
        return _experienceByPlayerId.TryGetValue(player.PlayerId, out int experience) ? experience : 0;
    }

    public int Add(Player player, int amount)
    {
        int next = Get(player) + amount;
        _experienceByPlayerId[player.PlayerId] = next;
        return next;
    }
}
