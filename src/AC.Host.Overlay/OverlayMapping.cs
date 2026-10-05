using System.Collections.Generic;
using System.Globalization;
using AC.Host.Plugins;
using AC.Host.Plugins.Views;
using PluginControl = AC.Host.Plugins.OverlayControl;
using ViewList = AC.Host.Plugins.Views.List;

namespace AC.Host.Overlay
{
    /// <summary>
    /// Turns what the host collects - windows, controls, Decal views - into the types that
    /// cross the pipe.
    /// </summary>
    /// <remarks>
    /// The only place the host's vocabulary and the wire's meet, so it converts and does
    /// nothing else. Every string it writes is non-null, because the other end reads each
    /// into a std::string and a JSON null there is a parse failure rather than an empty
    /// value.
    ///
    /// <para>
    /// A view is the plugin's live object, so mapping one reads the plugin's state: call
    /// this on the game thread, as the host does when it publishes on its tick.
    /// </para>
    /// </remarks>
    public static class OverlayMapping
    {
        /// <summary>A plugin's window, with its controls and, if it has one, its view.</summary>
        /// <remarks>
        /// A window with a view goes by the view's title, as Decal's bar labelled a plugin's switch
        /// - "Decal Hotkey System", not the "Decal/dhs" it is routed by; one without, by its owner.
        /// </remarks>
        public static OverlayWindow ToDto(OverlayWindowInfo window)
        {
            if (window == null)
                return null;

            OverlayWindow dto = new OverlayWindow
            {
                Owner = window.Owner ?? string.Empty,
                Title = window.View?.Title ?? string.Empty,
                Enabled = window.Enabled,
                StartsClosed = window.StartsClosed,
                View = ToDto(window.View),
            };

            foreach (PluginControl control in window.Controls)
            {
                if (control != null)
                    dto.Controls.Add(ToDto(control));
            }

            return dto;
        }

        /// <summary>One of the simpler controls a plugin declares through <see cref="IOverlayControls"/>.</summary>
        public static OverlayControl ToDto(PluginControl control)
        {
            if (control == null)
                return null;

            List<string> options = new List<string>(control.Options.Count);
            foreach (string option in control.Options)
                options.Add(option ?? string.Empty);

            return new OverlayControl
            {
                Id = control.Id ?? string.Empty,
                Label = control.Label ?? string.Empty,
                Kind = (int)control.Kind,
                Value = control.Value ?? string.Empty,
                Options = options,
                Min = control.Min,
                Max = control.Max,
                Step = control.Step,
                Tooltip = control.Tooltip ?? string.Empty,
            };
        }

        /// <summary>A Decal view with every control's current state. Null for no view.</summary>
        public static OverlayView ToDto(DecalView view)
        {
            if (view == null)
                return null;

            OverlayView dto = new OverlayView
            {
                Title = view.Title ?? string.Empty,
                Icon = view.IconKey ?? string.Empty,
                Bar = view.Bar == ViewBar.Vvs ? "vvs" : "decal",
                Width = view.Width,
                Height = view.Height,
                Theme = view.Theme ?? string.Empty,
                Ghosted = view.Ghosted,
                ClickThrough = view.ClickThrough,
                Resizeable = view.Resizeable,
                Ghostable = view.Ghostable,
                ClickThroughable = view.ClickThroughable,
                ShowInBar = view.ShowInBar,
                Minimizable = view.Minimizable,
                AlphaChangeable = view.AlphaChangeable,
                OpenRequest = view.OpenRequests,
                ToggleRequest = view.ToggleRequests,
                CloseRequest = view.CloseRequests,
                BarGroup = view.BarGroup ?? string.Empty,
                BarOrder = string.IsNullOrEmpty(view.BarAssembly) ? null : ViewBarOrder.Of(view.BarAssembly),
                OpensFromGrip = view.OpensFromBarGrip,
                Root = ToDto(view.Root),
            };

            foreach (ViewTitleButton button in view.TitleButtons)
            {
                if (button == null || button.Name.Length == 0)
                    continue;
                dto.TitleButtons.Add(new OverlayTitleButton
                {
                    Name = button.Name,
                    Image = button.ImageKey ?? string.Empty,
                    ImageDown = button.PressedImageKey ?? string.Empty,
                    Tooltip = button.Tooltip ?? string.Empty,
                });
            }

            return dto;
        }

        /// <summary>
        /// One control and everything inside it. A control this host does not draw goes
        /// over with an empty type, which the overlay skips.
        /// </summary>
        public static OverlayViewControl ToDto(ViewControl control)
        {
            if (control == null)
                return null;

            OverlayViewControl dto = new OverlayViewControl
            {
                Name = control.Name ?? string.Empty,
                X = control.Left,
                Y = control.Top,
                W = control.Width,
                H = control.Height,
                Enabled = control.Enabled,
                Visible = control.Visible,
            };

            switch (control)
            {
                case FixedLayout layout:
                    dto.Type = ViewControlTypes.Fixed;
                    foreach (ViewControl child in layout.Children)
                        dto.Children.Add(ToDto(child));
                    break;

                case Notebook notebook:
                    dto.Type = ViewControlTypes.Notebook;
                    dto.Selected = notebook.ActivePage;
                    foreach (NotebookPage page in notebook.Pages)
                        dto.Pages.Add(new OverlayViewPage { Label = page.Label ?? string.Empty, Content = ToDto(page.Content) });
                    break;

                case StaticText text:
                    dto.Type = ViewControlTypes.Static;
                    dto.Text = text.Text ?? string.Empty;
                    dto.TextColor = text.TextColor ?? -1;
                    dto.FontSize = text.FontSize;
                    dto.Bold = text.Bold;
                    dto.Justify = Justify(text.Justify);
                    dto.Shadow = text.Shadow;
                    break;

                case Checkbox box:
                    dto.Type = ViewControlTypes.Checkbox;
                    dto.Text = box.Text ?? string.Empty;
                    dto.Checked = box.Checked;
                    break;

                case PushButton button:
                    dto.Type = ViewControlTypes.PushButton;
                    dto.Text = button.Text ?? string.Empty;
                    break;

                case ImageButton button:
                    dto.Type = ViewControlTypes.Button;
                    dto.Image = button.ImageKey ?? string.Empty;
                    break;

                case Edit edit:
                    dto.Type = ViewControlTypes.Edit;
                    dto.Value = edit.Text ?? string.Empty;
                    dto.Image = edit.ImageKey ?? string.Empty;
                    break;

                case Choice choice:
                    dto.Type = ViewControlTypes.Choice;
                    dto.Selected = choice.Selected;
                    foreach (ChoiceOption option in choice.Options)
                        dto.Options.Add(option.Text ?? string.Empty);
                    break;

                case Slider slider:
                    dto.Type = ViewControlTypes.Slider;
                    dto.Min = slider.Minimum;
                    dto.Max = slider.Maximum;
                    dto.Value = Number(slider.Position);
                    dto.Vertical = slider.Vertical;
                    break;

                case ViewList list:
                    dto.Type = ViewControlTypes.List;
                    MapList(list, dto);
                    break;

                case Progress progress:
                    dto.Type = ViewControlTypes.Progress;
                    dto.Min = progress.Minimum;
                    dto.Max = progress.Maximum;
                    dto.Value = Number(progress.Value);
                    break;

                default:
                    // Unknown to this host. Sent rather than dropped so the tree the overlay
                    // sees has the same shape as the XML, which makes a capture readable.
                    dto.Type = string.Empty;
                    break;
            }

            return dto;
        }

        private static void MapList(ViewList list, OverlayViewControl dto)
        {
            foreach (ListColumn column in list.Columns)
            {
                dto.Columns.Add(new OverlayViewColumn
                {
                    Type = column.Kind switch
                    {
                        ListColumnKind.Check => ViewColumnTypes.Check,
                        ListColumnKind.Icon => ViewColumnTypes.Icon,
                        _ => ViewColumnTypes.Text,
                    },
                    Width = column.FixedWidth,
                });
            }

            foreach (ListRow row in list.Rows)
            {
                OverlayViewRow wire = new OverlayViewRow();

                foreach (ListCell cell in row.Cells)
                {
                    wire.Cells.Add(new OverlayViewCell
                    {
                        Text = cell.Text ?? string.Empty,
                        Checked = cell.Checked,
                        Image = cell.ImageKey ?? string.Empty,
                        Color = cell.Color ?? -1,
                    });
                }

                dto.Rows.Add(wire);
            }
        }

        private static string Justify(ViewJustify justify) => justify switch
        {
            ViewJustify.Left => "left",
            ViewJustify.Center => "center",
            ViewJustify.Right => "right",
            _ => string.Empty,
        };

        /// <summary>
        /// The invariant round-trip form, which is the only one the overlay's from_chars
        /// reads: a host under a comma-decimal culture writing "0,5" would put every
        /// slider at its minimum.
        /// </summary>
        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
