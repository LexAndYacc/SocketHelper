using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SocketHelper.Common
{
    /// <summary>
    /// 缓存管理类
    /// </summary>
    public class BufferManager
    {
        /// <summary>
        /// 缓冲池控制的总字节数
        /// </summary>
        private int numBytes;
        /// <summary>
        /// 缓冲区管理器维护的底层字节数组
        /// </summary>
        private byte[] buffer;
        /// <summary>
        /// 偏移位
        /// </summary>
        private Stack<int> freeIndexPool;
        /// <summary>
        /// 当前偏移位
        /// </summary>
        private int currentIndex;
        /// <summary>
        /// 缓存大小
        /// </summary>
        private int bufferSize;

        /// <summary>
        /// 初始化缓存
        /// </summary>
        /// <param name="totalBytes">缓存区总大小</param>
        /// <param name="bufferSize">缓存大小</param>
        public BufferManager(int totalBytes, int bufferSize)
        {
            this.numBytes = totalBytes;
            this.currentIndex = 0;
            this.bufferSize = bufferSize;
            this.freeIndexPool = new Stack<int>();
        }

        /// <summary>
        /// 分配缓冲池使用的缓冲区空间
        /// </summary>
        public void InitBuffer()
        {
            //创造一个巨大的缓冲区并将其分开出来给每个SocketAsyncEventArg对象
            this.buffer = new byte[numBytes];
        }

        /// <summary>
        /// 将缓冲池中的缓冲区分配给指定SocketAsyncEventArgs对象
        /// </summary>
        /// <param name="args">如果缓冲区成功设置，则为true，否则为false</param>
        /// <returns></returns>
        public bool SetBuffer(SocketAsyncEventArgs args)
        {
            if (freeIndexPool.Count > 0)
            {
                args.SetBuffer(buffer, freeIndexPool.Pop(), bufferSize);
            }
            else
            {
                if ((numBytes - bufferSize) < currentIndex)
                {
                    return false;
                }
                args.SetBuffer(buffer, currentIndex, bufferSize);
                currentIndex += bufferSize;
            }
            return true;
        }

        /// <summary>
        /// 从SocketAsyncEventArg对象中删除缓冲区。这将缓冲区释放回缓冲池
        /// </summary>
        /// <param name="args">操作对象</param>
        public void FreeBuffer(SocketAsyncEventArgs args)
        {
            freeIndexPool.Push(args.Offset);
            args.SetBuffer(null, 0, 0);
        }

    }
}
