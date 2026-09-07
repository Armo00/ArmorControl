using System.Reflection;

namespace ArmorOverhaul.ArmorControl.Core
{
    internal static class TrajectoryMemberAccess
    {
        internal static object Read(object value, string name)
        {
            if (value == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = value.GetType();
            var property = type.GetProperty(name, flags);
            return property != null ? property.GetValue(value, null) : type.GetField(name, flags)?.GetValue(value);
        }
    }
}
