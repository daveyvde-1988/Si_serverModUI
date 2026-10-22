using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;

namespace SiSquadHud
{
    // Runs one chat command on behalf of a player through the Admin Mod's own dispatch entry point and captures
    // what the handling mod prints to THAT player. No mod-specific code: whatever a chat user can do, the UI can do.
    internal static class Proxy
    {
        private static readonly Regex NavCommand = new Regex(@"^(?:\d{1,4}|back|exit|close)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static Player captureTarget;
        private static List<string> captured;
        private static int capturedChars;
        internal static bool Installed { get; private set; }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var chat = AccessTools.Method(typeof(HelperMethods), "NetworkSendChat");
            var console = AccessTools.Method(typeof(HelperMethods), "NetworkSendConsole");
            if (chat == null || console == null) throw new InvalidOperationException("Admin Mod chat send methods not found; command replies cannot be captured.");
            harmony.Patch(chat, prefix: new HarmonyMethod(typeof(Proxy), nameof(ChatPrefix)));
            harmony.Patch(console, prefix: new HarmonyMethod(typeof(Proxy), nameof(ConsolePrefix)));
            Installed = true;
        }
        private static bool ChatPrefix(Player __0, string[] __2) => !Capture(__0, __2);
        private static bool ConsolePrefix(Player __0, string[] __1) => !Capture(__0, __1);
        private static bool Capture(Player recipient, string[] messages)
        {
            if (captureTarget == null || !ReferenceEquals(recipient, captureTarget)) return false;
            string line = string.Concat(messages ?? Array.Empty<string>());
            if (captured.Count >= 200 || capturedChars >= 128 * 1024) return false; // retain overflow in native chat
            if (line.Length > 8192) return false;
            captured.Add(line); capturedChars += line.Length;
            return true;
        }

        // Only text the chat dispatcher would route to a registered handler is accepted; nothing else reaches a mod.
        internal static string Validate(string text)
        {
            if (!Protocol.ValidCommandText(text)) return "Invalid command text.";
            text = text.Trim();
            char c = text[0];
            if (HelperMethods.IsValidCommandPrefix(c))
            {
                string token = text.Substring(1).Split(' ')[0];
                if (token.Length == 0) return "Empty command.";
                if (NavCommand.IsMatch(token)) return null;
                if (HelperMethods.GetPlayerCommand(text) != null || HelperMethods.GetAdminCommand(text) != null) return null;
                return "Unknown command " + c + token + ".";
            }
            if (HelperMethods.GetPlayerPhrase(text) != null) return null;
            return "Not a registered command.";
        }

        internal static List<string> Execute(Player player, string text, out string error)
        {
            error = null;
            var lines = new List<string>();
            error = Validate(text);
            if (error != null) return lines;
            if (!Installed) { error = "Capture unavailable."; return lines; }
            captureTarget = player; captured = lines; capturedChars = 0;
            try { Event_Chat.FireOnRequestPlayerChatEvent(player, text.Trim(), false); }
            catch (Exception e) { error = e.GetBaseException().Message; MelonLogger.Warning("Command '" + text + "' threw: " + e.GetBaseException().Message); }
            finally { captureTarget = null; captured = null; }
            return lines;
        }
    }
}
