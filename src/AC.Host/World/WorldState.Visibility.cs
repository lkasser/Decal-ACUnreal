using System;
using System.Collections.Generic;
using AC.Dat;

namespace AC.Host.World
{
    /// <summary>
    /// What the client can see from where the character is, and its letting go of what it
    /// can no longer see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server never says that an object has gone out of sight. ACE keeps, for each player,
    /// the objects the client has been told of; when the character moves - or an object does - so
    /// that one is no longer in view, ACE stops counting it as visible and puts it in a queue to be
    /// forgotten 25 seconds later (ObjectMaint's destruction queue, DestructionTime), sending
    /// nothing. That is the client's own rule, which ACE's physics was taken from: the client
    /// keeps an object it cannot see for 25 seconds, so that one stepping out of view and back is
    /// still there, and then lets it go. ACE, having forgotten it as well, creates it afresh if it
    /// comes back into view. Decal plugins saw the client's list, and saw the object go.
    /// </para>
    /// <para>
    /// In view, as ACE decides it (ObjCell.IsVisible and ObjectMaint.GetVisibleObjects):
    /// </para>
    /// <list type="bullet">
    /// <item>Outdoors, everything in the character's landblock and the eight around it - two
    /// landblocks no more than one apart either way, ACE's adjacent landblocks.</item>
    /// <item>From outdoors, an object in a building is in view the same way if its cell can see
    /// outside; a dungeon's cells cannot.</item>
    /// <item>Indoors, the character's own cell and the cells of its landblock that the cell lists
    /// as visible, and, if the cell can see outside, everything within a landblock of it.</item>
    /// <item>The character, what it carries and wears, what is in a container, and what a creature
    /// holds have no place of their own and are never let go of this way; a creature's weapon
    /// goes with the creature, as the client's children go with their parent.</item>
    /// </list>
    /// <para>
    /// A teleport is the same thing: the character is somewhere else, and whatever cannot be seen
    /// from there goes 25 seconds later. Which cells see which comes from the client's cell
    /// archive (<see cref="Cells"/>); without it, indoors is taken to see as far as a building's
    /// room does - a landblock either way - so that nothing still in view is let go of.
    /// </para>
    /// <para>
    /// Where the character is comes from the client's own reports of its position and from the
    /// server's positions for it, whichever came last: after a teleport the server says where the
    /// character is going before the client has arrived.
    /// </para>
    /// </remarks>
    public sealed partial class WorldState
    {
        /// <summary>
        /// How long an object out of view is kept: the client's destruction delay, which ACE
        /// copied as ObjectMaint.DestructionTime.
        /// </summary>
        public static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(25);

        private readonly Dictionary<uint, DateTimeOffset> _outOfView = new Dictionary<uint, DateTimeOffset>();

        /// <summary>
        /// The client's indoor cells, which say what can be seen from inside; null without the
        /// client's cell archive.
        /// </summary>
        public ICellData Cells { get; set; }

        /// <summary>The cell the character was last known to be in, or 0 before anything said.</summary>
        public uint ViewerCell { get; private set; }

        /// <summary>The objects out of the character's view, and when each went out of it.</summary>
        public IReadOnlyDictionary<uint, DateTimeOffset> OutOfView => _outOfView;

        /// <summary>
        /// Whether something in <paramref name="objectCell"/> is in view from
        /// <paramref name="viewerCell"/>, as ACE decides it for the client.
        /// </summary>
        public bool CanSee(uint viewerCell, uint objectCell)
        {
            if (viewerCell == objectCell)
                return true;

            int apart = LandblocksApart(viewerCell, objectCell);

            if (IsIndoors(viewerCell))
            {
                EnvCellInfo room = Cells?.GetEnvCell(viewerCell);
                if (room == null)
                    return apart <= 1;

                if (apart == 0 && room.Sees((ushort)(objectCell & 0xFFFF)))
                    return true;

                return room.SeenOutside && apart <= 1;
            }

            if (IsIndoors(objectCell))
            {
                EnvCellInfo room = Cells?.GetEnvCell(objectCell);
                return apart <= 1 && (room == null || room.SeenOutside);
            }

            return apart <= 1;
        }

        /// <summary>Cells 0x0100 and up are indoors; below are the 64 outdoor cells of a landblock.</summary>
        private static bool IsIndoors(uint cell) => (cell & 0xFFFF) >= 0x0100;

        /// <summary>
        /// How many landblocks apart two cells are, the further of the two ways: landblocks are
        /// numbered by x in the top byte and y in the next (ACE's PhysicsObj.GetBlockDist).
        /// </summary>
        private static int LandblocksApart(uint a, uint b)
        {
            int dx = Math.Abs((int)(a >> 24) - (int)(b >> 24));
            int dy = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
            return Math.Max(dx, dy);
        }

        /// <summary>
        /// Lets go of every object that has been out of view for <see cref="ForgetAfter"/>, as though
        /// the server had deleted it - and of what its creature holds with it. Called on the host's
        /// ticks.
        /// </summary>
        internal void ForgetOutOfView()
        {
            if (_outOfView.Count == 0)
                return;

            DateTimeOffset now = _clock();
            List<uint> due = null;
            foreach (KeyValuePair<uint, DateTimeOffset> entry in _outOfView)
            {
                if (now - entry.Value >= ForgetAfter)
                    (due ??= new List<uint>()).Add(entry.Key);
            }

            if (due == null)
                return;

            foreach (uint id in due)
            {
                // Gone already with its creature, or back in view since.
                if (!_outOfView.Remove(id) || !TryGet(id, out WorldObject obj)
                    || !TryGetPlace(obj, out uint cell) || ViewerCell == 0 || CanSee(ViewerCell, cell))
                    continue;

                List<uint> held = new List<uint>();
                foreach (WorldObject other in _objects.Values)
                {
                    if (other.WielderId == id || other.ParentId == id)
                        held.Add(other.Id);
                }

                Remove(id);
                foreach (uint child in held)
                    Remove(child);
            }
        }

        /// <summary>
        /// Notes where something now is: the character's whereabouts decide what is in view, and
        /// anything else's decide whether it is.
        /// </summary>
        private void NoteWhereabouts(WorldObject obj)
        {
            if (obj.Id != 0 && obj.Id == Character.Id)
            {
                if (obj.Location.HasValue)
                    ViewFrom(obj.Location.Value.LandblockCell);
                return;
            }

            CheckInView(obj);
        }

        /// <summary>The character is in <paramref name="cell"/>: everything is looked at again if that is somewhere new.</summary>
        private void ViewFrom(uint cell)
        {
            if (cell == 0 || cell == ViewerCell)
                return;

            ViewerCell = cell;
            foreach (WorldObject obj in _objects.Values)
                CheckInView(obj);
        }

        /// <summary>Starts an object's 25 seconds when it goes out of view, and stops them when it comes back.</summary>
        private void CheckInView(WorldObject obj)
        {
            if (ViewerCell == 0 || !TryGetPlace(obj, out uint cell) || CanSee(ViewerCell, cell))
            {
                _outOfView.Remove(obj.Id);
                return;
            }

            if (!_outOfView.ContainsKey(obj.Id))
                _outOfView[obj.Id] = _clock();
        }

        /// <summary>
        /// The cell an object stands in, or false for one with no place of its own: the character,
        /// anything in a container or worn or wielded, anything attached to another, and anything
        /// whose position the host was never told.
        /// </summary>
        private bool TryGetPlace(WorldObject obj, out uint cell)
        {
            cell = 0;
            if (obj.Id == Character.Id || obj.ContainerId.HasValue || obj.WielderId.HasValue || obj.ParentId.HasValue || !obj.Location.HasValue)
                return false;

            cell = obj.Location.Value.LandblockCell;
            return true;
        }

        /// <summary>Forgets what was in view, with the character gone.</summary>
        private void ForgetView()
        {
            _outOfView.Clear();
            ViewerCell = 0;
        }
    }
}
