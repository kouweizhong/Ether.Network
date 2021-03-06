﻿using System;
using System.Collections.Generic;
using Ether.Network.Packets;
using System.Net.Sockets;
using System.Net;
using Ether.Network.Utils;
using Ether.Network.Exceptions;
using System.Threading;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Ether.Network
{
    /// <summary>
    /// Managed TCP client.
    /// </summary>
    public abstract class NetClient : INetClient
    {
        private readonly Guid _id;
        private readonly string _host;
        private readonly int _port;
        private readonly SocketAsyncEventArgs _socketConnectArgs;
        private readonly SocketAsyncEventArgs _socketReceiveArgs;
        private readonly SocketAsyncEventArgs _socketSendArgs;
        private readonly AutoResetEvent _sendEvent;
        private readonly BlockingCollection<byte[]> _sendQueue;

        /// <summary>
        /// Gets the <see cref="NetClient"/> unique Id.
        /// </summary>
        public Guid Id => this._id;

        /// <summary>
        /// Gets the <see cref="NetClient"/> socket.
        /// </summary>
        protected Socket Socket { get; private set; }

        /// <summary>
        /// Gets the <see cref="NetClient"/> connected state.
        /// </summary>
        public bool IsConnected => this.Socket != null && this.Socket.Connected;

        /// <summary>
        /// Creates a new <see cref="NetClient"/> instance.
        /// </summary>
        /// <param name="host">Remote host or ip</param>
        /// <param name="port">Remote port</param>
        /// <param name="bufferSize">Buffer size</param>
        protected NetClient(string host, int port, int bufferSize)
        {
            this._id = Guid.NewGuid();
            this._host = host;
            this._port = port;
            this._sendEvent = new AutoResetEvent(false);
            this._sendQueue = new BlockingCollection<byte[]>();
            this._socketConnectArgs = this.CreateSocketAsync();
            this._socketSendArgs = this.CreateSocketAsync();
            this._socketReceiveArgs = this.CreateSocketAsync();
            this._socketReceiveArgs.SetBuffer(new byte[bufferSize], 0, bufferSize);
            this.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            Task.Run(() => this.ProcessSend());
        }

        /// <summary>
        /// Connect to the remote host.
        /// </summary>
        public void Connect()
        {
            if (this.IsConnected)
                throw new InvalidOperationException("Client is already connected to remote.");

            IPAddress address = NetUtils.GetIpAddress(this._host);

            if (address == null)
                throw new EtherConfigurationException($"Invalid host or ip address: {this._host}.");
            if (this._port <= 0)
                throw new EtherConfigurationException($"Invalid port: {this._port}");

            this.StartConnect(address, this._port);
        }

        /// <summary>
        /// Disconnects the <see cref="NetClient"/>?
        /// </summary>
        public void Disconnect()
        {
            if (this.IsConnected)
            {
                this.Socket.Shutdown(SocketShutdown.Both);
            }
        }

        /// <summary>
        /// Sends a packet through the network.
        /// </summary>
        /// <param name="packet"></param>
        public void Send(NetPacketBase packet)
        {
            this._sendQueue.Add(packet.Buffer);
        }

        private void ProcessSend()
        {
            while (true)
            {
                byte[] buffer = this._sendQueue.Take();
                if (buffer != null)
                {
                    if (!this.IsConnected)
                        throw new SocketException();

                    if (buffer.Length <= 0)
                        return;

                    Console.WriteLine("NetClient.ProcessSend(): buffer.Length: {0}", buffer.Length);

                    this._socketSendArgs.SetBuffer(buffer, 0, buffer.Length);

                    if (this.Socket != null)
                    {
                        this.Socket.SendAsync(this._socketSendArgs);
                        this._sendEvent.WaitOne();
                    }
                }
            }
        }

        /// <summary>
        /// Triggered when the <see cref="NetClient"/> receives a packet.
        /// </summary>
        /// <param name="packet"></param>
        protected abstract void HandleMessage(NetPacketBase packet);

        /// <summary>
        /// Triggered when the client is connected to the remote end point.
        /// </summary>
        protected abstract void OnConnected();

        /// <summary>
        /// Triggered when the client is disconnected from the remote end point.
        /// </summary>
        protected abstract void OnDisconnected();

        /// <summary>
        /// Split an incoming buffer from the network in a collection of <see cref="NetPacketBase"/>.
        /// </summary>
        /// <param name="buffer">Incoming data</param>
        /// <returns></returns>
        protected virtual IReadOnlyCollection<NetPacketBase> SplitPackets(byte[] buffer) => NetPacket.Split(buffer);

        /// <summary>
        /// Starts the connect async operation.
        /// </summary>
        /// <param name="address">Remote address</param>
        /// <param name="port">Remote port</param>
        private void StartConnect(IPAddress address, int port)
        {
            this._socketConnectArgs.RemoteEndPoint = new IPEndPoint(address, port);

            if (!this.Socket.ConnectAsync(this._socketConnectArgs))
                this.ProcessConnect(this._socketConnectArgs);
        }

        /// <summary>
        /// Process the connect async operation.
        /// </summary>
        /// <param name="e"></param>
        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                this.OnConnected();
                this.StartReceive(this._socketReceiveArgs);
            }
        }

        /// <summary>
        /// Starts the receive async operation.
        /// </summary>
        /// <param name="e"></param>
        private void StartReceive(SocketAsyncEventArgs e)
        {
            if (this.Socket != null && !this.Socket.ReceiveAsync(e))
                this.ProcessReceive(e);
        }

        /// <summary>
        /// Process the receive async operation.
        /// </summary>
        /// <param name="e"></param>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
            {
                byte[] buffer = NetUtils.GetPacketBuffer(e.Buffer, e.Offset, e.BytesTransferred);
                IReadOnlyCollection<NetPacketBase> packets = this.SplitPackets(buffer);

                foreach (var packet in packets)
                    this.HandleMessage(packet);
            }
            else if (e.SocketError == SocketError.ConnectionReset)
            {
                Console.WriteLine(e.SocketError.ToString());
                this.Disconnect();
                return;
            }

            this.StartReceive(e);
        }

        /// <summary>
        /// Triggered when a <see cref="SocketAsyncEventArgs"/> async operation is completed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            if (sender == null)
                throw new ArgumentNullException(nameof(sender));
            if (e.LastOperation == SocketAsyncOperation.Connect)
                this.ProcessConnect(e);
            if (e.LastOperation == SocketAsyncOperation.Receive)
                this.ProcessReceive(e);
            if (e.LastOperation == SocketAsyncOperation.Send)
                this._sendEvent.Set();
        }

        /// <summary>
        /// Creates a <see cref="SocketAsyncEventArgs"/>.
        /// </summary>
        /// <returns></returns>
        private SocketAsyncEventArgs CreateSocketAsync()
        {
            var socketAsync = new SocketAsyncEventArgs();
            socketAsync.UserToken = this.Socket;
            socketAsync.Completed += this.IO_Completed;

            return socketAsync;
        }
    }
}
