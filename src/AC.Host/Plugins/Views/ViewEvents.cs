using System;

namespace AC.Host.Plugins.Views
{
    /// <summary>
    /// What every view event carries: the control it happened to.
    /// </summary>
    /// <remarks>
    /// The sender is the control as well, but typed as object. Carrying it here lets one
    /// handler serve a row of buttons and tell them apart by name, which is how Decal's
    /// ControlEventArgs was used and how code ported from it will expect to work.
    /// </remarks>
    public class ViewEventArgs : EventArgs
    {
        public ViewEventArgs(ViewControl control)
        {
            Control = control;
        }

        public ViewControl Control { get; }

        /// <summary>The control's name in the view XML.</summary>
        public string Name => Control?.Name ?? string.Empty;
    }

    /// <summary>A checkbox was ticked or cleared.</summary>
    public sealed class CheckboxChangedEventArgs : ViewEventArgs
    {
        public CheckboxChangedEventArgs(ViewControl control, bool isChecked)
            : base(control)
        {
            Checked = isChecked;
        }

        /// <summary>The new state, already set on the checkbox.</summary>
        public bool Checked { get; }
    }

    /// <summary>The player finished editing a text box.</summary>
    public sealed class EditChangedEventArgs : ViewEventArgs
    {
        public EditChangedEventArgs(ViewControl control, string text)
            : base(control)
        {
            Text = text ?? string.Empty;
        }

        /// <summary>The new text, already set on the edit box.</summary>
        public string Text { get; }
    }

    /// <summary>A different option was chosen from a dropdown.</summary>
    public sealed class ChoiceChangedEventArgs : ViewEventArgs
    {
        public ChoiceChangedEventArgs(ViewControl control, int selected, string text)
            : base(control)
        {
            Selected = selected;
            Text = text ?? string.Empty;
        }

        /// <summary>The index of the option now chosen.</summary>
        public int Selected { get; }

        /// <summary>That option's text, so a handler need not look it up.</summary>
        public string Text { get; }
    }

    /// <summary>A slider was let go at a new position.</summary>
    public sealed class SliderChangedEventArgs : ViewEventArgs
    {
        public SliderChangedEventArgs(ViewControl control, double position)
            : base(control)
        {
            Position = position;
        }

        /// <summary>The new position, already clamped to the slider's range and set on it.</summary>
        public double Position { get; }
    }

    /// <summary>A notebook's tab was clicked.</summary>
    public sealed class PageChangedEventArgs : ViewEventArgs
    {
        public PageChangedEventArgs(ViewControl control, int page, string label)
            : base(control)
        {
            Page = page;
            Label = label ?? string.Empty;
        }

        /// <summary>The index of the page now showing.</summary>
        public int Page { get; }

        public string Label { get; }
    }

    /// <summary>A cell in a list was clicked.</summary>
    public sealed class ListClickedEventArgs : ViewEventArgs
    {
        public ListClickedEventArgs(ViewControl control, int row, int column)
            : base(control)
        {
            Row = row;
            Column = column;
        }

        public int Row { get; }

        public int Column { get; }
    }
}
