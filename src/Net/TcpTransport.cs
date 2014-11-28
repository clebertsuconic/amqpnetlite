﻿//  ------------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation
//  All rights reserved. 
//  
//  Licensed under the Apache License, Version 2.0 (the ""License""); you may not use this 
//  file except in compliance with the License. You may obtain a copy of the License at 
//  http://www.apache.org/licenses/LICENSE-2.0  
//  
//  THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, 
//  EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR 
//  CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR 
//  NON-INFRINGEMENT. 
// 
//  See the Apache Version 2.0 License for specific language governing permissions and 
//  limitations under the License.
//  ------------------------------------------------------------------------------------

namespace Amqp
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    sealed class TcpTransport : IAsyncTransport
    {
        static readonly RemoteCertificateValidationCallback noneCertValidator = (a, b, c, d) => true;
        Connection connection;
        Writer writer;
        IAsyncTransport socketTransport;

        public void Connect(Connection connection, Address address, bool noVerification)
        {
            this.ConnectAsync(address, new ConnectionFactory()).Wait();
        }

        public async Task ConnectAsync(Address address, ConnectionFactory factory)
        {
            TcpSocket socket;
            IPAddress[] ipAddresses;
            IPAddress ipAddress;
            if (IPAddress.TryParse(address.Host, out ipAddress))
            {
                socket = new TcpSocket(this, ipAddress.AddressFamily);
                ipAddresses = new IPAddress[] { ipAddress };
            }
            else
            {
                socket = new TcpSocket(this, AddressFamily.InterNetwork);
                ipAddresses = Dns.GetHostEntry(address.Host).AddressList;
            }

            if (factory.tcpSettings != null)
            {
                factory.tcpSettings.Configure(socket);
            }

            await Task.Factory.FromAsync(
                (c, s) => ((Socket)s).BeginConnect(ipAddresses, address.Port, c, s),
                (r) => ((Socket)r.AsyncState).EndConnect(r),
                socket);

            IAsyncTransport transport = socket;
            if (address.UseSsl)
            {
                SslSocket sslSocket;
                var ssl = factory.sslSettings;
                if (ssl == null)
                {
                    sslSocket = new SslSocket(this, socket, null);
                    await sslSocket.AuthenticateAsClientAsync(address.Host);
                }
                else
                {
                    sslSocket = new SslSocket(this, socket, ssl.RemoteCertificateValidationCallback);
                    await sslSocket.AuthenticateAsClientAsync(address.Host, ssl.ClientCertificates,
                        ssl.Protocols, ssl.CheckCertificateRevocation);
                }

                transport = sslSocket;
            }

            this.socketTransport = transport;
            this.writer = new Writer(this, this.socketTransport);
        }

        void IAsyncTransport.SetConnection(Connection connection)
        {
            this.connection = connection;
        }

        Task<int> IAsyncTransport.ReceiveAsync(byte[] buffer, int offset, int count)
        {
            return this.socketTransport.ReceiveAsync(buffer, offset, count);
        }

        bool IAsyncTransport.SendAsync(ByteBuffer buffer, IList<ArraySegment<byte>> bufferList, int listSize)
        {
            throw new InvalidOperationException();
        }

        void ITransport.Close()
        {
            this.socketTransport.Close();
        }

        void ITransport.Send(ByteBuffer buffer)
        {
            this.writer.Write(buffer);
        }

        int ITransport.Receive(byte[] buffer, int offset, int count)
        {
            return this.socketTransport.Receive(buffer, offset, count);
        }

        class TcpSocket : Socket, IAsyncTransport
        {
            readonly static EventHandler<SocketAsyncEventArgs> onWriteComplete = OnWriteComplete;
            readonly TcpTransport transport;
            readonly SocketAsyncEventArgs args;

            public TcpSocket(TcpTransport transport, AddressFamily addressFamily)
                : base(addressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                this.NoDelay = true;

                this.transport = transport;
                this.args = new SocketAsyncEventArgs();
                this.args.Completed += onWriteComplete;
                this.args.UserToken = this;
            }

            static void OnWriteComplete(object sender, SocketAsyncEventArgs eventArgs)
            {
                var thisPtr = (TcpSocket)eventArgs.UserToken;
                if (eventArgs.SocketError != SocketError.Success)
                {
                    thisPtr.transport.connection.OnIoException(new SocketException((int)eventArgs.SocketError));
                }
                else
                {
                    thisPtr.transport.writer.ContinueWrite();
                }
            }

            void IAsyncTransport.SetConnection(Connection connection)
            {
                throw new NotImplementedException();
            }

            bool IAsyncTransport.SendAsync(ByteBuffer buffer, IList<ArraySegment<byte>> bufferList, int listSize)
            {
                if (buffer != null)
                {
                    this.args.BufferList = null;
                    this.args.SetBuffer(buffer.Buffer, buffer.Offset, buffer.Length);
                }
                else
                {
                    this.args.SetBuffer(null, 0, 0);
                    this.args.BufferList = bufferList;
                }

                return this.SendAsync(this.args);
            }

            Task<int> IAsyncTransport.ReceiveAsync(byte[] buffer, int offset, int count)
            {
                return Task.Factory.FromAsync(
                    (c, s) => this.BeginReceive(buffer, offset, count, SocketFlags.None, c, s),
                    (r) => this.EndReceive(r),
                    null);
            }

            void ITransport.Send(ByteBuffer buffer)
            {
                throw new InvalidOperationException();
            }

            int ITransport.Receive(byte[] buffer, int offset, int count)
            {
                return base.Receive(buffer, offset, count, SocketFlags.None);
            }

            void ITransport.Close()
            {
                base.Close();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.args.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        class SslSocket : SslStream, IAsyncTransport
        {
            readonly TcpTransport transport;

            public SslSocket(TcpTransport transport, Socket socket, RemoteCertificateValidationCallback certValidator)
                : base(new NetworkStream(socket), false, certValidator)
            {
                this.transport = transport;
            }

            void IAsyncTransport.SetConnection(Connection connection)
            {
                throw new NotImplementedException();
            }

            bool IAsyncTransport.SendAsync(ByteBuffer buffer, IList<ArraySegment<byte>> bufferList, int listSize)
            {
                ArraySegment<byte> writeBuffer;
                if (buffer != null)
                {
                    writeBuffer = new ArraySegment<byte>(buffer.Buffer, buffer.Offset, buffer.Length);
                }
                else
                {
                    byte[] temp = new byte[listSize];
                    int offset = 0;
                    for (int i = 0; i < bufferList.Count; i++)
                    {
                        ArraySegment<byte> segment = bufferList[i];
                        Buffer.BlockCopy(segment.Array, segment.Offset, temp, offset, segment.Count);
                        offset += segment.Count;
                    }

                    writeBuffer = new ArraySegment<byte>(temp, 0, listSize);
                }

                Task task = this.WriteAsync(writeBuffer.Array, writeBuffer.Offset, writeBuffer.Count);
                bool pending = !task.IsCompleted;
                if (pending)
                {
                    task.ContinueWith(
                        (t, s) =>
                        {
                            var thisPtr = (SslSocket)s;
                            if (t.IsFaulted)
                            {
                                thisPtr.transport.connection.OnIoException(t.Exception.InnerException);
                            }
                            else
                            {
                                thisPtr.transport.writer.ContinueWrite();
                            }
                        },
                        this);
                }

                return pending;
            }

            Task<int> IAsyncTransport.ReceiveAsync(byte[] buffer, int offset, int count)
            {
                return this.ReadAsync(buffer, offset, count);
            }

            void ITransport.Send(ByteBuffer buffer)
            {
                throw new InvalidOperationException();
            }

            int ITransport.Receive(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, count);
            }

            void ITransport.Close()
            {
                base.Close();
            }
        }

        sealed class Writer
        {
            readonly TcpTransport owner;
            readonly IAsyncTransport transport;
            Queue<ByteBuffer> bufferQueue;
            bool writing;

            public Writer(TcpTransport owner, IAsyncTransport transport)
            {
                this.owner = owner;
                this.transport = transport;
                this.bufferQueue = new Queue<ByteBuffer>();
            }

            public void Write(ByteBuffer buffer)
            {
                lock (this.bufferQueue)
                {
                    if (this.writing)
                    {
                        this.bufferQueue.Enqueue(buffer);
                        return;
                    }

                    this.writing = true;
                }

                if (!this.transport.SendAsync(buffer, null, 0))
                {
                    this.ContinueWrite();
                }
            }

            public void ContinueWrite()
            {
                ByteBuffer buffer = null;
                IList<ArraySegment<byte>> buffers = null;
                int listSize = 0;
                do
                {
                    lock (this.bufferQueue)
                    {
                        int queueDepth = this.bufferQueue.Count;
                        if (queueDepth == 0)
                        {
                            this.writing = false;
                            return;
                        }
                        else if (queueDepth == 1)
                        {
                            buffer = this.bufferQueue.Dequeue();
                            buffers = null;
                        }
                        else
                        {
                            buffer = null;
                            listSize = 0;
                            buffers = new ArraySegment<byte>[queueDepth];
                            for (int i = 0; i < queueDepth; i++)
                            {
                                ByteBuffer item = this.bufferQueue.Dequeue();
                                buffers[i] = new ArraySegment<byte>(item.Buffer, item.Offset, item.Length);
                                listSize += item.Length;
                            }
                        }
                    }

                    try
                    {
                        if (this.transport.SendAsync(buffer, buffers, listSize))
                        {
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        this.owner.connection.OnIoException(exception);
                        break;
                    }
                }
                while (true);
            }
        }
    }
}