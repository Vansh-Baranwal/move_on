using System.Globalization;
using System.Text;

namespace RobotEngine.Sim
{
    /// <summary>Tiny JSON builders so responses have exact, dependency-free shapes.</summary>
    public static class ApiJson
    {
        public static string Obj(params string[] members)
        {
            return "{" + string.Join(",", members) + "}";
        }

        public static string Str(string key, string value)
        {
            return "\"" + key + "\":\"" + Escape(value ?? "") + "\"";
        }

        public static string Bool(string key, bool value)
        {
            return "\"" + key + "\":" + (value ? "true" : "false");
        }

        public static string Num(string key, float value)
        {
            return "\"" + key + "\":" + value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string Int(string key, int value)
        {
            return "\"" + key + "\":" + value.ToString(CultureInfo.InvariantCulture);
        }

        public static string NullNum(string key)
        {
            return "\"" + key + "\":null";
        }

        private static string Escape(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character == '"' || character == '\\')
                {
                    builder.Append('\\').Append(character);
                }
                else if (character < 32)
                {
                    builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
