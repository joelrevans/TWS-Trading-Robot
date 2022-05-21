using System;
using System.Collections.Generic;
using System.Text;
using IBApi;

namespace TWS_BOT
{
    public class TWS_HELPER
    {
        private EClientSocket socket = null;
        private EWrapper wrapper = null;
        public TWS_HELPER(EWrapperImpl wrapper)
        {
            socket = wrapper.ClientSocket;
            this.wrapper = wrapper;
        }
    }
}
