using System;
using System.Reflection;

namespace ArmorOverhaul.ArmorControl.Core
{
    // Match B9's flight-dialog eligibility; unknown or unavailable APIs fail closed.
    internal static class B9SwitchPolicy
    {
        internal static bool CanSwitchFrom(object module)
        {
            try { return IsTrue(module, "switchInFlight") && IsTrue(Read(module, "CurrentSubtype"), "allowSwitchFromInFlight"); }
            catch { return false; }
        }

        internal static bool CanSwitchTo(object subtype)
        {
            try
            {
                if (!IsTrue(subtype, "allowSwitchInFlight")) return false;
                MethodInfo unlocked = subtype.GetType().GetMethod("IsUnlocked", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                return unlocked != null && object.Equals(unlocked.Invoke(subtype, null), true);
            }
            catch { return false; }
        }

        private static bool IsTrue(object target, string name) { return object.Equals(Read(target, name), true); }

        private static object Read(object target, string name)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = target.GetType().GetField(name, flags);
            if (field != null) return field.GetValue(target);
            PropertyInfo property = target.GetType().GetProperty(name, flags);
            return property == null ? null : property.GetValue(target, null);
        }
    }
}
