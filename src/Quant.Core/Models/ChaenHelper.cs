using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Chaen //Quant.Core.Models
{
    //internal class ChaenHelper
    //{
    //}

    public static class ChaenHelper
    {
        public static string SafeStr(DataRow r, string col)
        {
            try { return r.IsNull(col) ? "" : r[col]?.ToString() ?? ""; }
            catch { return ""; }
        }

        public static int SafeInt(DataRow r, string col)
        {
            try { return r.IsNull(col) ? 0 : Convert.ToInt32(r[col]); }
            catch { return 0; }
        }

        public static double SafeDouble(DataRow r, string col)
        {
            try { return r.IsNull(col) ? 0.0 : Convert.ToDouble(r[col]); }
            catch { return 0.0; }
        }

        public static bool GetBool(IReadOnlyDictionary<string, string> dict, string key)
        {
            return dict.TryGetValue(key, out var s)
                && bool.TryParse(s, out var b) && b;
        }

        public static DateOnly? ParseDateOnly(string? value) => value is not null && DateOnly.TryParse(value, out var d) ? d : null;
        public static string DateToString(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "";    
        public static string BoolToString(bool value) => value ? "true" : "false";
    }
}

