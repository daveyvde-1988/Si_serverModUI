using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MelonLoader;
using Newtonsoft.Json;
using SilicaAdminMod;

namespace SiSquadHud
{
    // The static "mod frame": every chat command the Admin Mod registry knows, grouped by the mod that registered it,
    // filtered by what THIS player may execute. Admin commands never leave the server for players without the power.
    internal static class Frame
    {
        internal sealed class Cmd
        {
            [JsonProperty("t")] public string Text;          // what the client sends, e.g. "/buy", "rtv", "!kick"
            [JsonProperty("l")] public string Label;
            [JsonProperty("d")] public string Desc;
            [JsonProperty("a")] public bool Admin;
            [JsonProperty("p")] public string Power;         // required admin power name, "" for player commands
            [JsonProperty("x")] public List<string> Aliases = new List<string>();
            // Picker chain for the arguments, comma separated: "player", "team" (built on the client), "map", "ban" or
            // "choice" (from Choices). "" = free text. "player,team" asks for a player, then a team, then sends both.
            [JsonProperty("arg")] public string Arg = "";
            [JsonProperty("ch")] public List<string[]> Choices = new List<string[]>();   // [value, label] pairs for map / ban / choice stages
            // Current state of a switch: with Choices it renders as one button per choice, the matching one highlighted,
            // a click sends Text + " " + value. Only mods set it (ModUIData "v").
            [JsonProperty("v", NullValueHandling = NullValueHandling.Ignore)] public string Value;
            // Display order inside the mod: registry commands sort by label (1000); a mod's exported list keeps its own order.
            [JsonProperty("o", DefaultValueHandling = DefaultValueHandling.Ignore)] [System.ComponentModel.DefaultValue(1000)] public int Order = 1000;
            [JsonProperty("ui", NullValueHandling = NullValueHandling.Ignore)] public Newtonsoft.Json.Linq.JObject Ui;
            [JsonProperty("role")] public string Role = "shared";
            [JsonIgnore] public bool RegistryAdmin;
            [JsonIgnore] public MethodInfo Callback;
            internal Cmd Clone() => (Cmd)MemberwiseClone();
        }
        internal sealed class Mod
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("ver")] public string Version;
            [JsonProperty("cat")] public string Category;
            [JsonProperty("prio")] public int Priority;
            [JsonProperty("nav")] public string Nav;         // prefix for numbered menu options, e.g. "/" -> "/1"
            [JsonProperty("hash")] public string Hash;
            [JsonProperty("cmds")] public List<Cmd> Cmds = new List<Cmd>();
            // Optional structured data the mod exposes through `public static string ModUIData()` (e.g. the /buy catalog).
            [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public Newtonsoft.Json.Linq.JRaw Data;
            internal Mod Clone() { var m = (Mod)MemberwiseClone(); m.Cmds = new List<Cmd>(Cmds); return m; }
        }
        internal sealed class Result { public string Root; public Dictionary<string, Mod> Mods; }

        // Mods may publish a JSON document for the client by exposing `public static string ModUIData()` on any type.
        // The lookup is by assembly name (= mod id); a failing or absent provider leaves the entry without data.
        private static readonly Dictionary<string, MethodInfo> providers = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        // `public static string ModUIPlayerData(ulong steamId)` per mod: per-player values, see Personalize.
        private static readonly Dictionary<string, MethodInfo> playerProviders = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> providerWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The document may carry "commands": [{"t":"/rtsai status","l":"Status","d":"...","a":false,"arg":"","ch":[[value,label]]}]
        // for sub-commands the Admin Mod registry cannot see. They are merged into the mod's command list here, admin ones
        // only for admins, and removed from the data that leaves the server.
        private static Newtonsoft.Json.Linq.JRaw ModData(string id, bool admin, Mod mod)
        {
            var raw = ModData(id);
            if (raw == null) return null;
            try
            {
                var doc = Newtonsoft.Json.Linq.JObject.Parse(raw.ToString());
                if (doc["commands"] is Newtonsoft.Json.Linq.JArray commands)
                {
                    int order = 0;
                    foreach (var entry in commands.OfType<Newtonsoft.Json.Linq.JObject>().Take(512))
                    {
                        order++;
                        List<string[]> choices = null;
                        if (entry["ch"] is Newtonsoft.Json.Linq.JArray ch)
                        {
                            choices = new List<string[]>();
                            foreach (var choice in ch.OfType<Newtonsoft.Json.Linq.JArray>())
                                if (choice.Count >= 2) choices.Add(new[] { (string)choice[0] ?? "", (string)choice[1] ?? "" });
                        }
                        MergeEntry(mod, id, (string)entry["t"] ?? "", entry["a"] != null && (bool)entry["a"], admin,
                            (string)entry["l"], (string)entry["d"], (string)entry["arg"], (string)entry["v"], choices, order);
                        var merged = mod.Cmds.FirstOrDefault(c => c.Text.Equals((string)entry["t"], StringComparison.OrdinalIgnoreCase));
                        if (merged != null) { merged.Ui = entry["ui"] as Newtonsoft.Json.Linq.JObject; merged.Role = (string)entry["role"] ?? "shared"; if (merged.Admin && !merged.RegistryAdmin && entry["p"] != null) merged.Power = (string)entry["p"]; }
                    }
                    doc.Remove("commands");
                }
                return doc.Count == 0 ? null : new Newtonsoft.Json.Linq.JRaw(JsonConvert.SerializeObject(doc, Formatting.None));
            }
            catch (Exception error)
            {
                if (providerWarned.Add(id + "#commands")) MelonLogger.Warning("ModUIData commands of " + id + " unreadable: " + error.GetBaseException().Message);
                return null;
            }
        }
        // One entry from a mod's export or from the menu.json manifest: a new sub-command of the mod, or decoration (label,
        // description, picker, state) of a command the registry already knows. It cannot turn a player command into an admin
        // one or vice versa. Null fields leave the existing value alone. Admin entries exist only in admin frames.
        private static void MergeEntry(Mod mod, string source, string text, bool isAdmin, bool viewerAdmin, string label, string desc, string arg, string value, List<string[]> choices, int order)
        {
            if(string.IsNullOrEmpty(text)||!Protocol.ValidCommandText(text))return;
            string root=text.Split(' ')[0];
            var registryAdmin=(root.Length>1&&(root[0]=='/'||root[0]=='!'||root[0]=='.'))?
                AdminMethods.AdminCommands.FirstOrDefault(a=>a.AdminCommandText.Equals(root.Substring(1),StringComparison.OrdinalIgnoreCase)):null;
            if(registryAdmin!=null)isAdmin=true;
            var cmd=mod.Cmds.FirstOrDefault(c=>c.Text.Equals(text,StringComparison.OrdinalIgnoreCase));
            // Exports may tighten a player-registered command's visibility, never relax admin access.
            if(isAdmin&&!viewerAdmin){if(cmd!=null)mod.Cmds.Remove(cmd);return;}
            if(cmd!=null&&isAdmin&&!cmd.Admin){cmd.Admin=true;cmd.Power=Power.Generic.ToString();}
            if (cmd != null && cmd.Admin != isAdmin)
            {
                if (providerWarned.Add(source + "#" + text)) MelonLogger.Warning(source + ": entry '" + text + "' dropped, the registry has it as a " + (cmd.Admin ? "admin" : "player") + " command; use a distinct sub-command text.");
                return;
            }
            bool fresh = cmd == null;
            if (fresh) cmd = new Cmd { Text = text, Admin = isAdmin, Power = isAdmin ? Power.Generic.ToString() : "", Label = text };
            if(registryAdmin!=null){cmd.RegistryAdmin=true;cmd.Admin=true;cmd.Power=registryAdmin.AdminPower.ToString();}
            cmd.Order = order;
            if (label != null) cmd.Label = label;
            if (desc != null) cmd.Desc = Plain(desc);
            if (arg != null) cmd.Arg = arg;
            if (value != null) cmd.Value = value;
            if (choices != null) { cmd.Choices = choices; if (cmd.Arg.Length == 0 && choices.Count > 0) cmd.Arg = "choice"; }
            if (fresh) mod.Cmds.Add(cmd);
        }
        // menu.json: Commands[text].Subs describe sub-commands of mods without an export (databomb's), hand-written from
        // their documentation. Applied after the registry so every sub attaches to its parent's mod.
        private static void ApplyManifest(Dictionary<string, Mod> mods, MenuConfig cfg, bool admin)
        {
            foreach (var mod in mods.Values.ToArray())
                foreach (var parent in mod.Cmds.ToArray())
                {
                    if (!cfg.Commands.TryGetValue(parent.Text, out var cc) || cc?.Subs == null) continue;
                    int order = 0;
                    foreach (var sub in cc.Subs)
                    {
                        if (sub == null || string.IsNullOrEmpty(sub.Text)) continue;
                        MergeEntry(mod, "menu.json", sub.Text, sub.Admin, admin, sub.Label, sub.Description, sub.Arg, null, sub.Choices, ++order);
                    }
                }
        }
        private static Newtonsoft.Json.Linq.JRaw ModData(string id)
        {
            if (!providers.TryGetValue(id, out var method))
            {
                method = null;
                try
                {
                    foreach (var melon in MelonMod.RegisteredMelons)
                    {
                        var assembly = melon.MelonAssembly?.Assembly;
                        if (assembly == null || assembly.GetName().Name != id) continue;
                        foreach (var type in assembly.GetTypes())
                        {
                            if (!playerProviders.ContainsKey(id))
                            {
                                var pm = type.GetMethod("ModUIPlayerData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ulong) }, null);
                                if (pm != null && pm.ReturnType == typeof(string)) playerProviders[id] = pm;
                            }
                            if (method != null) continue;
                            var m = type.GetMethod("ModUIData", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                                ?? type.GetMethod("SquadHudData", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);   // pre-rename hook name
                            if (m != null && m.ReturnType == typeof(string)) method = m;
                        }
                        break;
                    }
                }
                catch (Exception error) { if (providerWarned.Add(id)) MelonLogger.Warning("ModUIData lookup failed for " + id + ": " + error.GetBaseException().Message); }
                providers[id] = method;
            }
            if (method == null) return null;
            try
            {
                string json = method.Invoke(null, null) as string;
                if (string.IsNullOrEmpty(json) || json.Length > 512 * 1024) return null;
                // the server's Newtonsoft lacks JToken.ToString(Formatting); JsonConvert is the safe compact serializer
                return new Newtonsoft.Json.Linq.JRaw(JsonConvert.SerializeObject(Newtonsoft.Json.Linq.JToken.Parse(json), Formatting.None));
            }
            catch (Exception error)
            {
                if (providerWarned.Add(id)) MelonLogger.Warning("ModUIData of " + id + " failed: " + error.GetBaseException().Message);
                return null;
            }
        }

        private static readonly Regex NavCommand = new Regex(@"^(?:\d{1,4}|back|exit|close|0)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, Result> cache = new Dictionary<string, Result>();
        private static float cacheAt = -100;

        // After a command ran its mod's state may have changed: the next Build starts fresh so the switches update at once.
        internal static void Invalidate() { cache.Clear(); cacheAt = -100; }
        private static string providerSet="";
        internal static void RefreshProviders()
        {
            string current=string.Join("|",MelonMod.RegisteredMelons.Select(m=>m.MelonAssembly?.Assembly?.FullName));
            if(current==providerSet)return;providerSet=current;ResetProviders();
        }
        internal static void ResetProviders() { providers.Clear(); playerProviders.Clear(); providerWarned.Clear(); Invalidate(); }
        internal static Result Build(Player player)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - cacheAt > 2) { cache.Clear(); cacheAt = now; }
            bool admin = player.IsAdmin();
            Power powers = admin ? player.GetAdminPowers() : Power.None;
            string key = admin + ":" + powers;
            if (!cache.TryGetValue(key, out var cached)) { cached = BuildFor(admin, powers); cache[key] = cached; }
            return FilterPlayer(Personalize(cached, player), player);
        }

        // Per-player state. A mod may expose `public static string ModUIPlayerData(ulong steamId)` returning
        // {"v":{"<command text>":"<current value>"}}: those values replace the shared `v` of the named switch and value
        // rows in this player's copy of the frame (Elo's /flex). The shared frame stays cached; only touched mods are
        // cloned and rehashed, so the diff to the client is the one mod.
        private static Result Personalize(Result shared, Player player)
        {
            if (playerProviders.Count == 0) return shared;
            ulong steam;
            try { steam = player.PlayerID.SteamID.m_SteamID; } catch { return shared; }
            Dictionary<string, Mod> mods = null;
            foreach (var kv in playerProviders.ToArray())
            {
                if (!shared.Mods.TryGetValue(kv.Key, out var mod)) continue;
                Newtonsoft.Json.Linq.JObject doc;
                try
                {
                    string json = kv.Value.Invoke(null, new object[] { steam }) as string;
                    if (string.IsNullOrEmpty(json) || json.Length > 128 * 1024) continue;
                    doc = Newtonsoft.Json.Linq.JObject.Parse(json);
                }
                catch (Exception error)
                {
                    if (providerWarned.Add(kv.Key + "#player")) MelonLogger.Warning("ModUIPlayerData of " + kv.Key + " failed: " + error.GetBaseException().Message);
                    continue;
                }
                Mod clone = null;
                if (doc["commands"] is Newtonsoft.Json.Linq.JArray entries)
                {
                    clone = mod.Clone(); clone.Cmds = mod.Cmds.Select(c=>c.Clone()).ToList();
                    foreach (var e in entries.OfType<Newtonsoft.Json.Linq.JObject>().Take(256))
                    {
                        string t = (string)e["t"] ?? "";
                        if (!Protocol.ValidCommandText(t)) continue;
                        var parent = mod.Cmds.FirstOrDefault(c=>t.Equals(c.Text,StringComparison.OrdinalIgnoreCase)||t.StartsWith(c.Text+" ",StringComparison.OrdinalIgnoreCase));
                        if (parent == null) continue;
                        var ch = new List<string[]>();
                        if(e["ch"] is Newtonsoft.Json.Linq.JArray choices) foreach(var a in choices.OfType<Newtonsoft.Json.Linq.JArray>().Take(256)) if(a.Count>=2) ch.Add(new[]{(string)a[0],(string)a[1]});
                        MergeEntry(clone,kv.Key,t,parent.Admin||(bool?)e["a"]==true,player.IsAdmin(),(string)e["l"],(string)e["d"],(string)e["arg"],(string)e["v"],ch,(int?)e["o"]??0);
                        var merged=clone.Cmds.FirstOrDefault(c=>c.Text.Equals(t,StringComparison.OrdinalIgnoreCase));
                        if(merged!=null){merged.Ui=e["ui"] as Newtonsoft.Json.Linq.JObject;merged.Role=(string)e["role"]??parent.Role;merged.Power=parent.Admin?parent.Power:((string)e["p"]??(merged.Admin?Power.Generic.ToString():""));}
                    }
                }
                var values = doc["v"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject();
                for (int i = 0; i < mod.Cmds.Count; i++)
                {
                    var c = mod.Cmds[i];
                    var prop = values.Properties().FirstOrDefault(p => p.Name.Equals(c.Text, StringComparison.OrdinalIgnoreCase));
                    if (prop == null || prop.Value.Type == Newtonsoft.Json.Linq.JTokenType.Null) continue;
                    string v = prop.Value.ToString();
                    if (v == c.Value) continue;
                    if (clone == null) clone = mod.Clone();
                    var pc = c.Clone(); pc.Value = v; clone.Cmds[i] = pc;
                }
                if (clone == null) continue;
                clone.Hash = ""; clone.Hash = Protocol.ShortHash(JsonConvert.SerializeObject(clone, Formatting.None));
                if (mods == null) mods = new Dictionary<string, Mod>(shared.Mods, StringComparer.OrdinalIgnoreCase);
                mods[clone.Id] = clone;
            }
            if (mods == null) return shared;
            string root = Protocol.ShortHash(string.Join("\n", mods.Values.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id + ":" + m.Hash)));
            return new Result { Root = root, Mods = mods };
        }

        private static Result FilterPlayer(Result source, Player player)
        {
            bool admin = player.IsAdmin(); Power powers = admin ? player.GetAdminPowers() : Power.None;
            var mods = new Dictionary<string, Mod>();
            foreach(var original in source.Mods.Values)
            {
                var mod = original.Clone();
                mod.Cmds = original.Cmds.Select(c => c.Clone()).Where(c =>
                    (!c.Admin || admin && Enum.TryParse(c.Power, true, out Power required) && AdminMethods.PowerInPowers(required, powers)) &&
                    (c.Role != "commander" || player.IsCommander || admin)).ToList();
                if (!admin) ScrubIdentities(mod);
                mod.Hash = ""; mod.Hash = Protocol.ShortHash(JsonConvert.SerializeObject(mod, Formatting.None)); mods[mod.Id] = mod;
            }
            return new Result {Mods = mods, Root = Protocol.ShortHash(string.Join("\n", mods.Values.OrderBy(m=>m.Id,StringComparer.Ordinal).Select(m=>m.Id+":"+m.Hash)))};
        }

        internal static Result BuildPublic() => BuildFor(false, Power.None);
        private static Result BuildFor(bool admin, Power powers)
        {
            var cfg = MenuConfig.Current;
            var mods = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            var byCallback = new Dictionary<MethodInfo, Cmd>();
            try
            {
                foreach (var c in PlayerMethods.PlayerCommands.ToArray()) Add(mods, byCallback, cfg, c.PlayerCommandCallback, "/" + c.CommandName, false, Power.None, "", admin);
                foreach (var c in PlayerMethods.PlayerPhrases.ToArray()) Add(mods, byCallback, cfg, c.PlayerCommandCallback, c.CommandName, false, Power.None, "", admin);
                if (admin)
                    foreach (var a in AdminMethods.AdminCommands.ToArray())
                        if (AdminMethods.PowerInPowers(a.AdminPower, powers))
                            Add(mods, byCallback, cfg, a.AdminCallback, "!" + a.AdminCommandText, true, a.AdminPower, a.AdminCommandDescription, admin);
            }
            catch (Exception error) { MelonLogger.Warning("Frame build incomplete: " + error.GetBaseException().Message); }

            // Include installed server mods with no command export.
            foreach(var melon in MelonMod.RegisteredMelons)
            {
                string id=melon.MelonAssembly?.Assembly?.GetName().Name;
                if(id=="SilicaAdminMod")id="Si_AdminMod";
                if(!Protocol.ValidId(id)||mods.ContainsKey(id))continue;
                var mc=cfg.Mod(id,melon.Info.Name);
                mods[id]=new Mod{Id=id,Name=mc.Name??melon.Info.Name,Version=melon.Info.Version,Category=mc.Category??"player",Priority=mc.Priority,Nav=mc.Nav??"/"};
            }
            // Match control: a fixed admin section that gathers selected admin commands from any mod.
            if (admin)
            {
                var match = new Mod { Id = "_match", Name = cfg.Categories.TryGetValue("match", out var mn) ? mn : "Match control", Version = "", Category = "match", Priority = 0, Nav = "!" };
                foreach (string name in cfg.MatchControl)
                {
                    var found = mods.Values.SelectMany(m => m.Cmds).FirstOrDefault(c => c.Admin && (c.Text.Equals("!" + name, StringComparison.OrdinalIgnoreCase) || c.Aliases.Any(x => x.Equals("!" + name, StringComparison.OrdinalIgnoreCase))));
                    if (found != null && !match.Cmds.Contains(found)) match.Cmds.Add(found);
                }
                if (match.Cmds.Count > 0) mods[match.Id] = match;
            }
            ApplyManifest(mods, cfg, admin);
            foreach (var mod in mods.Values.ToArray())
            {
                mod.Cmds = mod.Cmds.OrderBy(c => c.Admin).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (mod.Id != "_match") mod.Data = ModData(mod.Id, admin, mod);
                mod.Cmds = mod.Cmds.OrderBy(c => c.Admin).ThenBy(c => c.Order).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
                
                if (!admin) ScrubIdentities(mod);
                mod.Hash = ""; mod.Hash = Protocol.ShortHash(JsonConvert.SerializeObject(mod, Formatting.None));
            }
            cfg.SaveIfDirty();
            string root = Protocol.ShortHash(string.Join("\n", mods.Values.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id + ":" + m.Hash)));
            return new Result { Root = root, Mods = mods };
        }

        private static void Add(Dictionary<string, Mod> mods, Dictionary<MethodInfo, Cmd> byCallback, MenuConfig cfg, HelperMethods.CommandCallback callback, string text, bool admin, Power power, string description, bool viewerAdmin)
        {
            if (callback == null || string.IsNullOrEmpty(text)) return;
            string bare = text.TrimStart('/', '!', '.');
            if (bare.Length == 0 || NavCommand.IsMatch(bare)) return;                       // numbered menu navigation / vote options are not catalog entries
            if (cfg.Hidden.Any(h => h.Equals(text, StringComparison.OrdinalIgnoreCase) || h.Equals(bare, StringComparison.OrdinalIgnoreCase))) return;
            if (!admin && cfg.Commands.TryGetValue(text, out var adminOnly) && adminOnly != null && adminOnly.AdminOnly)
            {
                if (!viewerAdmin) return;                                                     // menu.json AdminOnly: a player command whose handler is admin-gated
                admin = true; power = Power.Generic;
            }
            var method = callback.Method;
            if (byCallback.TryGetValue(method, out var existing))
            {
                // same handler registered under another name (rtv / rockthevote, command + phrase): keep one entry, list aliases
                if (!existing.Text.Equals(text, StringComparison.OrdinalIgnoreCase) && !existing.Aliases.Contains(text)) existing.Aliases.Add(text);
                return;
            }
            var assembly = OwningAssembly(callback);
            string id = assembly?.GetName().Name ?? "unknown";
            if (id == "SilicaAdminMod") id = "Si_AdminMod";
            cfg.Commands.TryGetValue(text, out var cc);
            if (!string.IsNullOrEmpty(cc?.Mod)) id = cc.Mod;                                   // sidecar override
            else if (assembly == null || IsFramework(assembly)) id = cfg.Attribute(text, id);   // DynamicMethod shims cannot be attributed by reflection on Mono
            if (!mods.TryGetValue(id, out var mod))
            {
                var info = assembly != null && assembly.GetName().Name == id ? ModInfo(assembly) : ModInfo(id);
                var mc = cfg.Mod(id, info.Key ?? id);
                mod = new Mod { Id = id, Name = mc.Name ?? id, Version = info.Value ?? "", Category = string.IsNullOrEmpty(mc.Category) ? "player" : mc.Category, Priority = mc.Priority, Nav = string.IsNullOrEmpty(mc.Nav) ? "/" : mc.Nav };
                mods[id] = mod;
            }
            var cmd = new Cmd
            {
                Text = text, Admin = admin, RegistryAdmin = admin, Power = admin ? power.ToString() : "", Callback = method,
                Label = cc?.Label ?? text,
                Desc = cc?.Description ?? Plain(description ?? "")
            };
            if (admin && cmd.Desc.Length == 0) cmd.Desc = "Requires admin power " + power + ".";
            cmd.Arg = cfg.ArgType(text) ?? "";
            if (cc?.Choices != null && cc.Choices.Count > 0) { cmd.Choices = cc.Choices; if (cmd.Arg.Length == 0) cmd.Arg = "choice"; }
            if (cmd.Arg.Length == 0 && cmd.Choices.Count == 0) InferArgs(cmd, description ?? "");
            // server-fed stages anywhere in the chain ("map", "map,mode?", "ban"); "mode" reads the chosen map's row
            var stageNames = cmd.Arg.Split(',').Select(s => s.Trim().TrimEnd('?').ToLowerInvariant()).ToList();
            if (stageNames.Contains("map")) cmd.Choices = MapChoices();
            else if (stageNames.Contains("ban")) cmd.Choices = BanChoices();
            mod.Cmds.Add(cmd); byCallback[method] = cmd;
        }
        // Player frames must never carry Steam IDs (ban lists, bounty lists, pins). Admin entries are already dropped
        // for players; this is the safety net for a mod that puts an ID into a player-visible label, choice, description,
        // value or data document: the ID is replaced and the mod is named once in the log.
        private static readonly Regex SteamId64 = new Regex(@"\b7656\d{13}\b", RegexOptions.Compiled);
        private static void ScrubIdentities(Mod mod)
        {
            bool hit = false;
            string Scrub(string text) { if (text == null || !SteamId64.IsMatch(text)) return text; hit = true; return SteamId64.Replace(text, "[id]"); }
            foreach (var cmd in mod.Cmds)
            {
                if(cmd.Ui!=null)cmd.Ui=Newtonsoft.Json.Linq.JObject.Parse(Scrub(cmd.Ui.ToString()));
                cmd.Label = Scrub(cmd.Label); cmd.Desc = Scrub(cmd.Desc); cmd.Value = Scrub(cmd.Value);
                if (cmd.Choices.Count > 0 && cmd.Choices.Any(c => c != null && c.Any(x => x != null && SteamId64.IsMatch(x)))) { cmd.Choices = new List<string[]>(); cmd.Arg = cmd.Arg == "choice" ? "" : cmd.Arg; hit = true; }
            }
            if (mod.Data != null)
            {
                string raw = mod.Data.ToString();
                if(SteamId64.IsMatch(raw))
                {
                    var data=Newtonsoft.Json.Linq.JToken.Parse(raw);
                    if(data is Newtonsoft.Json.Linq.JContainer container)
                    foreach(var token in container.Descendants().OfType<Newtonsoft.Json.Linq.JValue>().ToArray())
                        if(SteamId64.IsMatch(token.ToString()))token.Replace(new Newtonsoft.Json.Linq.JValue(SteamId64.Replace(token.ToString(),"[id]")));
                    mod.Data=new Newtonsoft.Json.Linq.JRaw(JsonConvert.SerializeObject(data,Formatting.None));hit=true;
                }
            }
            if (hit && providerWarned.Add(mod.Id + "#steamid")) MelonLogger.Warning("Player frame of " + mod.Id + " carried Steam IDs; they were removed. Put such data on admin entries only.");
        }
        // Mods that are not ours still describe their arguments in the registered description, databomb style:
        // "Usage: !team [optional:<player>] <teamname>", "!spawn <unit> on|off". Placeholders map to picker stages
        // (<player>, <team...>, <map...>), a literal a|b|c group to a choice; the chain stops at the first unknown token.
        private static readonly Regex UsageToken = new Regex(@"<([a-z][a-z0-9 _-]*)>|\b([a-z0-9_-]+(?:\|[a-z0-9_-]+)+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static void InferArgs(Cmd cmd, string description)
        {
            int at = description.IndexOf("usage", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            var stages = new List<string>();
            foreach (Match m in UsageToken.Matches(description.Substring(at)))
            {
                if (m.Groups[1].Success)
                {
                    string name = m.Groups[1].Value.ToLowerInvariant();
                    if (name.Contains("player") || name == "target" || name == "name") stages.Add("player");
                    else if (name.StartsWith("team")) stages.Add("team");
                    else if (name.StartsWith("map")) stages.Add("map");
                    else break;
                }
                else if (stages.Count == 0 && cmd.Choices.Count == 0)
                {
                    foreach (string option in m.Groups[2].Value.Split('|')) cmd.Choices.Add(new[] { option, option.ToUpperInvariant() });
                    stages.Add("choice"); break;   // a choice group ends the chain: the client offers it as the (single) picker
                }
                else break;
            }
            if (stages.Count > 0) cmd.Arg = string.Join(",", stages);
        }
        // The game's own ban list (serversettings.xml); the value is the Steam64 id that !unban accepts.
        private static List<string[]> BanChoices()
        {
            var result = new List<string[]>();
            try
            {
                var bans = NetworkServerSettings.Instance?.BannedPlayers;
                if (bans == null) return result;
                foreach (var ban in bans)
                {
                    if (ban == null) continue;
                    string id = ban.m_ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    result.Add(new[] { id, Plain(ban.m_Name ?? "?") + "  (" + id + ")" });
                }
            }
            catch (Exception error) { MelonLogger.Warning("Ban list unavailable: " + error.GetBaseException().Message); }
            return result.OrderBy(c => c[1], StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Multiplayer maps the game knows; the value is the file name the map commands expect.
        private static List<string[]> MapChoices()
        {
            var result = new List<string[]>();
            try
            {
                var levels = GameDatabase.Database?.AllLevels;
                if (levels == null) return result;
                foreach (var level in levels)
                {
                    if (level == null || !level.Enabled || !level.IsMultiplayer || level.IsDeveloperOnly || string.IsNullOrEmpty(level.FileName)) continue;
                    string label = Plain(level.DisplayName);
                    // third element: the level's enabled game modes (ObjectName, comma separated) for a following "mode" stage
                    string modes = "";
                    try { if (level.GameModes != null) modes = string.Join(",", level.GameModes.Where(g => g != null && g.Enabled && !string.IsNullOrEmpty(g.ObjectName)).Select(g => g.ObjectName)); } catch { }
                    result.Add(new[] { level.FileName, label.Length > 0 && label != level.FileName ? label + "  (" + level.FileName + ")" : level.FileName, modes });
                }
            }
            catch (Exception error) { MelonLogger.Warning("Map list unavailable: " + error.GetBaseException().Message); }
            return result.OrderBy(c => c[1], StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A callback created by reflection (Delegate.CreateDelegate on another delegate's Invoke) reports mscorlib
        // as the declaring assembly; the mod is the delegate's target, unwrapped as deep as needed.
        private static Assembly OwningAssembly(Delegate callback)
        {
            for (int depth = 0; callback != null && depth < 8; depth++)
            {
                var assembly = callback.Method?.DeclaringType?.Assembly;
                if (assembly != null && !IsFramework(assembly)) return assembly;
                // DynamicMethod shims (Unit Balance) carry no declaring type, but their owner module is the mod's.
                Assembly module = null;
                try { module = callback.Method?.Module?.Assembly; } catch { }
                if (module != null && !IsFramework(module)) return module;
                var target = callback.Target;
                if (target is Delegate inner) { callback = inner; continue; }
                if (target != null && !IsFramework(target.GetType().Assembly)) return target.GetType().Assembly;
                return assembly;
            }
            return null;
        }
        private static bool IsFramework(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal);
        }
        private static KeyValuePair<string, string> ModInfo(string assemblyName)
        {
            try
            {
                foreach (var melon in MelonMod.RegisteredMelons)
                    if (melon.MelonAssembly?.Assembly?.GetName().Name == assemblyName) return new KeyValuePair<string, string>(melon.Info.Name, melon.Info.Version);
            }
            catch { }
            return new KeyValuePair<string, string>(null, "");
        }
        private static KeyValuePair<string, string> ModInfo(Assembly assembly)
        {
            try
            {
                foreach (var melon in MelonMod.RegisteredMelons)
                    if (melon.MelonAssembly?.Assembly == assembly) return new KeyValuePair<string, string>(melon.Info.Name, melon.Info.Version);
            }
            catch { }
            return new KeyValuePair<string, string>(null, assembly?.GetName().Version?.ToString());
        }
        private static string Plain(string text) => Regex.Replace(text ?? "", "<[^>]*>", "").Replace('\n', ' ').Trim();

        // Everything the client does not have yet (by per-mod hash), plus ids it should drop.
        internal static string Diff(Result frame, Dictionary<string, string> known, bool same)
        {
            var cfg = MenuConfig.Current;
            if (same) return JsonConvert.SerializeObject(new { k = "frame", v = 2, caps = new[]{"controls","picker-chain","player-commands"}, root = frame.Root, same = true }, Formatting.None);
            var changed = frame.Mods.Values.Where(m => !known.TryGetValue(m.Id, out var h) || h != m.Hash).OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = known.Keys.Where(id => !frame.Mods.ContainsKey(id)).ToList();
            return JsonConvert.SerializeObject(new { k = "frame", v = 2, caps = new[]{"controls","picker-chain","player-commands"}, root = frame.Root, full = known.Count == 0, cats = cfg.Categories, mods = changed, removed }, Formatting.None);
        }
    }
}
