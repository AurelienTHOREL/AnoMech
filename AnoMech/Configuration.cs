using Dalamud.Configuration;
using System;

namespace AnoMech;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool OpenSimMenuOnInn { get; set; } = true;
    public bool OpenSimMenuOnSupportedInstanceSolo { get; set; } = false;
    public bool EnableEventLogging { get; set; } = false;
    public bool SuppressBgm { get; set; } = true;

    // Multiplayer relay address (see Relay/README.md) -- remembered across
    // sessions so the user only has to type it once.
    public string RelayServerUrl { get; set; } = "";

    // Optional shared secret some relays require to connect -- remembered the same way as
    // RelayServerUrl. Only ever sent to the relay URL above; never logged.
    public string RelayAccessToken { get; set; } = "";

    // Stable per-install multiplayer identity. Reused across Host/Join calls
    // (instead of a fresh Guid each time) so that if a peer's connection drops
    // and they rejoin the same session, the host's still-held role claim for
    // their old identity matches their new connection instead of orphaning it.
    public Guid LocalPeerId { get; set; } = Guid.NewGuid();

    // Firewall opcode config — updated automatically by OpcodeUpdater on game version change.
    public uint[] ZoneDownOpcodes { get; set; } = [];
    public string ZoneFirewallGameVersion { get; set; } = "";

    // Safe mode (incoming packet firewall):
    //   true  — only ZoneDownOpcodes pass; cuts you off from server traffic
    //           (no party join/leave updates, no ready checks, no duty pops).
    //   false — all incoming packets pass to the engine. You'll see popups
    //           and party updates, but it's easier to break the sim zone.
    // The send-side firewall stays on either way: nothing the client does in
    // the sim zone leaks back to the server.
    public bool SafeMode { get; set; } = true;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
