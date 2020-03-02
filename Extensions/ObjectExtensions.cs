using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace PlayService.Extensions
{
    public static class ObjectExtensions
    {
        public static string ReflectionToString(this object o)
        {

            if (o == null) {
                return "";
            }

            var l = new List<string>();

            foreach (FieldInfo fi in o.GetType().GetFields()) {
                l.Add($"{fi.Name}:{fi.GetValue(o)}");
            }

            foreach (PropertyInfo pi in o.GetType().GetProperties()) {
                l.Add($"{pi.Name}:{pi.GetValue(o, null)}");
            }

            var sb = new StringBuilder(o.GetType().Name);
            sb.Append($": [{string.Join(",", l.ToArray())}]");

            return sb.ToString();

        }
    }
}
