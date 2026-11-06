namespace BDNukes
{
    /// <summary>
    /// A weapon the strike planner window can list, check and launch: implemented by the part modules of BDNukes'
    /// strike vehicles (orbit-to-ground, interplanetary). BDArmory GPS ballistic missiles are wrapped separately by
    /// the planner. StrikeTarget is defined in NukeStrikeMath.cs.
    /// </summary>
    internal interface IStrikeWeapon
    {
        /// <summary>The part carrying the module (shown in the planner, used to find the vessel).</summary>
        Part StrikePart { get; }

        /// <summary>Short line for the planner list, e.g. "Orbital strike vehicle, 2 warheads".</summary>
        string StrikeDescription { get; }

        /// <summary>
        /// Can this weapon, from where its vessel is now, hit the target? `why` is a one-line reason shown in the
        /// window either way (delta-v margin, flight time, "target body not in this SOI", ...). `flightTime` is the
        /// estimated seconds from launch to impact (NaN if unknown).
        /// </summary>
        bool CanReach(StrikeTarget target, out string why, out double flightTime);

        /// <summary>Starts the attack. Returns the vessel to follow with the camera (or null), and a status line.</summary>
        Vessel Launch(StrikeTarget target, out string status);
    }
}
