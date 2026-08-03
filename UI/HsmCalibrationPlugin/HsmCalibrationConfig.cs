using System.ComponentModel;

namespace HsmCalibration;

public sealed class HsmCalibrationConfig
{
    [Description("Whether the calibration plugin is enabled.")]
    public bool IsEnabled { get; set; } = true;

    [Description("Whether to automatically start the calibration ring for players who join.")]
    public bool AutoStart { get; set; } = true;

    [Description("Calibration display mode. Use 'rectangle' for static edge tags or 'orbit' for animated tags.")]
    public string Mode { get; set; } = "rectangle";

    [Description("Number of tags on the top and bottom edges in rectangle mode.")]
    public int HorizontalMarkerCount { get; set; } = 9;

    [Description("Number of tags on the left and right edges in rectangle mode.")]
    public int VerticalMarkerCount { get; set; } = 5;

    [Description("Horizontal inset for readable rectangle edge text.")]
    public float EdgeInsetX { get; set; } = 96f;

    [Description("Vertical inset for readable rectangle edge text.")]
    public float EdgeInsetY { get; set; } = 34f;

    [Description("Whether rectangle mode should show TOP/BOTTOM/LEFT/RIGHT edge labels.")]
    public bool ShowEdgeLabels { get; set; } = true;

    [Description("Left edge X coordinate in rectangle mode.")]
    public float LeftX { get; set; } = -1780f;

    [Description("Right edge X coordinate in rectangle mode.")]
    public float RightX { get; set; } = 1780f;

    [Description("Top edge Y coordinate in rectangle mode.")]
    public float TopY { get; set; } = 30f;

    [Description("Bottom edge Y coordinate in rectangle mode.")]
    public float BottomY { get; set; } = 1060f;

    [Description("Number of moving labels in the calibration ring.")]
    public int MarkerCount { get; set; } = 12;

    [Description("Horizontal orbit radius in HSM coordinates.")]
    public float RadiusX { get; set; } = 900f;

    [Description("Vertical orbit radius in HSM coordinates.")]
    public float RadiusY { get; set; } = 420f;

    [Description("Horizontal orbit center in HSM coordinates.")]
    public float CenterX { get; set; } = 0f;

    [Description("Vertical orbit center in HSM coordinates.")]
    public float CenterY { get; set; } = 540f;

    [Description("Seconds for one full orbit.")]
    public float OrbitSeconds { get; set; } = 18f;

    [Description("Minimum seconds between coordinate updates.")]
    public float RefreshIntervalSeconds { get; set; } = 0.2f;

    [Description("Text size for moving labels.")]
    public int TextSize { get; set; } = 18;

    [Description("Line height for moving labels.")]
    public float LineHeight { get; set; } = 0f;

    [Description("Whether each marker should include its current HSM coordinates in the text.")]
    public bool ShowCoordinates { get; set; } = false;

    [Description("Color used for even-numbered markers.")]
    public string PrimaryColor { get; set; } = "#80FFEA";

    [Description("Color used for odd-numbered markers.")]
    public string SecondaryColor { get; set; } = "#FFCB45";

    [Description("Text prefix shown on every moving marker.")]
    public string LabelPrefix { get; set; } = "HSM";
}
