namespace PhoneBox.Agent;

/// <summary>
/// The server-owned capture settings derived from one Screen widget. No socket message can change these;
/// a widget's own options are the only source, and unknown values normalize to safe defaults.
/// </summary>
internal sealed record ScreenOptions(string Mode, int Fps, int Bitrate, string Cursor, bool Control)
{
    public static ScreenOptions From(WidgetConfig widget)
    {
        ArgumentNullException.ThrowIfNull(widget);

        var mode = widget.Str("mode") is "mirror" ? "mirror" : "extend";

        var fpsRaw = widget.Int("fps");
        var fps = fpsRaw is 10 or 15 or 30 ? fpsRaw.Value : 30;

        var bitrate = widget.Str("quality") switch
        {
            "low" => 4_000_000,
            "high" => 16_000_000,
            _ => 8_000_000,
        };

        var cursor = widget.Str("cursor") is "hidden" ? "hidden" : "embedded";

        var control = widget.Bool("control") == true;

        return new ScreenOptions(mode, fps, bitrate, cursor, control);
    }
}

/// <summary>Identity of one active screen session. Device is an ordinal id; never a label or output position.</summary>
internal readonly record struct ScreenScope(string Device, string Mode, bool Control);
