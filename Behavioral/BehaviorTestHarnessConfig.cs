using System;
using System.IO;

namespace BehaviorTestHarness;

public sealed class BehaviorTestHarnessConfig
{
    private const string RepoRootEnvironmentVariable = "SCPSL_PLUGINS_METAREPO_ROOT";
    private const string UiHarnessPathEnvironmentVariable = "SCPSL_UI_HARNESS_PATH";
    private const string UiCaptureOutputEnvironmentVariable = "SCPSL_UI_CAPTURE_OUTPUT_DIRECTORY";

    public string NicknamePrefix { get; set; } = "BT";

    public bool CleanupDummiesAfterRun { get; set; } = true;

    public bool CleanupDummiesOnDisable { get; set; } = true;

    public string AutoRunScenario { get; set; } = string.Empty;

    public float AutoRunDelaySeconds { get; set; } = 8f;

    public float SmokeDamage { get; set; } = 15f;

    public int SmokeArmorPenetration { get; set; } = 100;

    public int SmokeExperienceAward { get; set; } = 25;

    public bool CaptureHsmUi { get; set; } = true;

    public bool RenderHsmUiScreenshots { get; set; } = true;

    public bool RenderHsmUiOnManualCapture { get; set; } = true;

    public string UiHarnessPath { get; set; } = GetDefaultUiHarnessPath();

    public string UiCaptureOutputDirectory { get; set; } = GetDefaultUiCaptureOutputDirectory();

    public string UiCaptureViewport { get; set; } = "1920x1080";

    public string NodeExecutable { get; set; } = "node";

    private static string GetDefaultUiHarnessPath()
    {
        string? configured = Environment.GetEnvironmentVariable(UiHarnessPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return GetFullPathWithExpandedEnvironment(configured);
        }

        string? repoRoot = Environment.GetEnvironmentVariable(RepoRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            return Path.Combine(GetFullPathWithExpandedEnvironment(repoRoot), ".tests", "UI");
        }

        return Path.Combine(".tests", "UI");
    }

    private static string GetDefaultUiCaptureOutputDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(UiCaptureOutputEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return GetFullPathWithExpandedEnvironment(configured);
        }

        return Path.Combine(GetDefaultUiHarnessPath(), "output", "captures");
    }

    private static string GetFullPathWithExpandedEnvironment(string path)
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
}
