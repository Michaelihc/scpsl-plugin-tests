using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using PlayerStatsSystem;

namespace BehaviorTestHarness.Harness;

internal sealed class BehaviorEventTap
{
    private readonly DummyRegistry _dummies;

    public BehaviorEventTap(DummyRegistry dummies)
    {
        _dummies = dummies;
    }

    public void Enable()
    {
        PlayerEvents.Hurt += OnPlayerHurt;
    }

    public void Disable()
    {
        PlayerEvents.Hurt -= OnPlayerHurt;
    }

    private void OnPlayerHurt(PlayerHurtEventArgs ev)
    {
        if (!_dummies.IsOwned(ev.Player) && (ev.Attacker == null || !_dummies.IsOwned(ev.Attacker)))
        {
            return;
        }

        string attacker = ev.Attacker == null ? "server" : DummyRegistry.Describe(ev.Attacker);
        string victim = DummyRegistry.Describe(ev.Player);
        string damage = ev.DamageHandler is StandardDamageHandler standard
            ? standard.TotalDamageDealt.ToString("0.##")
            : ev.DamageHandler.GetType().Name;

        BehaviorTestLog.WriteInfo($"observed dummy hurt event attacker={attacker} victim={victim} damage={damage}");
    }
}
