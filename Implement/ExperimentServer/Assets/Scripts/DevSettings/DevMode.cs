namespace ARCockpit.DevSettings
{
    /// <summary>
    /// What a dev-mode ArUco tag does when it places an element. The ones-digit of an element's tag
    /// block selects this (e.g. NavBall 220 = Window, 221 = Overrides, 222 = Defaults; map 210/211/212
    /// the same), so the printed tag both places the element AND picks how the dev settings apply. Read
    /// by <see cref="DevModeController"/> from the element profile.
    /// </summary>
    public enum DevMode
    {
        /// <summary>Apply the saved overrides AND show the live dev settings window.</summary>
        Window = 0,
        /// <summary>Apply the saved overrides, but do not show the window (a clean preview of a setup).</summary>
        Overrides = 1,
        /// <summary>Ignore overrides entirely: the pure Unity-authored profile values (a hardcoded build).</summary>
        Defaults = 2,
    }
}
