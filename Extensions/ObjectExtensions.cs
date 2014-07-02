using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

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
                l.Add(string.Format("{0}:{1}", fi.Name, fi.GetValue(o)));
            }

            foreach (PropertyInfo pi in o.GetType().GetProperties()) {
                l.Add(string.Format("{0}:{1}", pi.Name, pi.GetValue(o, null)));
            }

            var sb = new StringBuilder(o.GetType().Name);
            sb.Append(string.Format(": [{0}]", string.Join(",", l.ToArray())));

            return sb.ToString();

        }
    }
}
