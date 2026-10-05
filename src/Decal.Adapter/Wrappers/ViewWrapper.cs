using System;
using System.Collections.Generic;
using System.Drawing;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;

namespace Decal.Adapter.Wrappers
{
    /// <summary>
    /// A plugin's window, loaded from Decal view XML: its title, where it is, and its controls
    /// by name.
    /// </summary>
    /// <remarks>
    /// The window itself is a <see cref="DecalView"/> the host's overlay draws; this is the face
    /// Decal plugins knew it by. Disposing it closes the window.
    /// </remarks>
    public class ViewWrapper : MarshalByRefObject, IDisposable
    {
        private readonly DecalRuntime _runtime;
        private readonly HostedView _hosted;
        private Point _location;

        internal ViewWrapper(DecalRuntime runtime, HostedView hosted)
        {
            _runtime = runtime;
            _hosted = hosted;
            Controls = new ViewControls(hosted);
        }

        internal HostedView Hosted => _hosted;

        public ViewControls Controls { get; }

        public string Title
        {
            get => _hosted.View.Title;
            set => _hosted.View.Title = value;
        }

        /// <summary>Whether the window is open. Decal opened a view when its plugin's bar button was pressed.</summary>
        public bool Activated
        {
            get => _hosted.Visible;
            set => _hosted.Visible = value;
        }

        /// <summary>
        /// Where the window is and how big. The overlay places windows itself, so the position
        /// is remembered for the plugin to read back but does not move anything; the size is
        /// the view's own.
        /// </summary>
        public Rectangle Position
        {
            get => new Rectangle(_location.X, _location.Y, _hosted.View.Width, _hosted.View.Height);
            set
            {
                _location = value.Location;
                _hosted.View.Width = value.Width;
                _hosted.View.Height = value.Height;
            }
        }

        public int Alpha { get; set; } = 255;

        public bool Transparent { get; set; }

        public void Activate() => Activated = true;

        public void Deactivate() => Activated = false;

        /// <summary>Decal flashed the window's bar button. There is no such flash here.</summary>
        public void Alert()
        {
        }

        /// <summary>
        /// Sets the title bar's icon. An icon from the client's portal file - no library, or a
        /// library of zero - is shown; one from a DLL's resources cannot be, and is ignored.
        /// </summary>
        public void SetIcon(int icon, object iconLibrary)
        {
            if (iconLibrary == null || (iconLibrary is int module && module == 0))
                _hosted.View.IconKey = ViewImages.Portal(unchecked((uint)icon));
            else
                _runtime.NoteUnsupported("ViewWrapper.SetIcon from an icon library");
        }

        /// <summary>Adds controls from a schema at run time. Decal views here are fixed by their XML.</summary>
        public void LoadSchema(string xmlSchema) => _runtime.NoteUnsupported("ViewWrapper.LoadSchema");

        public void Dispose()
        {
            Controls.Dispose();
            _runtime.RemoveView(_hosted);
        }
    }

    /// <summary>A view's controls by name, each wrapped as the kind of control it is.</summary>
    public class ViewControls : MarshalByRefObject, IDisposable
    {
        private readonly HostedView _hosted;
        private readonly Dictionary<string, IControlWrapper> _wrappers = new Dictionary<string, IControlWrapper>(StringComparer.Ordinal);

        internal ViewControls(HostedView hosted)
        {
            _hosted = hosted;
        }

        /// <summary>
        /// The named control's wrapper - the same one every time. Throws for a name the view
        /// does not have, naming it, since a misspelt control is otherwise a null reference much
        /// later and far from the cause.
        /// </summary>
        public IControlWrapper this[string controlName]
        {
            get
            {
                if (controlName != null && _wrappers.TryGetValue(controlName, out IControlWrapper existing))
                    return existing;

                if (!_hosted.View.TryGet(controlName, out ViewControl control))
                    throw new KeyNotFoundException($"The view '{_hosted.View.Title}' has no control named '{controlName}'.");

                IControlWrapper wrapper = ControlWrapping.Wrap(_hosted, control);
                _wrappers[controlName] = wrapper;
                return wrapper;
            }

            set
            {
                if (controlName == null)
                    return;

                if (value == null)
                    _wrappers.Remove(controlName);
                else
                    _wrappers[controlName] = value;
            }
        }

        public void Dispose()
        {
            foreach (IControlWrapper wrapper in _wrappers.Values)
                (wrapper as IDisposable)?.Dispose();

            _wrappers.Clear();
        }
    }

    /// <summary>Makes the right wrapper for each kind of control, as Decal's control registry did.</summary>
    internal static class ControlWrapping
    {
        public static IControlWrapper Wrap(HostedView hosted, ViewControl control)
        {
            IControlWrapper wrapper = control switch
            {
                PushButton button => Bind(new PushButtonWrapper(), hosted, button),
                ImageButton button => Bind(new ButtonWrapper(), hosted, button),
                Checkbox box => Bind(new CheckBoxWrapper(), hosted, box),
                Edit edit => Bind(new TextBoxWrapper(), hosted, edit),
                Choice choice => Bind(new ChoiceWrapper(), hosted, choice),
                Slider slider => Bind(new SliderWrapper(), hosted, slider),
                List list => Bind(new ListWrapper(), hosted, list),
                StaticText text => Bind(new StaticWrapper(), hosted, text),
                Notebook notebook => Bind(new NotebookWrapper(), hosted, notebook),
                Progress progress => Bind(new ProgressWrapper(), hosted, progress),
                _ => Bind(new ControlWrapper(), hosted, control),
            };

            return wrapper;
        }

        private static IControlWrapper Bind<T>(ControlWrapperBase<T> wrapper, HostedView hosted, T control)
            where T : ViewControl
        {
            wrapper.Bind(hosted, control);
            return wrapper;
        }
    }

    /// <summary>What every control wrapper offers, whatever the control.</summary>
    public interface IControlWrapper
    {
        void Initialize(object control);

        object ChildById(int id);

        object ChildByIndex(int index);

        object Underlying { get; }

        int Id { get; }

        int ChildCount { get; }
    }
}
