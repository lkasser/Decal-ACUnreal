using System;
using System.Collections.ObjectModel;

namespace Decal.Adapter
{
    /// <summary>The name Decal's plugin list showed. Without one, the class name is used.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class FriendlyNameAttribute : Attribute
    {
        public FriendlyNameAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    /// <summary>Asks for every <see cref="BaseEventAttribute"/> method on the plugin to be wired before Startup.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class WireUpBaseEventsAttribute : Attribute
    {
        public WireUpBaseEventsAttribute()
        {
        }
    }

    /// <summary>
    /// Asks for every <see cref="ControlReferenceAttribute"/> field and
    /// <see cref="ControlEventAttribute"/> method on the plugin to be wired to its views before
    /// Startup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class WireUpControlEventsAttribute : Attribute
    {
        public WireUpControlEventsAttribute()
        {
        }
    }

    /// <summary>
    /// Marks a method as a handler for an event: one of the plugin's own base events when
    /// <see cref="FilterName"/> is empty, or an event on the <see cref="CoreManager"/> property
    /// it names - "CharacterFilter", "WorldFilter" - otherwise.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class BaseEventAttribute : Attribute
    {
        public BaseEventAttribute(string eventName)
            : this(eventName, string.Empty)
        {
        }

        public BaseEventAttribute(string eventName, string baseFilter)
        {
            EventName = eventName;
            FilterName = baseFilter ?? string.Empty;
        }

        public string EventName { get; }

        public string FilterName { get; }
    }

    /// <summary>What every view attribute shares: which of the plugin's views it means.</summary>
    public abstract class ViewBaseAttribute : Attribute
    {
        /// <summary>The name a view has when nothing names it, and the one DefaultView returns.</summary>
        internal const string DefaultViewName = "Default";

        private string _viewName = DefaultViewName;

        protected ViewBaseAttribute()
        {
        }

        public string ViewName
        {
            get => _viewName;
            set => _viewName = string.IsNullOrEmpty(value) ? DefaultViewName : value;
        }
    }

    /// <summary>A view to load from an embedded resource before the plugin starts.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ViewAttribute : ViewBaseAttribute
    {
        public ViewAttribute(string resource)
        {
            Resource = resource;
        }

        public string Resource { get; }
    }

    /// <summary>A field to set to the named control's wrapper.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ControlReferenceAttribute : ViewBaseAttribute
    {
        public ControlReferenceAttribute(string control)
        {
            Control = control;
        }

        public string Control { get; }
    }

    /// <summary>An array field to fill with the named controls' wrappers, in order.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ControlReferenceArrayAttribute : ViewBaseAttribute
    {
        public ControlReferenceArrayAttribute(params string[] controls)
        {
            Controls = new Collection<string>(controls ?? Array.Empty<string>());
        }

        public Collection<string> Controls { get; }
    }

    /// <summary>A method to call when the named control raises the named event.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ControlEventAttribute : ViewBaseAttribute
    {
        public ControlEventAttribute(string control, string eventName)
        {
            Control = control;
            EventName = eventName;
        }

        public string Control { get; }

        public string EventName { get; }
    }
}
