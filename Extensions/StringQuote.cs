using System;
using System.Text;

namespace PlayService.Extensions
{
    public static class StringQuote
    {
        private const char DefaultQuote = '\"';

        public static String Quote(this String str)
        {
            return Quote(str, DefaultQuote);
        }

        public static String QuoteIfNecessary(this String str)
        {
            if (str.IndexOf(DefaultQuote) >= 0 || str.IndexOf(' ') >= 0)
                return Quote(str, DefaultQuote);
            return str;
        }

        public static String Quote(this String str, Char quote)
        {
            if (str == null)
                return null;

            var builder = new StringBuilder();
            builder.Append(quote);
            builder.Append(str.Replace(new String(quote, 1), new String(quote, 2)));
            builder.Append(quote);
            return builder.ToString();
        }

        public static String Dequote(this String str)
        {
            return Dequote(str, DefaultQuote);
        }
        public static String Dequote(this String str, Char quote)
        {
            if (str == null)
                return null;

            if (str.Length <= 0 || str[0] != quote) {
                return str;
            }

            String result = str.Substring(1, str.Length - 1);
            if (result[result.Length - 1] == quote) {
                result = result.Substring(0, result.Length - 1);
            }
            result = result.Replace(new String(quote, 2), new String(quote, 1));

            return result;
        }
    }
}
