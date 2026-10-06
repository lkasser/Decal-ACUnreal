using System;
using System.Collections.Generic;
using AC.Host.Decoding;

namespace AC.Host.Actions
{
    /// <summary>
    /// The motion commands of the client's chat emotes, by the names its ChatPoseTable gives them:
    /// the part of the client's own command table a "*dance*" goes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client looked a pose's command up by name in a table built into its executable. The
    /// numbers are the same as ACE's MotionCommand, which is that table; these are the 74 whose
    /// words the ChatPoseTable carries, every one of them a soul emote to ACE
    /// (ACE.Server.Entity.SoulEmote) - the only motions it accepts in a MoveToState's actions.
    /// Names are matched ignoring case: the table writes some in capitals ("WAVESTATE").
    /// </para>
    /// <para>
    /// The class bits say how each is played: 0x10000000, an action that plays once (a wave);
    /// 0x40000000, a state the character stays in until it moves (sitting, dancing). Every one
    /// has 0x02000000, the chat emote's bit, which the client's motion interpreter refused in a
    /// combat stance.
    /// </para>
    /// </remarks>
    public static class ChatEmoteCommands
    {
        /// <summary>The client's NonCombat stance, the only one a chat emote plays in.</summary>
        public const uint NonCombat = 0x8000003D;

        /// <summary>The standing state, as a forward command.</summary>
        public const uint Ready = 0x41000003;

        /// <summary>What the client said when an emote was asked for in a combat stance (WeenieError 0x42).</summary>
        public const string InCombat = "You can't use chat emotes in combat mode";

        /// <summary>What the client said when one was asked for while not standing still (WeenieError 0x44).</summary>
        public const string NotStanding = "You can't use chat emotes from this position";

        private static readonly Dictionary<string, uint> Commands = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["AFKState"] = 0x4300011B,
            ["AkimboState"] = 0x430000F2,
            ["AtEaseState"] = 0x43000149,
            ["ATOYOT"] = 0x420000F9,
            ["Beckon"] = 0x1300007A,
            ["BeSeeingYou"] = 0x1300007B,
            ["BlowKiss"] = 0x1300007C,
            ["BowDeepState"] = 0x430000EC,
            ["Cheer"] = 0x1300004C,
            ["ClapHands"] = 0x1300007E,
            ["ClapHandsState"] = 0x430000ED,
            ["Cringe"] = 0x13000091,
            ["CrossArmsState"] = 0x430000EE,
            ["Cry"] = 0x1300007F,
            ["CurtseyState"] = 0x4300011A,
            ["DrudgeDance"] = 0x13000151,
            ["DrudgeDanceState"] = 0x43000144,
            ["HaveASeat"] = 0x13000152,
            ["HaveASeatState"] = 0x43000148,
            ["HeartyLaugh"] = 0x13000089,
            ["Helper"] = 0x13000135,
            ["KneelState"] = 0x430000F7,
            ["Knock"] = 0x1300014F,
            ["Laugh"] = 0x13000080,
            ["LeanState"] = 0x430000F6,
            ["MeditateState"] = 0x4300011C,
            ["MimeDrink"] = 0x13000082,
            ["MimeEat"] = 0x13000081,
            ["Mock"] = 0x130000CB,
            ["Nod"] = 0x13000083,
            ["NudgeLeft"] = 0x1300014A,
            ["NudgeRight"] = 0x1300014B,
            ["PleadState"] = 0x430000F8,
            ["PointDown"] = 0x1300014E,
            ["PointDownState"] = 0x43000143,
            ["PointLeft"] = 0x1300014C,
            ["PointLeftState"] = 0x43000140,
            ["PointRight"] = 0x1300014D,
            ["PointRightState"] = 0x43000141,
            ["PointState"] = 0x430000F0,
            ["PossumState"] = 0x43000145,
            ["PrayState"] = 0x430000EB,
            ["ReadState"] = 0x43000146,
            ["SaluteState"] = 0x430000F3,
            ["ScanHorizon"] = 0x13000150,
            ["ScratchHead"] = 0x1300008B,
            ["ScratchHeadState"] = 0x430000F4,
            ["ShakeFist"] = 0x13000079,
            ["ShakeFistState"] = 0x430000EA,
            ["ShakeHead"] = 0x13000085,
            ["Shiver"] = 0x13000094,
            ["Shoo"] = 0x13000095,
            ["Shrug"] = 0x13000086,
            ["SitBackState"] = 0x4300013F,
            ["SitCrossleggedState"] = 0x4300013E,
            ["SitState"] = 0x4300013D,
            ["SlouchState"] = 0x430000FA,
            ["SmackHead"] = 0x1300008C,
            ["SnowAngelState"] = 0x43000118,
            ["Spit"] = 0x13000097,
            ["SurrenderState"] = 0x430000FB,
            ["TalktotheHandState"] = 0x43000142,
            ["TapFootState"] = 0x430000F5,
            ["Teapot"] = 0x130000CC,
            ["ThinkerState"] = 0x43000147,
            ["WarmHands"] = 0x13000119,
            ["Wave"] = 0x13000087,
            ["WaveHigh"] = 0x1300008E,
            ["WaveLow"] = 0x1300008F,
            ["WaveState"] = 0x430000F1,
            ["WindedState"] = 0x430000FD,
            ["WoahState"] = 0x430000FC,
            ["YawnStretch"] = 0x13000090,
            ["YMCA"] = 0x1200009B,
        };

        /// <summary>The motion command a chat emote's command name stands for, or 0 for a name the client did not know.</summary>
        public static uint Find(string name)
            => name != null && Commands.TryGetValue(name, out uint command) ? command : 0;

        /// <summary>
        /// Why the client would not play an emote now, in its own words, or null when it would: in
        /// a combat stance, or not standing still - walking, turning, or held in a state such as
        /// sitting - by what the client last said its body was doing. Nothing known is no refusal.
        /// </summary>
        /// <remarks>
        /// The client's motion interpreter refused a chat emote's motion outside the NonCombat
        /// stance (CMotionInterp::DoMotion, which ACE ported) and while the character was not
        /// standing (WeenieError CantChatEmoteNotStanding). Its words still went out: the client
        /// sent them whatever the motion came to.
        /// </remarks>
        public static string Refusal(ClientMotionState motion)
        {
            if (motion == null)
                return null;

            if ((motion.Flags & MotionFlags.CurrentStyle) != 0 && motion.CurrentStyle != 0 && motion.CurrentStyle != NonCombat)
                return InCombat;

            bool standing = (motion.ForwardCommand == 0 || motion.ForwardCommand == Ready)
                && motion.SidestepCommand == 0 && motion.TurnCommand == 0;
            return standing ? null : NotStanding;
        }
    }
}
