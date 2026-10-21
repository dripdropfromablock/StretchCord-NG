namespace StretchCordNG.ViewModels
{
    /// <summary>Integer-valued choice shown in a combo box (FPS, latency, …).</summary>
    public sealed record ValueOption(int Value, string Label);
}
