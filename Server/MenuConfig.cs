using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Newtonsoft.Json;

namespace SiSquadHud
{
    // Sidecar the server admin edits: display names, categories, priorities, labels, hidden commands, match-control list.
    // Discovered mods are appended with defaults on every frame build so the file is always complete; user edits are kept.
    internal sealed class MenuConfig
    {
        public sealed class ModConfig { public string Name; public string Category = "player"; public int Priority = 100; public string Nav = "/"; }
        // Mod: force the owning mod id. Arg: picker chain for the arguments ("player", "team", "map", "ban", comma separated for
        // several in a row, e.g. "player,team"; "" = free text).
        // Choices: [value,label] pairs offered as a picker (stage "choice") for the first argument. Subs: sub-commands the registry
        // cannot see ("!spawn list", "!spawn save"), each with its own label, picker and admin flag; written by hand from the mod's
        // documentation for mods that have no ModUIData() export. Same shape as the export entries, minus the state.
        // AdminOnly: a registered PLAYER command whose handler checks the admin power itself (Formation's tuning knobs); shown in the
        // admin frames only, as an admin entry, and never to players.
        public sealed class CommandConfig { public string Label = null; public string Description = null; public string Mod = null; public string Arg = null; public List<string[]> Choices = null; public List<SubConfig> Subs = null; public bool AdminOnly = false; }
        public sealed class SubConfig { public string Text = null; public string Label = null; public string Description = null; public bool Admin = false; public string Arg = null; public List<string[]> Choices = null; }
        private static readonly Dictionary<string, string> SeedArg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "!kick", "player" }, { "!kickban", "player" }, { "!ban", "player" }, { "!mute", "player" }, { "!unmute", "player" }, { "!silence", "player" }, { "!unsilence", "player" },
            { "!afk", "player" }, { "!eject", "player" }, { "!demote", "player" }, { "!cban", "player" }, { "!commanderban", "player" }, { "!cmdrban", "player" }, { "!removecommanderban", "player" },
            { "!uncban", "player" }, { "!commander", "player" }, { "!team", "player,team" }, { "!slay", "player" }, { "!promote", "player" }, { "!transfer", "player" }, { "!map", "map" }, { "!unban", "ban" }
        };
        internal string ArgType(string commandText)
        {
            Commands.TryGetValue(commandText, out var cc);
            SeedArg.TryGetValue(commandText, out var seeded);
            if (cc != null && cc.Arg != null)
            {
                // a seed that grew a stage ("player" -> "player,team") upgrades the recorded value once
                if (seeded == null || cc.Arg == seeded || !seeded.StartsWith(cc.Arg + ",", StringComparison.OrdinalIgnoreCase)) return cc.Arg;
                cc.Arg = seeded; dirty = true; return seeded;
            }
            if (seeded == null) return "";
            if (cc == null) { cc = new CommandConfig(); Commands[commandText] = cc; }
            cc.Arg = seeded; dirty = true; return seeded;
        }

        public bool Debug = false;
        public Dictionary<string, string> Categories = new Dictionary<string, string> { { "player", "Player" }, { "info", "Info" }, { "admin", "Admin" }, { "match", "Match control" } };
        public List<string> MatchControl = new List<string> { "map", "nextmap", "endround", "endgame", "kick", "kickban", "ban", "banid", "unban", "mute", "unmute", "silence", "unsilence", "demote", "commander", "cban", "removecommanderban", "mutiny", "afk", "eject" };
        // Formation registers its admin tuning as player commands and checks power inside the handler; keep them out of the player menu.
        public List<string> Hidden = new List<string> { "/formationsize", "/formationsizeglobal", "/formationsizecommander", "/Formationspeedcap", "/Formationtraveldistance", "/formationTravel", "/formationTolerance", "/FormationFallof", "/startdelayspeedlock", "/startdelaysoftlock", "/startdelaylock", "/startdelay", "/moduitest" };
        public Dictionary<string, ModConfig> Mods = new Dictionary<string, ModConfig>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CommandConfig> Commands = new Dictionary<string, CommandConfig>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, int> SeedPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        { { "Si_Buy", 10 }, { "Si_KingOfTheHill", 11 }, { "Si_Mapcycle", 20 }, { "Si_Leaderboard", 30 }, { "Si_EloMatchmaking", 31 }, { "Si_HeavyTeleporter", 80 }, { "Si_CrabCannon", 81 } };
        private static readonly Dictionary<string, string> SeedName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { { "Si_Buy", "Buy" }, { "Si_KingOfTheHill", "King of the Hill" }, { "Si_Mapcycle", "Map voting" }, { "Si_Leaderboard", "Leaderboard" }, { "Si_EloMatchmaking", "Elo matchmaking" }, { "Si_HeavyTeleporter", "Heavy teleporter" }, { "Si_CrabCannon", "Crab cannon" }, { "Si_AdminMod", "Admin mod" } };

        [JsonIgnore] private bool dirty;
        [JsonIgnore] private static DateTime loadedWrite;
        internal static MenuConfig Current { get; private set; } = new MenuConfig();
        internal static string Path => System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "cfg", "Si_ServerModUI", "menu.json");

        internal static void Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var loaded = JsonConvert.DeserializeObject<MenuConfig>(File.ReadAllText(Path));
                    if (loaded != null) { Current = loaded; loadedWrite = File.GetLastWriteTimeUtc(Path); EnrichDefaults(); return; }
                }
                Current = new MenuConfig { dirty = true }; EnrichDefaults(); Current.SaveIfDirty();
            }
            catch (Exception error) { MelonLogger.Warning("menu.json unreadable, using defaults: " + error.Message); Current = new MenuConfig(); }
        }
        private static void EnrichDefaults()
        {
            try
            {
                using(var stream=typeof(MenuConfig).Assembly.GetManifestResourceStream("Si_ServerModUI.MenuMetadata.json"))
                {
                    if(stream==null)return;
                    using(var reader=new StreamReader(stream))
                    {
                        var defaults=JsonConvert.DeserializeObject<MenuConfig>(reader.ReadToEnd());
                        Current.Commands=Current.Commands??new Dictionary<string,CommandConfig>(StringComparer.OrdinalIgnoreCase);
                        Current.Mods=Current.Mods??new Dictionary<string,ModConfig>(StringComparer.OrdinalIgnoreCase);
                        if(defaults==null)return;
                        foreach(var pair in defaults.Commands)
                        {
                            if(!Current.Commands.TryGetValue(pair.Key,out var existing)||existing==null)
                            {Current.Commands[pair.Key]=pair.Value;Current.dirty=true;continue;}
                            if(existing.Label==null&&pair.Value.Label!=null){existing.Label=pair.Value.Label;Current.dirty=true;}
                            if(existing.Description==null&&pair.Value.Description!=null){existing.Description=pair.Value.Description;Current.dirty=true;}
                            if(existing.Arg==null&&pair.Value.Arg!=null){existing.Arg=pair.Value.Arg;Current.dirty=true;}
                            if(existing.Subs==null&&pair.Value.Subs!=null){existing.Subs=pair.Value.Subs;Current.dirty=true;}
                            if(existing.Choices==null&&pair.Value.Choices!=null){existing.Choices=pair.Value.Choices;Current.dirty=true;}
                        }
                        foreach(var pair in defaults.Mods)if(!Current.Mods.ContainsKey(pair.Key)){Current.Mods[pair.Key]=pair.Value;Current.dirty=true;}
                    }
                }
            }
            catch(Exception error){MelonLogger.Warning("Optional default menu metadata unavailable: "+error.Message);}
        }
        internal static bool ReloadIfChanged()
        {
            try
            {
                if (!File.Exists(Path) || File.GetLastWriteTimeUtc(Path) == loadedWrite) return false;
                Load(); return true;
            }
            catch { return false; }
        }
        // Known commands registered through DynamicMethod shims (no owner visible to reflection); recorded in menu.json so it can be edited.
        private static readonly Dictionary<string, string> SeedOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { { "/stats", "Si_UnitBalance" }, { "!b", "Si_UnitBalance" }, { "!rebalance", "Si_UnitBalance" } };
        internal string Attribute(string commandText, string fallback)
        {
            if (!SeedOwner.TryGetValue(commandText, out var owner)) return fallback;
            if (!Commands.TryGetValue(commandText, out var cc)) { cc = new CommandConfig(); Commands[commandText] = cc; }
            if (string.IsNullOrEmpty(cc.Mod)) { cc.Mod = owner; dirty = true; }
            return cc.Mod;
        }
        internal ModConfig Mod(string id, string defaultName)
        {
            if (Mods.TryGetValue(id, out var mod)) return mod;
            mod = new ModConfig { Name = SeedName.TryGetValue(id, out var seeded) ? seeded : defaultName, Priority = SeedPriority.TryGetValue(id, out int prio) ? prio : 100 };
            Mods[id] = mod; dirty = true; return mod;
        }
        internal void SaveIfDirty()
        {
            if (!dirty) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented));
                loadedWrite = File.GetLastWriteTimeUtc(Path); dirty = false;
            }
            catch (Exception error) { MelonLogger.Warning("menu.json not saved: " + error.Message); }
        }
    }
}
