using System;
using System.Collections.Generic;

namespace AC.Host.World
{
    /// <summary>
    /// What happens to the character as a whole: going through a portal, standing at a
    /// vendor, dying - and what the server has said about its chat channels.
    /// </summary>
    public sealed partial class WorldState
    {
        /// <summary>
        /// Raised on entering portal space (true) and on coming out of it (false). Decal told its
        /// plugins the same, as ChangePortalMode, and Virindi Tank's portal conditions ran on it.
        /// </summary>
        public event EventHandler<bool> PortalSpaceChanged;

        /// <summary>Raised when a vendor's window opens, with its id, and when it closes, with 0.</summary>
        public event EventHandler<uint> VendorChanged;

        /// <summary>Raised when the character dies, with the server's message to the one who died.</summary>
        public event EventHandler<string> Died;

        /// <summary>
        /// Raised when the character leaves the world - logged off, sent back to the character
        /// list, booted, or its session gone - with why. Raised while the character is still
        /// described, so whoever keeps something for it can note it; the character and every
        /// object around it are forgotten straight after. Decal told its plugins the same, as Logoff.
        /// </summary>
        public event EventHandler<string> LoggedOff;

        /// <summary>
        /// How far past a vendor's use radius the character may stand before its window is taken
        /// to have closed. ACE closes a vendor when the distance between the edges of the two
        /// bodies passes the use radius; the centres are further apart than the edges by the two
        /// bodies' radii, which this allows for.
        /// </summary>
        public const float VendorReachSlack = 1.0f;

        /// <summary>A vendor's use radius when its description gave none.</summary>
        public const float DefaultVendorUseRadius = 3.0f;

        /// <summary>
        /// The server is moving the character through a portal - a portal used, a recall, a
        /// death. Everything near it is about to be replaced, a vendor's window included.
        /// </summary>
        internal void EnterPortalSpace(ushort teleportSequence)
        {
            Character.TeleportSequence = teleportSequence;
            CloseVendor();

            if (Character.InPortalSpace)
                return;

            Character.InPortalSpace = true;
            PortalSpaceChanged?.Invoke(this, true);
            NotifyCharacterUpdated();
        }

        /// <summary>
        /// The client says it has finished logging in or arriving. After a teleport that is
        /// the end of portal space; at login there was no portal, and nothing is said.
        /// </summary>
        internal void NoteLoginComplete()
        {
            if (!Character.InPortalSpace)
                return;

            Character.InPortalSpace = false;
            PortalSpaceChanged?.Invoke(this, false);
            NotifyCharacterUpdated();
        }

        /// <summary>The server showed a vendor's wares: its window is open, and any other has closed.</summary>
        internal void OpenVendor(uint vendorId)
        {
            if (Character.OpenVendorId == vendorId)
                return;

            CloseVendor();
            Character.OpenVendorId = vendorId;
            VendorChanged?.Invoke(this, vendorId);
        }

        /// <summary>The vendor's window has closed, if one was open.</summary>
        internal void CloseVendor()
        {
            if (Character.OpenVendorId == 0)
                return;

            Character.OpenVendorId = 0;
            VendorChanged?.Invoke(this, 0);
        }

        /// <summary>
        /// Closes the vendor's window once the character is out of its reach, as the game client
        /// does - the server sends nothing when a customer walks off; ACE only checks the same
        /// distance to have the vendor say goodbye. A vendor no longer in view has gone too.
        /// </summary>
        private void CheckVendorReach()
        {
            uint vendorId = Character.OpenVendorId;
            if (vendorId == 0)
                return;

            if (!TryGet(vendorId, out WorldObject vendor))
            {
                CloseVendor();
                return;
            }

            if (!Character.Location.HasValue || !vendor.Location.HasValue)
                return;

            Location me = Character.Location.Value;
            Location it = vendor.Location.Value;
            float reach = (vendor.UseRadius ?? DefaultVendorUseRadius) + VendorReachSlack;
            if (Distance(me, it) > reach)
                CloseVendor();
        }

        /// <summary>
        /// Metres between two places, across landblock edges outdoors. Two places indoors in
        /// different landblocks are simply far apart.
        /// </summary>
        private static float Distance(Location a, Location b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            if (a.Landblock != b.Landblock)
            {
                // Outdoor landblocks are 192 metres square, numbered by x in the high byte
                // and y in the low; indoor cells cannot be compared across landblocks.
                if ((a.LandblockCell & 0xFFFF) >= 0x100 || (b.LandblockCell & 0xFFFF) >= 0x100)
                    return float.MaxValue;

                dx += ((int)(a.Landblock >> 8) - (int)(b.Landblock >> 8)) * 192f;
                dy += ((int)(a.Landblock & 0xFF) - (int)(b.Landblock & 0xFF)) * 192f;
            }

            float dz = a.Z - b.Z;
            return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        /// <summary>
        /// The character has left the world. Says why, then forgets the character, what it could
        /// see and the chat rooms it was in - the server describes them all again at the next
        /// login, which may be another character's. Nothing is said when no character was in the
        /// world: the character list ACE sends at every login comes before one is.
        /// </summary>
        internal void LeaveWorld(string reason)
        {
            bool hadCharacter = Character.Id != 0;
            if (hadCharacter)
                LoggedOff?.Invoke(this, reason ?? string.Empty);

            CloseVendor();
            Character.Forget();
            _turbineChannels.Clear();
            Clear();

            if (hadCharacter)
                NotifyCharacterUpdated();
        }

        /// <summary>
        /// The session is over - the client closed it, or began another - so the server is gone
        /// as well as the character.
        /// </summary>
        internal void EndSession(string reason)
        {
            LeaveWorld(reason);
            ServerName = null;
        }

        /// <summary>The character has died. The message is the server's, as the chat window shows it.</summary>
        internal void NotifyDied(string message)
        {
            NotifyChat(new ChatMessage(ChatKind.Combat, message, null, 0, 0));
            Died?.Invoke(this, message ?? string.Empty);
        }

        // ------------------------------------------------------------------- chat channels

        private readonly Dictionary<TurbineChannel, uint> _turbineChannels = new Dictionary<TurbineChannel, uint>();

        /// <summary>
        /// The server's numbers for the chat rooms the character may speak in - allegiance,
        /// general, trade and the rest - as it gave them at login. A room it gave 0 is one the
        /// character is not in.
        /// </summary>
        public IReadOnlyDictionary<TurbineChannel, uint> TurbineChannels => _turbineChannels;

        internal void SetTurbineChannels(IReadOnlyList<uint> ids)
        {
            _turbineChannels.Clear();
            for (int i = 0; i < ids.Count && i < TurbineChannelOrder.Length; i++)
                _turbineChannels[TurbineChannelOrder[i]] = ids[i];
        }

        /// <summary>The order the server lists the rooms in.</summary>
        internal static readonly TurbineChannel[] TurbineChannelOrder =
        {
            TurbineChannel.Allegiance,
            TurbineChannel.General,
            TurbineChannel.Trade,
            TurbineChannel.Lfg,
            TurbineChannel.Roleplay,
            TurbineChannel.Olthoi,
            TurbineChannel.Society,
            TurbineChannel.SocietyCelestialHand,
            TurbineChannel.SocietyEldrytchWeb,
            TurbineChannel.SocietyRadiantBlood,
        };

        /// <summary>The last player to send the character a tell, whom "/r" answers. Empty until one does.</summary>
        public string LastTellFrom { get; private set; } = string.Empty;

        /// <summary>The last player the character sent a tell, whom "/rt" tells again. Empty until then.</summary>
        public string LastTellTo { get; internal set; } = string.Empty;

        internal void NoteTellFrom(string name)
        {
            if (!string.IsNullOrEmpty(name))
                LastTellFrom = name;
        }
    }

    /// <summary>The chat rooms the server runs itself (Turbine chat), in the order it lists them.</summary>
    public enum TurbineChannel
    {
        Allegiance,
        General,
        Trade,
        Lfg,
        Roleplay,
        Olthoi,
        Society,
        SocietyCelestialHand,
        SocietyEldrytchWeb,
        SocietyRadiantBlood,
    }
}
