using System;
using System.Linq;
using HarmonyLib;
using SilicaAdminMod;

namespace SiSquadHud
{
    // Server-triggered pushes. The client never asks for these.
    internal static class Events
    {
        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var hold = AccessTools.Method(typeof(ChatVotes), nameof(ChatVotes.HoldVote));
            if (hold == null) throw new InvalidOperationException("ChatVotes.HoldVote not found.");
            harmony.Patch(hold, postfix: new HarmonyMethod(typeof(Events), nameof(VoteStarted)));
        }
        // A vote opened (map vote, etc.): push question, options and duration so the client can render vote buttons.
        private static void VoteStarted(ChatVoteBallot __0)
        {
            if (__0 == null || !ChatVotes.IsVoteInProgress()) return;
            int duration = 30;
            try { duration = SiAdminMod.Pre_Admin_VoteDuration?.Value ?? 30; } catch { }
            ServerMod.Broadcast(Doc.Event("vote", new
            {
                q = __0.Question,
                opts = __0.Options.Select(o => new { c = o.Command, d = o.Description }).ToArray(),
                dur = duration
            }));
        }
    }
}
