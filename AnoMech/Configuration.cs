using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using AnoMech.Network;

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

    // Optional shared secret some relays require to connect. Bound to the origin it was entered
    // for (RelayTokenOrigin) and only ever sent there; never logged.
    public string RelayAccessToken { get; set; } = "";

    public string RelayTokenOrigin { get; set; } = "";

    public string TokenForRelay(string url)
    {
        try { return RelayTokenOrigin == RelayWire.Origin(url) ? RelayAccessToken : ""; }
        catch (Exception) { return ""; }
    }

    // The peer credential for each room joined, so a player coming back to the same room after
    // a crash or a manual rejoin is the same identity and resumes their seat. Oldest first.
    public List<RoomCredential> RoomCredentials { get; set; } = [];
    private const int MaxRoomCredentials = 16;

    public string RoomSecret(string relayUrl, string sessionCode)
    {
        string origin;
        try { origin = RelayWire.Origin(relayUrl); }
        catch (Exception) { origin = relayUrl.Trim(); }
        var room = $"{origin}|{sessionCode}";
        var existing = RoomCredentials.Find(c => c.Room == room);
        if (existing != null && IsValidSecret(existing.Secret)) return existing.Secret;
        if (existing != null) RoomCredentials.Remove(existing);
        var created = new RoomCredential { Room = room, Secret = RelayWire.NewSecret() };
        RoomCredentials.Add(created);
        if (RoomCredentials.Count > MaxRoomCredentials)
            RoomCredentials.RemoveRange(0, RoomCredentials.Count - MaxRoomCredentials);
        Save();
        return created.Secret;
    }

    private static bool IsValidSecret(string secret)
    {
        try { RelayWire.PeerId(secret); return true; }
        catch (Exception) { return false; }
    }

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

[Serializable]
public class RoomCredential
{
    public string Room { get; set; } = "";
    public string Secret { get; set; } = "";
}
