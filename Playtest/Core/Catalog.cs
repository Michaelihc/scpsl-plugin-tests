using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PlaytestHarness.Telemetry;

namespace PlaytestHarness.Core;

/// <summary>
/// Reflection discovery of <see cref="Scenario"/> subclasses across ALL loaded assemblies, so plugin
/// projects can ship their own scenarios (phase D pilot migration) without registering anything.
/// Duplicate names/aliases are detected and reported loudly; EVERY scenario claiming a clashing
/// key is excluded from runs (load-order independent), until the clash is renamed away.
/// </summary>
internal sealed class Catalog
{
    private readonly Dictionary<string, Scenario> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Scenario>> _bySuite = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Scenario> _ordered = new();
    private readonly List<string> _problems = new();

    public IReadOnlyList<Scenario> Scenarios => _ordered;

    public IReadOnlyList<string> Problems => _problems;

    public bool TryGet(string nameOrAlias, out Scenario scenario) => _byName.TryGetValue(nameOrAlias, out scenario!);

    public bool TryGetSuite(string name, out IReadOnlyList<Scenario> scenarios)
    {
        if (_bySuite.TryGetValue(name, out List<Scenario> suite))
        {
            scenarios = suite;
            return true;
        }

        scenarios = Array.Empty<Scenario>();
        return false;
    }

    public int Reload()
    {
        _byName.Clear();
        _bySuite.Clear();
        _ordered.Clear();
        _problems.Clear();

        List<Scenario> discovered = new();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray()!;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || !typeof(Scenario).IsAssignableFrom(type))
                {
                    continue;
                }

                try
                {
                    if (Activator.CreateInstance(type) is Scenario scenario)
                    {
                        discovered.Add(scenario);
                    }
                }
                catch (Exception e)
                {
                    _problems.Add($"failed to instantiate scenario type {type.FullName}: {e.Message}");
                }
            }
        }

        // Collision policy: a name/alias claimed by more than one scenario TYPE is banned outright
        // and EVERY scenario carrying it is excluded. Keeping any one of them would make
        // 'ptest run <key>' depend on arbitrary assembly load order.
        Dictionary<string, List<Scenario>> claims = new(StringComparer.OrdinalIgnoreCase);
        foreach (Scenario scenario in discovered)
        {
            foreach (string key in Keys(scenario).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!claims.TryGetValue(key, out List<Scenario> claimants))
                {
                    claimants = new List<Scenario>();
                    claims[key] = claimants;
                }

                claimants.Add(scenario);
            }
        }

        HashSet<Scenario> excluded = new();
        foreach (KeyValuePair<string, List<Scenario>> claim in claims.Where(c => c.Value.Count > 1))
        {
            _problems.Add($"duplicate scenario name/alias '{claim.Key}' claimed by {string.Join(", ", claim.Value.Select(s => s.GetType().FullName))}; ALL of them are EXCLUDED.");
            foreach (Scenario scenario in claim.Value)
            {
                excluded.Add(scenario);
            }
        }

        HashSet<string> activeKeys = discovered
            .Where(scenario => !excluded.Contains(scenario))
            .SelectMany(Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> invalidSuites = new(StringComparer.OrdinalIgnoreCase);
        foreach (string suiteName in discovered
                     .Where(scenario => !excluded.Contains(scenario))
                     .SelectMany(scenario => scenario.Suites)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(suiteName, "all", StringComparison.OrdinalIgnoreCase) || activeKeys.Contains(suiteName))
            {
                invalidSuites.Add(suiteName);
                _problems.Add(
                    $"suite name '{suiteName}' collides with a scenario name/alias or reserved selector; the suite is EXCLUDED.");
            }
        }

        // Stable order: by name, so 'run all' and suites are deterministic regardless of assembly load order.
        foreach (Scenario scenario in discovered.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (excluded.Contains(scenario))
            {
                continue;
            }

            foreach (string key in Keys(scenario))
            {
                _byName[key] = scenario;
            }

            foreach (string suiteName in scenario.Suites
                         .Where(name => !string.IsNullOrWhiteSpace(name) && !invalidSuites.Contains(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_bySuite.TryGetValue(suiteName, out List<Scenario> suite))
                {
                    suite = new List<Scenario>();
                    _bySuite[suiteName] = suite;
                }

                suite.Add(scenario);
            }

            _ordered.Add(scenario);
        }

        foreach (string problem in _problems)
        {
            EventLog.Line("CATALOG", problem, error: true);
        }

        return _ordered.Count;
    }

    private static IEnumerable<string> Keys(Scenario scenario)
    {
        yield return scenario.Name;
        foreach (string alias in scenario.Aliases)
        {
            yield return alias;
        }
    }
}
