﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using System.Collections.Concurrent;
using SocketHelper.Common;

namespace SocketHelper.Server
{
    /// <summary>
    /// tcp Socket监听基库
    /// </summary>
    public class TcpServer
    {
        /// <summary>
        /// 连接标示 自增长
        /// </summary>
        private int connectID;
        /// <summary>
        /// 同时处理的最大连接数
        /// </summary>
        private int numConnections;
        /// <summary>
        /// 用于每个套接字I/O操作的缓冲区大小
        /// </summary>
        private int receiveBufferSize;
        /// <summary>
        /// 所有套接字接收操作的一个可重用的大型缓冲区集合。
        /// </summary>
        private BufferManager bufferManager;
        /// <summary>
        /// 用于监听传入连接请求的套接字
        /// </summary>
        private Socket listenSocket;
        /// <summary>
        /// 接受端SocketAsyncEventArgs对象重用池，接受套接字操作
        /// </summary>
        private SocketAsyncEventArgsPool receivePool;
        /// <summary>
        /// 发送端SocketAsyncEventArgs对象重用池，发送套接字操作
        /// </summary>
        private SocketAsyncEventArgsPool sendPool;
        /// <summary>
        /// 超时，如果超时，服务端断开连接，客户端需要重连操作
        /// </summary>
        private int overtime;
        /// <summary>
        /// 超时检查间隔时间(秒)
        /// </summary>
        private int overtimecheck = 1;
        /// <summary>
        /// 能接到最多客户端个数的原子操作
        /// </summary>
        private Semaphore maxNumberAcceptedClients;
        /// <summary>
        /// 已经连接的对象池
        /// </summary>
        internal ConcurrentDictionary<int, ConnectClient> connectClient;
        /// <summary>
        /// 客户端列表
        /// </summary>
        internal ConcurrentDictionary<int, string> clientList;
        /// <summary>
        /// 发送线程数
        /// </summary>
        private int sendthread = 10;
        /// <summary>
        /// 发送线程
        /// </summary>
        private List<Thread> sendThreads = new List<Thread>();
        /// <summary>
        /// 心跳线程
        /// </summary>
        private Thread heartBeatThread = null;
        /// <summary>
        /// 需要发送的数据
        /// </summary>
        private ConcurrentQueue<SendingQueue>[] sendQueues;
        /// <summary>
        /// 锁
        /// </summary>
        private Mutex mutex = new Mutex();
        /// <summary>
        /// 连接成功事件 item1:connectID
        /// </summary>
        internal event Action<int> OnAccept;
        /// <summary>
        /// 接收通知事件 item1:connectID,item2:数据,item3:偏移位,item4:长度
        /// </summary>
        internal event Action<int, byte[], int, int> OnReceive;
        /// <summary>
        /// 已发送通知事件 item1:connectID,item2:长度
        /// </summary>
        internal event Action<int, int> OnSend;
        /// <summary>
        /// 断开连接通知事件 item1:connectID,
        /// </summary>
        internal event Action<int> OnClose;

        /// <summary>
        /// 设置基本配置
        /// </summary>   
        /// <param name="numConnections">同时处理的最大连接数</param>
        /// <param name="receiveBufferSize">用于每个套接字I/O操作的缓冲区大小(接收端)</param>
        /// <param name="overTime">超时时长,单位秒.(每秒检查一次)，当值为0时，不设置超时</param>
        public TcpServer(int numConnections, int receiveBufferSize, int overTime)
        {
            overtime = overTime;
            this.numConnections = numConnections;
            this.receiveBufferSize = receiveBufferSize;
            this.bufferManager = new BufferManager(receiveBufferSize * numConnections, receiveBufferSize);
            this.receivePool = new SocketAsyncEventArgsPool(numConnections);
            this.sendPool = new SocketAsyncEventArgsPool(numConnections);
            this.maxNumberAcceptedClients = new Semaphore(numConnections, numConnections);
            Init();
        }

        /// <summary>
        /// 初始化服务器通过预先分配的可重复使用的缓冲区和上下文对象。这些对象不需要预先分配或重用，但这样做是为了说明API如何可以易于用于创建可重用对象以提高服务器性能。
        /// </summary>
        private void Init()
        {
            connectClient = new ConcurrentDictionary<int, ConnectClient>();
            clientList = new ConcurrentDictionary<int, string>();
            sendQueues = new ConcurrentQueue<SendingQueue>[sendthread];
            for (int i = 0; i < sendthread; i++)
            {
                sendQueues[i] = new ConcurrentQueue<SendingQueue>();
            }
            //分配一个大字节缓冲区，所有I/O操作都使用一个。这个侍卫对内存碎片
            this.bufferManager.InitBuffer();
            //预分配的接受对象池socketasynceventargs，并分配缓存
            SocketAsyncEventArgs saeaReceive;
            //分配的发送对象池socketasynceventargs，但是不分配缓存
            SocketAsyncEventArgs saea_send;
            for (int i = 0; i < numConnections; i++)
            {
                //预先接受端分配一组可重用的消息
                saeaReceive = new SocketAsyncEventArgs();
                saeaReceive.Completed += new EventHandler<SocketAsyncEventArgs>(IOCompleted);
                //分配缓冲池中的字节缓冲区的socketasynceventarg对象
                this.bufferManager.SetBuffer(saeaReceive);
                this.receivePool.Push(saeaReceive);
                //预先发送端分配一组可重用的消息
                saea_send = new SocketAsyncEventArgs();
                saea_send.Completed += new EventHandler<SocketAsyncEventArgs>(IOCompleted);
                this.sendPool.Push(saea_send);
            }
        }

        /// <summary>
        /// 启动tcp服务侦听
        /// </summary>       
        /// <param name="port">监听端口</param>
        internal void Start(int port)
        {
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, port);
            //创建listens是传入的套接字。
            listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.NoDelay = true;
            //绑定端口
            listenSocket.Bind(localEndPoint);
            //挂起的连接队列的最大长度。
            listenSocket.Listen(1000);
            //在监听套接字上接受
            StartAccept(null);
            //发送线程
            for (int i = 0; i < sendthread; i++)
            {
                Thread thread = new Thread(StartSend);
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Start(i);

                sendThreads.Add(thread);
            }
            //超时机制
            if (overtime > 0)
            {
                heartBeatThread = new Thread(new ThreadStart(() =>
                {
                    Heartbeat();
                }));
                heartBeatThread.IsBackground = true;
                heartBeatThread.Priority = ThreadPriority.Lowest;
                heartBeatThread.Start();
            }
        }
        /// <summary>
        /// 停止监听
        /// </summary>
        internal void Stop()
        {
            if (heartBeatThread == null) return;

            listenSocket.Close();
            listenSocket.Dispose();
            heartBeatThread.Abort();
            heartBeatThread = null;
            foreach (var thread in sendThreads)
            {
                thread.Abort();
            }
            sendThreads.Clear();
        }

        /// <summary>
        /// 超时机制
        /// </summary>
        private void Heartbeat()
        {
            //计算超时次数 ，超过count就当客户端断开连接。服务端清除该连接资源
            int count = overtime / overtimecheck;
            while (true)
            {
                foreach (var item in connectClient.Values)
                {
                    if (item.keepAlive >= count)
                    {
                        item.keepAlive = 0;
                        CloseClientSocket(item.saeaReceive);
                    }
                }
                foreach (var item in connectClient.Values)
                {
                    item.keepAlive++;
                }
                Thread.Sleep(overtimecheck * 1000);
            }
        }

        #region Accept

        /// <summary>
        /// 开始接受客户端的连接请求的操作。
        /// </summary>
        /// <param name="acceptEventArg">发布时要使用的上下文对象服务器侦听套接字上的接受操作</param>
        private void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(IOCompleted);
            }
            else
            {
                // 套接字必须被清除，因为上下文对象正在被重用。
                acceptEventArg.AcceptSocket = null;
            }
            this.maxNumberAcceptedClients.WaitOne();
            //准备一个客户端接入
            if (!listenSocket.AcceptAsync(acceptEventArg))
            {
                ProcessAccept(acceptEventArg);
            }
        }

        /// <summary>
        /// 当异步连接完成时调用此方法
        /// </summary>
        /// <param name="e">操作对象</param>
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            connectID++;
            //把连接到的客户端信息添加到集合中
            ConnectClient connecttoken = new ConnectClient();
            connecttoken.socket = e.AcceptSocket;
            //从接受端重用池获取一个新的SocketAsyncEventArgs对象
            connecttoken.saeaReceive = this.receivePool.Pop();
            connecttoken.saeaReceive.UserToken = connectID;
            connecttoken.saeaReceive.AcceptSocket = e.AcceptSocket;
            connectClient.TryAdd(connectID, connecttoken);
            clientList.TryAdd(connectID, e.AcceptSocket.RemoteEndPoint.ToString());
            //一旦客户机连接，就准备接收。
            if (!e.AcceptSocket.ReceiveAsync(connecttoken.saeaReceive))
            {
                ProcessReceive(connecttoken.saeaReceive);
            }
            //事件回调
            if (OnAccept != null)
            {
                OnAccept(connectID);
            }
            //接受第二连接的请求
            StartAccept(e);
        }

        #endregion

        #region 接受处理 receive

        /// <summary>
        /// 接受处理回调
        /// </summary>
        /// <param name="e">操作对象</param>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            //检查远程主机是否关闭连接
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                int connectID = (int)e.UserToken;
                ConnectClient client;
                if (!connectClient.TryGetValue(connectID, out client))
                {
                    return;
                }
                //如果接收到数据，超时记录设置为0
                if (overtime > 0)
                {
                    if (client != null)
                    {
                        client.keepAlive = 0;
                    }
                }
                //回调               
                if (OnReceive != null)
                {
                    if (client != null)
                    {
                        OnReceive(connectID, e.Buffer, e.Offset, e.BytesTransferred);
                    }
                }
                //准备下次接收数据      
                try
                {
                    if (!e.AcceptSocket.ReceiveAsync(e))
                    {
                        ProcessReceive(e);
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    if (OnClose != null)
                    {
                        OnClose(connectID);
                    }
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        #endregion

        #region 发送处理 send

        /// <summary>
        /// 开始启用发送
        /// </summary>
        private void StartSend(object thread)
        {
            while (true)
            {
                SendingQueue sending;
                if (sendQueues[(int)thread].TryDequeue(out sending))
                {
                    Send(sending);
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// 异步发送消息 
        /// </summary>
        /// <param name="connectID">连接ID</param>
        /// <param name="data">数据</param>
        /// <param name="offset">偏移位</param>
        /// <param name="length">长度</param>
        internal void Send(int connectID, byte[] data, int offset, int length)
        {
            sendQueues[connectID % sendthread].Enqueue(new SendingQueue() { connectID = connectID, data = data, offset = offset, length = length });
        }

        /// <summary>
        /// 异步发送消息 
        /// </summary>
        /// <param name="sendQuere">发送消息体</param>
        private void Send(SendingQueue sendQuere)
        {
            ConnectClient client;
            if (!connectClient.TryGetValue(sendQuere.connectID, out client))
            {
                return;
            }
            //如果发送池为空时，临时新建一个放入池中
            mutex.WaitOne();
            if (this.sendPool.Count == 0)
            {
                SocketAsyncEventArgs saea_send = new SocketAsyncEventArgs();
                saea_send.Completed += new EventHandler<SocketAsyncEventArgs>(IOCompleted);
                this.sendPool.Push(saea_send);
            }
            SocketAsyncEventArgs sendEventArgs = this.sendPool.Pop();
            mutex.ReleaseMutex();
            sendEventArgs.UserToken = sendQuere.connectID;
            sendEventArgs.SetBuffer(sendQuere.data, sendQuere.offset, sendQuere.length);
            try
            {
                if (!client.socket.SendAsync(sendEventArgs))
                {
                    ProcessSend(sendEventArgs);
                }
            }
            catch (ObjectDisposedException ex)
            {
                if (OnClose != null)
                {
                    OnClose(sendQuere.connectID);
                }
            }
            sendQuere = null;
        }

        /// <summary>
        /// 发送回调
        /// </summary>
        /// <param name="e">操作对象</param>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                this.sendPool.Push(e);
                if (OnSend != null)
                {
                    OnSend((int)e.UserToken, e.BytesTransferred);
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }

        #endregion

        /// <summary>
        /// 每当套接字上完成接收或发送操作时，都会调用此方法。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">与完成的接收操作关联的SocketAsyncEventArg</param>
        private void IOCompleted(object sender, SocketAsyncEventArgs e)
        {
            //确定刚刚完成哪种类型的操作并调用相关的处理程序
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                case SocketAsyncOperation.Accept:
                    ProcessAccept(e);
                    break;
                default:
                    break;
            }
        }

        #region 断开连接处理


        /// <summary>
        /// 客户端断开一个连接
        /// </summary>
        /// <param name="connectID">连接标记</param>
        internal void Close(int connectID)
        {
            ConnectClient client;
            if (!connectClient.TryGetValue(connectID, out client))
            {
                return;
            }
            CloseClientSocket(client.saeaReceive);
        }

        /// <summary>
        /// 断开一个连接
        /// </summary>
        /// <param name="e">操作对象</param>
        private void CloseClientSocket(SocketAsyncEventArgs e)
        {
            int connectID = (int)e.UserToken;
            ConnectClient client;
            string clientip;
            if (!connectClient.TryRemove(connectID, out client))
            {
                return;
            }
            if (client.socket.Connected == false)
            {
                this.receivePool.Push(e);
                this.maxNumberAcceptedClients.Release();
                clientList.TryRemove(connectID, out clientip);
                return;
            }
            try
            {
                client.socket.Shutdown(SocketShutdown.Both);
            }
            // 抛出客户端进程已经关闭
            catch (Exception) { }
            client.socket.Close();
            this.receivePool.Push(e);
            this.maxNumberAcceptedClients.Release();
            if (OnClose != null)
            {
                OnClose(connectID);
            }
            clientList.TryRemove(connectID, out clientip);
            client = null;
        }

        #endregion

        #region 附加数据

        /// <summary>
        /// 给连接对象设置附加数据
        /// </summary>
        /// <param name="connectID">连接标识</param>
        /// <param name="data">附加数据</param>
        /// <returns>true:设置成功,false:设置失败</returns>
        internal bool SetAttached(int connectID, object data)
        {
            ConnectClient client;
            if (!connectClient.TryGetValue(connectID, out client))
            {
                return false;
            }
            client.Attached = data;
            return true;
        }

        /// <summary>
        /// 获取连接对象的附加数据
        /// </summary>
        /// <param name="connectID">连接标识</param>
        /// <returns>附加数据，如果没有找到则返回null</returns>
        internal T GetAttached<T>(int connectID)
        {
            ConnectClient client;
            if (!connectClient.TryGetValue(connectID, out client))
            {
                return default(T);
            }
            else
            {
                return (T)client.Attached;
            }
        }
        #endregion
    }

}