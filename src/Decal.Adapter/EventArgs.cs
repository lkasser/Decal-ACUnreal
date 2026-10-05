using System;
using System.Drawing;

namespace Decal.Adapter
{
    // Decal's event arguments. Constructors are internal, as Decal's were: plugins receive
    // these and never make them, and keeping it that way means a plugin cannot fake an event
    // another plugin will believe.

    /// <summary>Something happened to a view control. <see cref="Id"/> is the control's own.</summary>
    public class ControlEventArgs : EventArgs
    {
        internal ControlEventArgs(int id)
        {
            Id = id;
        }

        public int Id { get; }
    }

    public class CheckBoxChangeEventArgs : ControlEventArgs
    {
        internal CheckBoxChangeEventArgs(int id, bool isChecked)
            : base(id)
        {
            Checked = isChecked;
        }

        public bool Checked { get; }
    }

    /// <summary>A choice, slider or notebook moved to a new index.</summary>
    public class IndexChangeEventArgs : ControlEventArgs
    {
        internal IndexChangeEventArgs(int id, int index)
            : base(id)
        {
            Index = index;
        }

        public int Index { get; }
    }

    public class ListSelectEventArgs : ControlEventArgs
    {
        internal ListSelectEventArgs(int id, int row, int column)
            : base(id)
        {
            Row = row;
            Column = column;
        }

        public int Row { get; }

        public int Column { get; }
    }

    public class TextBoxChangeEventArgs : ControlEventArgs
    {
        internal TextBoxChangeEventArgs(int id, string text)
            : base(id)
        {
            Text = text ?? string.Empty;
        }

        public string Text { get; }
    }

    /// <summary>The player finished with a text box. Success is false when they cancelled out of it.</summary>
    public class TextBoxEndEventArgs : ControlEventArgs
    {
        internal TextBoxEndEventArgs(int id, bool success)
            : base(id)
        {
            Success = success;
        }

        public bool Success { get; }
    }

    /// <summary>
    /// An event a handler may "eat", stopping the game from acting on it - a chat line not
    /// shown, a command not sent.
    /// </summary>
    /// <remarks>
    /// The host watches the game rather than sitting inside it, so most of what these carry
    /// has already happened by the time a plugin sees it, and eating it changes nothing the
    /// player sees. The flag is still kept and read back, so a plugin that eats its own
    /// commands behaves as it always did, and whatever raised the event can act on it where
    /// it can.
    /// </remarks>
    public class EatableEventArgs : EventArgs
    {
        internal EatableEventArgs()
        {
        }

        public bool Eat { get; set; }
    }

    /// <summary>A line of chat, as the chat window would have shown it.</summary>
    public class ChatTextInterceptEventArgs : EatableEventArgs
    {
        internal ChatTextInterceptEventArgs(string text, int color, int target)
        {
            Text = text ?? string.Empty;
            Color = color;
            Target = target;
        }

        public string Text { get; }

        /// <summary>The chat colour index the client drew the line in.</summary>
        public int Color { get; }

        /// <summary>The chat window it went to; 0 for the main one.</summary>
        public int Target { get; }
    }

    /// <summary>The player clicked a name link in chat.</summary>
    public class ChatClickInterceptEventArgs : EatableEventArgs
    {
        internal ChatClickInterceptEventArgs(string text, int id)
        {
            Text = text ?? string.Empty;
            Id = id;
        }

        public string Text { get; }

        public int Id { get; }
    }

    /// <summary>
    /// The player typed something into the chat bar - usually a plugin's own /command. A
    /// plugin that recognises it sets <see cref="EatableEventArgs.Eat"/>, so it is not said
    /// out loud.
    /// </summary>
    public class ChatParserInterceptEventArgs : EatableEventArgs
    {
        internal ChatParserInterceptEventArgs(string text)
        {
            Text = text ?? string.Empty;
        }

        public string Text { get; }
    }

    public class StatusTextInterceptEventArgs : EatableEventArgs
    {
        internal StatusTextInterceptEventArgs(string text)
        {
            Text = text ?? string.Empty;
        }

        public string Text { get; set; }
    }

    public class WindowMessageEventArgs : EatableEventArgs
    {
        internal WindowMessageEventArgs(int hwnd, short msg, int wParam, int lParam)
        {
            Hwnd = hwnd;
            Msg = msg;
            WParam = wParam;
            LParam = lParam;
        }

        public int Hwnd { get; }

        public short Msg { get; }

        public int WParam { get; }

        public int LParam { get; }
    }

    /// <summary>The player selected something in the game.</summary>
    public class ItemSelectedEventArgs : EventArgs
    {
        internal ItemSelectedEventArgs(int itemGuid)
        {
            ItemGuid = itemGuid;
        }

        public int ItemGuid { get; }
    }

    public class ItemDestroyedEventArgs : EventArgs
    {
        internal ItemDestroyedEventArgs(int itemGuid)
        {
            ItemGuid = itemGuid;
        }

        public int ItemGuid { get; }
    }

    /// <summary>A container's contents arrived: the player opened a chest, a corpse, a pack.</summary>
    public class ContainerOpenedEventArgs : EventArgs
    {
        internal ContainerOpenedEventArgs(int itemGuid)
        {
            ItemGuid = itemGuid;
        }

        public int ItemGuid { get; }
    }

    public class RegionChange3DEventArgs : EventArgs
    {
        internal RegionChange3DEventArgs(Rectangle rect)
        {
            Rect = rect;
        }

        public int Left => Rect.Left;

        public int Right => Rect.Right;

        public int Top => Rect.Top;

        public int Bottom => Rect.Bottom;

        public Rectangle Rect { get; }
    }

    public class DirectoryResolveEventArgs : EventArgs
    {
        internal DirectoryResolveEventArgs(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public object Result { get; set; }
    }

    /// <summary>
    /// A network message went by: the server's, for ServerDispatch, or the client's, for
    /// ClientDispatch. Every subscriber is given the same <see cref="Message"/>, read on first use.
    /// </summary>
    public class NetworkMessageEventArgs : EventArgs
    {
        internal NetworkMessageEventArgs(Message message)
        {
            Message = message;
        }

        public Message Message { get; }
    }

    /// <summary>The client has dealt with a message from the server.</summary>
    /// <remarks>
    /// Decal raised this from inside the client once it had processed the message, with
    /// <see cref="Data"/> pointing at the bytes in the client's memory. The bytes are not in
    /// this process's memory, nor in any it can point into, so <see cref="Data"/> is always 0
    /// - which Marshal.Copy refuses rather than reading - and the bytes are
    /// <see cref="Message"/>'s RawData, as they always were too.
    /// </remarks>
    public class MessageProcessedEventArgs : EventArgs
    {
        internal MessageProcessedEventArgs(Message message, int data, int size)
        {
            Message = message;
            Data = data;
            Size = size;
        }

        public Message Message { get; }

        /// <summary>Where the client had the bytes. Nowhere here: always 0.</summary>
        public int Data { get; }

        /// <summary>How many bytes the message is, its type's four included.</summary>
        public int Size { get; }
    }
}
