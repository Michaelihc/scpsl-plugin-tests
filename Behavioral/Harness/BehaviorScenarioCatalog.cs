using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BehaviorTestHarness.Harness;

internal sealed class BehaviorScenarioCatalog
{
    private readonly Dictionary<string, IBehaviorScenario> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private BehaviorScenarioCatalog(IReadOnlyList<IBehaviorScenario> scenarios)
    {
        Scenarios = scenarios;

        foreach (IBehaviorScenario scenario in scenarios)
        {
            RegisterKey(scenario.Name, scenario);
            foreach (string alias in scenario.Aliases)
            {
                RegisterKey(alias, scenario);
            }
        }
    }

    public IReadOnlyList<IBehaviorScenario> Scenarios { get; }

    public static BehaviorScenarioCatalog Discover(Assembly assembly)
    {
        return Discover([assembly]);
    }

    public static BehaviorScenarioCatalog DiscoverLoadedAssemblies()
    {
        return Discover(AppDomain.CurrentDomain.GetAssemblies());
    }

    public static BehaviorScenarioCatalog Discover(IEnumerable<Assembly> assemblies)
    {
        List<IBehaviorScenario> scenarios = assemblies
            .Distinct()
            .SelectMany(GetLoadableTypes)
            .Where(static type => !type.IsAbstract && typeof(IBehaviorScenario).IsAssignableFrom(type))
            .Where(static type => type.GetConstructor(Type.EmptyTypes) != null)
            .Select(static type => (IBehaviorScenario)Activator.CreateInstance(type))
            .OrderBy(static scenario => scenario.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BehaviorScenarioCatalog(scenarios);
    }

    public bool TryGet(string key, out IBehaviorScenario scenario)
    {
        return _byKey.TryGetValue(key, out scenario);
    }

    private void RegisterKey(string key, IBehaviorScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException($"Behavior scenario '{scenario.GetType().FullName}' has an empty name or alias.");
        }

        if (_byKey.TryGetValue(key, out IBehaviorScenario existing))
        {
            throw new InvalidOperationException($"Duplicate behavior scenario key '{key}' on '{scenario.GetType().FullName}' and '{existing.GetType().FullName}'.");
        }

        _byKey[key] = scenario;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type != null)!;
        }
    }
}
