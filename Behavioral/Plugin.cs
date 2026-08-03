using System;
using BehaviorTestHarness.Harness;
using LabApi.Features;
using LabApi.Loader.Features.Plugins;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;
using UnityObject = UnityEngine.Object;

namespace BehaviorTestHarness;

public sealed class BehaviorTestHarnessPlugin : Plugin<BehaviorTestHarnessConfig>
{
    private DummyRegistry? _dummies;
    private ExperienceLedger? _experience;
    private BehaviorEventTap? _events;
    private UiCaptureRecorder? _uiCapture;
    private BehaviorTestRunner? _runner;
    private GameObject? _autoRunObject;

    public static BehaviorTestHarnessPlugin? Instance { get; private set; }

    internal BehaviorTestRunner? Runner => _runner;

    internal DummyRegistry? Dummies => _dummies;

    public override string Name => "BehaviorTestHarness";

    public override string Description => "Headless dummy-driven behavior test harness for SCP:SL plugins.";

    public override string Author => "Codex";

    public override Version Version => new(0, 1, 0);

    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);

    public override void Enable()
    {
        Instance = this;
        _dummies = new DummyRegistry(Config.NicknamePrefix);
        _experience = new ExperienceLedger();
        _events = new BehaviorEventTap(_dummies);
        _uiCapture = new UiCaptureRecorder(Config);
        _runner = new BehaviorTestRunner(Config, _dummies, _experience, _uiCapture);

        _events.Enable();
        _uiCapture.Enable();
        Logger.Info("[BehaviorTest] harness enabled; run 'scptest run smoke' from console, query, or Remote Admin.");

        if (!string.IsNullOrWhiteSpace(Config.AutoRunScenario))
        {
            _autoRunObject = new GameObject("BehaviorTestHarness Autorun");
            UnityObject.DontDestroyOnLoad(_autoRunObject);
            _autoRunObject.AddComponent<BehaviorTestAutoRunBehaviour>().Initialize(this, Config.AutoRunScenario, Config.AutoRunDelaySeconds);
            Logger.Info($"[BehaviorTest] autorun scheduled scenario={Config.AutoRunScenario} delay={Config.AutoRunDelaySeconds:0.##}s.");
        }
    }

    public override void Disable()
    {
        if (_autoRunObject != null)
        {
            UnityObject.Destroy(_autoRunObject);
            _autoRunObject = null;
        }

        _uiCapture?.Dispose();
        _events?.Disable();

        if (Config.CleanupDummiesOnDisable)
        {
            _dummies?.DestroyOwnedDummies(new BehaviorTestLog());
        }

        _runner = null;
        _uiCapture = null;
        _events = null;
        _experience = null;
        _dummies = null;
        Instance = null;
    }

    internal void RunAutoScenario(string scenarioName)
    {
        BehaviorTestRunner? runner = _runner;
        if (runner == null)
        {
            Logger.Error("[BehaviorTest] autorun skipped because runner is not initialized.");
            return;
        }

        BehaviorTestResult result = runner.Run(scenarioName);
        if (result.Passed)
        {
            Logger.Info("[BehaviorTest] autorun " + result.CommandResponse);
        }
        else
        {
            Logger.Error("[BehaviorTest] autorun " + result.CommandResponse);
        }
    }
}

internal sealed class BehaviorTestAutoRunBehaviour : MonoBehaviour
{
    private BehaviorTestHarnessPlugin? _plugin;
    private string _scenarioName = string.Empty;
    private float _remainingSeconds;
    private bool _ran;

    public void Initialize(BehaviorTestHarnessPlugin plugin, string scenarioName, float delaySeconds)
    {
        _plugin = plugin;
        _scenarioName = scenarioName;
        _remainingSeconds = Mathf.Max(0.25f, delaySeconds);
    }

    private void Update()
    {
        if (_ran || _plugin == null)
        {
            return;
        }

        _remainingSeconds -= Time.unscaledDeltaTime;
        if (_remainingSeconds > 0f)
        {
            return;
        }

        _ran = true;
        _plugin.RunAutoScenario(_scenarioName);
    }
}
