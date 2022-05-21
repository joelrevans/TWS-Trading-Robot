using System;
using System.Collections.Generic;
using System.Text;

namespace TWS_BOT.MODELS
{
    public class OPTION_CHAIN
    {
        public string exchange { get; set; }
        public int underlyingConId { get; set; }
        public string tradingClass { get; set; }
        public string multiplier { get; set; }
        public HashSet<string> expirations { get; set; }
        public HashSet<double> strikes { get; set; }
    }
}
