namespace StretchCordNG.Models
{
    /// <summary>
    /// A manual correction applied on top of the detected pointer mapping, remembered per
    /// game.
    ///
    /// The automatic mapping has to infer which coordinate space the pointer is reported
    /// in, and a game can always turn up that defeats the inference. Rather than keep
    /// guessing, this lets the one case that misbehaves be fixed by hand: the multipliers
    /// and offsets are in frame pixels and default to the identity, so a game nobody has
    /// touched maps exactly as the detection computed.
    /// </summary>
    public sealed class PointerCalibration
    {
        /// <summary>Multiplier applied to the mapped position. 1 = as detected.</summary>
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;

        /// <summary>Shift added after scaling, in frame pixels.</summary>
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }

        public bool IsIdentity => ScaleX == 1 && ScaleY == 1 && OffsetX == 0 && OffsetY == 0;

        public PointerCalibration Clone() => new()
        {
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            OffsetX = OffsetX,
            OffsetY = OffsetY
        };

        /// <summary>Short human-readable form used in the log and the diagnostics report.</summary>
        public string Describe() => IsIdentity
            ? "as detected"
            : $"scale {ScaleX:0.###}x{ScaleY:0.###}, offset {OffsetX:+0.##;-0.##;0},{OffsetY:+0.##;-0.##;0} px";

        /// <summary>Keeps a hand-edited or hand-written value inside sane bounds.</summary>
        public void Clamp()
        {
            ScaleX = Math.Clamp(ScaleX, 0.1, 5);
            ScaleY = Math.Clamp(ScaleY, 0.1, 5);
            OffsetX = Math.Clamp(OffsetX, -2000, 2000);
            OffsetY = Math.Clamp(OffsetY, -2000, 2000);
        }
    }
}
