using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AC.Host.Plugins.Views
{
    /// <summary>
    /// Turns Decal view XML into a <see cref="DecalView"/>.
    /// </summary>
    /// <remarks>
    /// Strict about the XML and lenient about what is in it. Rubbish, or a document that is
    /// not a view, is a <see cref="FormatException"/>; within a view, anything unexpected
    /// is kept as nearly as possible and described in a warning. Decal itself was lenient -
    /// its views were written by hand, tested by eye, and never validated against anything
    /// - so a view that worked there has to be taken as it is here.
    /// </remarks>
    internal sealed class DecalViewParser
    {
        /// <summary>
        /// Virindi View Service's picture box and console, which Decal had no XML for: VVS's own
        /// controls, as a view a plugin builds in code is given them.
        /// </summary>
        internal const string PictureProgId = "virindiviewservice.controls.hudpicturebox";

        internal const string ConsoleProgId = "virindiviewservice.controls.hudconsole";

        private readonly Dictionary<string, ViewControl> _byName = new Dictionary<string, ViewControl>(StringComparer.Ordinal);
        private readonly List<ViewControl> _controls = new List<ViewControl>();
        private readonly List<string> _warnings = new List<string>();

        private DecalViewParser()
        {
        }

        public static DecalView Parse(string xml)
        {
            if (xml == null) throw new ArgumentNullException(nameof(xml));

            XDocument document;
            try
            {
                // No DTD is ever needed for a view, and ignoring one means a stray DOCTYPE
                // can neither fail the parse nor go fetching anything.
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                };

                using StringReader text = new StringReader(xml);
                using XmlReader reader = XmlReader.Create(text, settings);
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                throw new FormatException($"The view XML is not well formed: {ex.Message}", ex);
            }

            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "view")
                throw new FormatException($"The view XML's outermost element is <{root?.Name.LocalName}>, not <view>.");

            return new DecalViewParser().ReadView(root);
        }

        private DecalView ReadView(XElement element)
        {
            List<XElement> controls = element.Elements("control").ToList();
            if (controls.Count == 0)
                throw new FormatException("The view has no <control> in it, so there is nothing to draw.");

            for (int i = 1; i < controls.Count; i++)
                Warn(controls[i], $"The view has more than one outermost control; only the first is shown, and the {Describe(controls[i])} is ignored.");

            ViewControl rootControl = ReadControl(controls[0]);

            return new DecalView(rootControl, _byName, _controls, _warnings)
            {
                Title = Text(element, "title"),
                IconKey = Image(element, "icon", "the view"),
                Width = Int(element, "width", "the view"),
                Height = Int(element, "height", "the view"),
            };
        }

        private ViewControl ReadControl(XElement element)
        {
            string progId = Text(element, "progid");
            string name = Text(element, "name");

            // ProgIDs are COM's, and COM never cared about case.
            ViewControl control = progId.ToLowerInvariant() switch
            {
                "decalcontrols.fixedlayout" => new FixedLayout(progId, name),
                "decalcontrols.notebook" => new Notebook(progId, name),
                "decalcontrols.statictext" => new StaticText(progId, name),
                "decalcontrols.checkbox" => new Checkbox(progId, name),
                "decalcontrols.pushbutton" => new PushButton(progId, name),
                "decalcontrols.button" => new ImageButton(progId, name),
                "decalcontrols.edit" => new Edit(progId, name),
                "decalcontrols.choice" => new Choice(progId, name),
                "decalcontrols.slider" => new Slider(progId, name),
                "decalcontrols.list" => new List(progId, name),
                "decalcontrols.progress" => new Progress(progId, name),
                PictureProgId => new Picture(progId, name),
                ConsoleProgId => new TextConsole(progId, name),
                _ => new UnknownControl(progId, name),
            };

            string where = control.ToString();

            control.Left = Int(element, "left", where);
            control.Top = Int(element, "top", where);
            control.Width = Int(element, "width", where);
            control.Height = Int(element, "height", where);

            // Registered before its children, so Controls is in document order with every
            // parent ahead of what it contains.
            Register(element, control);

            switch (control)
            {
                case FixedLayout layout:
                    foreach (XElement child in element.Elements())
                    {
                        if (child.Name.LocalName == "control")
                            layout.Add(ReadControl(child));
                        else
                            Warn(child, $"A FixedLayout holds only controls; the <{child.Name.LocalName}> in {where} is ignored.");
                    }

                    break;

                case Notebook notebook:
                    ReadPages(element, notebook, where);
                    break;

                case StaticText label:
                    label.Text = Text(element, "text");
                    label.TextColor = Color(element, "textcolor", where);
                    label.FontSize = Int(element, "fontsize", where);
                    label.Bold = Text(element, "fontstyle").Contains("bold", StringComparison.OrdinalIgnoreCase);
                    label.Justify = Justify(Text(element, "justify"));
                    break;

                case Checkbox box:
                    box.Text = Text(element, "text");
                    box.Checked = Bool(element, "checked");
                    break;

                case PushButton button:
                    button.Text = Text(element, "text");
                    break;

                case ImageButton button:
                    // Decal's own XML gives a Button no face - the plugin sets one in code -
                    // but some views name a background, and that is better than nothing.
                    button.ImageKey = element.Attribute("imageportalsrc") != null
                        ? Image(element, "imageportalsrc", where)
                        : Image(element, "background", where);
                    break;

                case Edit edit:
                    edit.Text = Text(element, "text");
                    edit.ImageKey = Image(element, "imageportalsrc", where);
                    break;

                case Choice choice:
                    ReadOptions(element, choice, where);
                    break;

                case Slider slider:
                    slider.Minimum = Number(element, "minimum", 0, where);
                    slider.Maximum = Number(element, "maximum", 100, where);
                    slider.Vertical = Bool(element, "vertical");
                    slider.Position = slider.Minimum;
                    break;

                case List list:
                    ReadColumns(element, list, where);
                    break;

                case Picture picture:
                    picture.ImageKey = Image(element, "image", where);
                    break;

                case Progress progress:
                    // Decal's progress bar wrote "maxvalue"; "maximum" is accepted as well
                    // because it is what every other ranged control calls it.
                    progress.Minimum = Number(element, "minimum", 0, where);
                    progress.Maximum = element.Attribute("maxvalue") != null
                        ? Number(element, "maxvalue", 100, where)
                        : Number(element, "maximum", 100, where);
                    progress.Value = Number(element, "value", progress.Minimum, where);
                    break;

                case UnknownControl:
                    string which = name.Length > 0 ? $"The control '{name}'" : "An unnamed control";
                    Warn(element, progId.Length > 0
                        ? $"{which} is a {progId}, which this host does not draw; it, and anything inside it, is left out."
                        : $"{which} has no progid, so it is not drawn.");
                    break;
            }

            return control;
        }

        private void ReadPages(XElement element, Notebook notebook, string where)
        {
            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName != "page")
                {
                    Warn(child, $"A Notebook holds only pages; the {Describe(child)} in {where} is ignored.");
                    continue;
                }

                string label = Text(child, "label");
                List<XElement> controls = child.Elements("control").ToList();

                // A page with nothing on it is still a tab; an empty layout lets the
                // overlay draw it without a special case for a page with no content.
                ViewControl content = controls.Count > 0
                    ? ReadControl(controls[0])
                    : Registered(child, new FixedLayout("DecalControls.FixedLayout", string.Empty));

                for (int i = 1; i < controls.Count; i++)
                    Warn(controls[i], $"Page '{label}' of {where} has more than one control; only the first is shown, and the {Describe(controls[i])} is ignored.");

                notebook.Add(new NotebookPage(label, content));
            }

            if (element.Attribute("selected") != null)
            {
                int selected = Int(element, "selected", where);
                if (selected >= 0 && selected < notebook.Pages.Count)
                    notebook.ActivePage = selected;
                else
                    Warn(element, $"{where} starts on page {selected}, which it does not have; it starts on the first instead.");
            }
        }

        private void ReadOptions(XElement element, Choice choice, string where)
        {
            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName == "option")
                    choice.Add(Text(child, "text"), Text(child, "data"));
                else
                    Warn(child, $"A Choice holds only options; the <{child.Name.LocalName}> in {where} is ignored.");
            }

            if (element.Attribute("selected") != null)
            {
                int selected = Int(element, "selected", where);
                if (selected >= -1 && selected < choice.Count)
                    choice.Selected = selected;
                else
                    Warn(element, $"{where} starts on option {selected}, which it does not have; nothing is chosen.");
            }
        }

        private void ReadColumns(XElement element, List list, string where)
        {
            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName != "column")
                {
                    Warn(child, $"A List holds only columns; the {Describe(child)} in {where} is ignored.");
                    continue;
                }

                string progId = Text(child, "progid");
                ListColumnKind kind;
                switch (progId.ToLowerInvariant())
                {
                    case "decalcontrols.textcolumn":
                        kind = ListColumnKind.Text;
                        break;
                    case "decalcontrols.checkcolumn":
                        kind = ListColumnKind.Check;
                        break;
                    case "decalcontrols.iconcolumn":
                        kind = ListColumnKind.Icon;
                        break;
                    default:
                        // Text shows whatever the plugin puts in the cell, which is the
                        // least surprising way to be wrong about a column.
                        kind = ListColumnKind.Text;
                        Warn(child, $"Column {list.Columns.Count} of {where} is a '{progId}', which this host does not know; it is drawn as text.");
                        break;
                }

                list.AddColumn(new ListColumn(kind, Int(child, "fixedwidth", where), Text(child, "name")));
            }
        }

        private void Register(XElement element, ViewControl control)
        {
            _controls.Add(control);

            if (control.Name.Length == 0)
                return;

            if (_byName.ContainsKey(control.Name))
            {
                Warn(element, $"More than one control is named '{control.Name}'; lookups and clicks reach only the first.");
                return;
            }

            _byName.Add(control.Name, control);
        }

        private ViewControl Registered(XElement element, ViewControl control)
        {
            Register(element, control);
            return control;
        }

        private void Warn(XObject where, string message)
        {
            // Line numbers because a view is a hand-written file, and the line is where
            // its author will go to fix it.
            _warnings.Add(where is IXmlLineInfo info && info.HasLineInfo()
                ? $"Line {info.LineNumber}: {message}"
                : message);
        }

        private static string Describe(XElement element)
        {
            if (element.Name.LocalName != "control")
                return $"<{element.Name.LocalName}>";

            string name = Text(element, "name");
            string progId = Text(element, "progid");
            return name.Length > 0 ? $"{progId} '{name}'" : $"unnamed {progId}";
        }

        private static string Text(XElement element, string attribute)
            => element.Attribute(attribute)?.Value ?? string.Empty;

        private static bool Bool(XElement element, string attribute)
        {
            string value = Text(element, attribute).Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private int Int(XElement element, string attribute, string where)
        {
            XAttribute found = element.Attribute(attribute);
            if (found == null || found.Value.Trim().Length == 0)
                return 0;

            if (int.TryParse(found.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return value;

            Warn(found, $"{attribute}=\"{found.Value}\" on {where} is not a whole number; 0 is used.");
            return 0;
        }

        private double Number(XElement element, string attribute, double fallback, string where)
        {
            XAttribute found = element.Attribute(attribute);
            if (found == null || found.Value.Trim().Length == 0)
                return fallback;

            if (double.TryParse(found.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                return value;

            Warn(found, $"{attribute}=\"{found.Value}\" on {where} is not a number; {fallback.ToString(CultureInfo.InvariantCulture)} is used.");
            return fallback;
        }

        private long? Color(XElement element, string attribute, string where)
        {
            XAttribute found = element.Attribute(attribute);
            if (found == null || found.Value.Trim().Length == 0)
                return null;

            if (long.TryParse(found.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long colorRef))
                return ViewColor.FromColorRef(colorRef);

            Warn(found, $"{attribute}=\"{found.Value}\" on {where} is not a colour; the theme's is used.");
            return null;
        }

        /// <summary>
        /// A portal image the XML names by the low part of its id, in decimal - 4726 for
        /// 0x06001276 - as an image key.
        /// </summary>
        private string Image(XElement element, string attribute, string where)
        {
            XAttribute found = element.Attribute(attribute);
            if (found == null || found.Value.Trim().Length == 0)
                return string.Empty;

            if (uint.TryParse(found.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
                return ViewImages.Portal(id);

            Warn(found, $"{attribute}=\"{found.Value}\" on {where} is not an image number; no image is used.");
            return string.Empty;
        }

        private static ViewJustify Justify(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "left":
                    return ViewJustify.Left;
                case "center":
                case "centre":
                    return ViewJustify.Center;
                case "right":
                    return ViewJustify.Right;
                default:
                    return ViewJustify.Default;
            }
        }
    }
}
