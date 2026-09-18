// Native game types are unavailable in the transport-only test process.
namespace AnoMech.Core { internal static class DiagnosticLog { public static void Info(string s) { } public static void Warn(string s) { } public static void Debug(string s) { } } }
namespace AnoMech.Core.SimObjects { public enum EnemyListMode { Always, OnlyWhenVisible, Never, Manual } public enum TimelineHoldKind { None, Loop, Base } public enum PropBeatMode { ActorControl, PlayAnimation, SetSharedTimelineState } }
namespace AnoMech.Scenarios.Top { public enum OmegaAttack { Staff, Sword } }
namespace AnoMech.Scenarios.Umad.P1TeleTrouncing { public enum TelePortentDirection { North, East, South, West } }
namespace AnoMech.Scenarios.Umad.P2Forsaken { public enum EndAttack { A, B } }
namespace AnoMech.Scenarios.Umad.P3BlackHole { public enum ThunderIIIAssignment { MtInvulnsBoth, OtInvulnsBoth, ShareMtFirst, ShareOtFirst } }
namespace Dalamud.Configuration { public interface IPluginConfiguration { int Version { get; set; } } }
namespace AnoMech { internal static class Plugin { public static readonly ConfigSaver PluginInterface = new(); } internal sealed class ConfigSaver { public void SavePluginConfig(object value) { } } }
