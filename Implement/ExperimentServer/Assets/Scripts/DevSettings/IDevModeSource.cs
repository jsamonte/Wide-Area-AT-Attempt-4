namespace ARCockpit.DevSettings
{
    /// <summary>
    /// A profile that maps a placed ArUco id to a <see cref="DevMode"/> (the repurposed ones-digit of
    /// the element's tag block). Implemented by MapProfile and NavBallProfile.
    /// <see cref="DevModeController"/> reads the active marker's mode from here.
    /// </summary>
    public interface IDevModeSource
    {
        /// <summary>The dev mode for <paramref name="markerId"/>, or null if it is not one of this
        /// element's tags.</summary>
        DevMode? FindDevMode(int markerId);
    }
}
