using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SocketHelper.Common
{
    /// <summary>
    /// 已经连接的客户端
    /// </summary>
    internal class ConnectClient
    {       
        /// <summary>
        /// 套接字
        /// </summary>
        internal Socket socket{get;set;}
        /// <summary>
        /// 接受端SocketAsyncEventArgs对象
        /// </summary>
        internal SocketAsyncEventArgs saeaReceive { get; set; }
        /// <summary>
        /// 每隔10秒扫描次数,用于检查客户端是否存活
        /// </summary>
        internal int keepAlive { get; set; }
        /// <summary>
        /// 附加数据
        /// </summary>
        public object Attached { get; set; }
    }
}
