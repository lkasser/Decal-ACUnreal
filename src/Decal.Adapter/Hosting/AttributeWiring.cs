using System;
using System.Collections.Generic;
using System.Reflection;
using Decal.Adapter.Wrappers;

namespace Decal.Adapter.Hosting
{
    /// <summary>
    /// Decal's attribute-driven set-up of a plugin: views loaded from <see cref="ViewAttribute"/>,
    /// base events wired from <see cref="BaseEventAttribute"/>, and control references and
    /// control events from <see cref="ControlReferenceAttribute"/> and
    /// <see cref="ControlEventAttribute"/>.
    /// </summary>
    /// <remarks>
    /// Done before the plugin's Startup, because plugins written this way use their control
    /// references in Startup and never wired anything by hand. A reference or event that
    /// cannot be wired - a misspelt control, a handler of the wrong shape - throws with a
    /// message naming it, so the plugin fails to start with the reason in the log instead of
    /// starting with a button that silently does nothing.
    /// </remarks>
    internal static class AttributeWiring
    {
        public static void LoadViews(PluginBase plugin)
        {
            foreach (ViewAttribute view in plugin.GetType().GetCustomAttributes<ViewAttribute>(true))
                plugin.LoadView(view.ViewName, view.Resource);
        }

        public static void WireBaseEvents(PluginBase plugin, DecalRuntime runtime)
        {
            Type type = plugin.GetType();
            if (!type.IsDefined(typeof(WireUpBaseEventsAttribute), true))
                return;

            foreach (MethodInfo method in MethodsOf(type))
            {
                foreach (BaseEventAttribute attribute in method.GetCustomAttributes<BaseEventAttribute>(true))
                {
                    object source = plugin;
                    EventInfo found = null;

                    if (attribute.FilterName.Length > 0)
                    {
                        // A filter by the name of the CoreManager property holding it.
                        PropertyInfo filter = typeof(CoreManager).GetProperty(attribute.FilterName, BindingFlags.Instance | BindingFlags.Public);
                        source = filter?.GetValue(runtime.Core)
                            ?? throw new InvalidOperationException($"{type.Name}.{method.Name} asks for an event on '{attribute.FilterName}', which Decal does not have.");
                        found = source.GetType().GetEvent(attribute.EventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    else
                    {
                        // The plugin's own events first, then the core's: RenderFrame and the
                        // init-complete events live only on the core, and plugins named them bare.
                        found = type.GetEvent(attribute.EventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (found == null)
                        {
                            source = runtime.Core;
                            found = typeof(CoreManager).GetEvent(attribute.EventName, BindingFlags.Instance | BindingFlags.Public);
                        }
                    }

                    if (found == null)
                    {
                        throw new InvalidOperationException(
                            $"{type.Name}.{method.Name} handles '{attribute.EventName}'{(attribute.FilterName.Length > 0 ? " on " + attribute.FilterName : string.Empty)}, which does not exist.");
                    }

                    AddHandler(found, source, plugin, method);
                }
            }
        }

        public static void WireControls(PluginBase plugin)
        {
            Type type = plugin.GetType();
            if (!type.IsDefined(typeof(WireUpControlEventsAttribute), true))
                return;

            foreach (FieldInfo field in FieldsOf(type))
            {
                ControlReferenceAttribute single = field.GetCustomAttribute<ControlReferenceAttribute>(true);
                if (single != null)
                {
                    IControlWrapper control = Control(plugin, single.ViewName, single.Control, $"{type.Name}.{field.Name}");
                    if (!field.FieldType.IsInstanceOfType(control))
                        throw new InvalidOperationException($"{type.Name}.{field.Name} is a {field.FieldType.Name}, but '{single.Control}' is a {control.GetType().Name}.");

                    field.SetValue(plugin, control);
                    continue;
                }

                ControlReferenceArrayAttribute many = field.GetCustomAttribute<ControlReferenceArrayAttribute>(true);
                if (many != null)
                {
                    Type element = field.FieldType.GetElementType()
                        ?? throw new InvalidOperationException($"{type.Name}.{field.Name} holds several controls, so it must be an array.");

                    Array controls = Array.CreateInstance(element, many.Controls.Count);
                    for (int i = 0; i < many.Controls.Count; i++)
                        controls.SetValue(Control(plugin, many.ViewName, many.Controls[i], $"{type.Name}.{field.Name}"), i);

                    field.SetValue(plugin, controls);
                }
            }

            foreach (MethodInfo method in MethodsOf(type))
            {
                foreach (ControlEventAttribute attribute in method.GetCustomAttributes<ControlEventAttribute>(true))
                {
                    IControlWrapper control = Control(plugin, attribute.ViewName, attribute.Control, $"{type.Name}.{method.Name}");
                    EventInfo found = control.GetType().GetEvent(attribute.EventName, BindingFlags.Instance | BindingFlags.Public)
                        ?? throw new InvalidOperationException($"{type.Name}.{method.Name} handles '{attribute.EventName}' on '{attribute.Control}', a {control.GetType().Name}, which has no such event.");

                    AddHandler(found, control, plugin, method);
                }
            }
        }

        private static IControlWrapper Control(PluginBase plugin, string viewName, string controlName, string wantedBy)
        {
            ViewWrapper view = plugin.GetView(viewName)
                ?? throw new InvalidOperationException($"{wantedBy} refers to the view '{viewName}', which the plugin has not loaded.");

            return view.Controls[controlName]
                ?? throw new InvalidOperationException($"{wantedBy} refers to '{controlName}', which the view '{viewName}' does not have.");
        }

        private static void AddHandler(EventInfo target, object source, object handlerOwner, MethodInfo method)
        {
            Delegate handler;
            try
            {
                handler = Delegate.CreateDelegate(target.EventHandlerType, method.IsStatic ? null : handlerOwner, method);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} does not have the shape of a handler for '{target.Name}' ({target.EventHandlerType.Name}).", ex);
            }

            // Base events are protected, and EventInfo.AddEventHandler insists on a public add.
            MethodInfo add = target.GetAddMethod(nonPublic: true);
            add.Invoke(source, new object[] { handler });
        }

        /// <summary>Every method the class and its bases declare, up to the Decal base class.</summary>
        private static IEnumerable<MethodInfo> MethodsOf(Type type)
        {
            for (Type t = type; t != null && t != typeof(PluginBase); t = t.BaseType)
            {
                foreach (MethodInfo method in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return method;
            }
        }

        private static IEnumerable<FieldInfo> FieldsOf(Type type)
        {
            for (Type t = type; t != null && t != typeof(PluginBase); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return field;
            }
        }
    }
}
