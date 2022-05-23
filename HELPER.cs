using System;
using System.Collections.Generic;
using System.Text;

namespace TWS_BOT
{
    public static class HELPER
    {
        public static DateTime PARSE_DATE(string datestring)
        {
            var year = datestring.Substring(0, 4);
            var month = datestring.Substring(4, 2);
            var day = datestring.Substring(6, 2);
            return new DateTime(int.Parse(year), int.Parse(month), int.Parse(day));
        }
    }
}
