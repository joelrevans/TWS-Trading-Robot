using System;
using System.Collections.Generic;
using System.Text;

namespace TWS_BOT.MODELS
{
    public class DIVIDEND_SUMMARY
    {
        public double? PREV_12_MONTH_DIVIDEND_TOTAL { get; set; }
        public double? NEXT_12_MONTH_DIVIDEND_TOTAL { get; set; }
        public DateTime? NEXT_DIVIDEND_DATE { get; set; }
        public double? NEXT_DIVIDEND_AMOUNT { get; set; }
    }
}
