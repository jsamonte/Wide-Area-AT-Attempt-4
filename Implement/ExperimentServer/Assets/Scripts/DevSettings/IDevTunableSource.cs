using System.Collections.Generic;

namespace ARCockpit.DevSettings
{
    /// <summary>
    /// A config asset that exposes some of its fields to the dev settings window. Implemented by
    /// MapProfile and NavBallProfile (the same precedent as IMarkerSource, which they already
    /// implement). The returned <see cref="DevTunable"/>s bind directly to this asset's fields, so the
    /// value stays in one place and editing it through the window edits the profile itself.
    /// </summary>
    public interface IDevTunableSource
    {
        void CollectTunables(List<DevTunable> into);
    }
}
