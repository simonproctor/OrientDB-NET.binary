﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Orient.Client.Protocol.Operations;
using Orient.Client.Protocol.Serializers;

namespace Orient.Client.Protocol
{
    internal class Connection : IDisposable
    {
        private TcpClient _socket;
        private NetworkStream _networkStream;
        private byte[] _readBuffer;

        internal string Hostname { get; set; }
        internal int Port { get; set; }
        internal ConnectionType Type { get; private set; }

        internal string Alias { get; set; }
        internal bool IsReusable { get; set; }
        internal short ProtocolVersion { get; set; }
        internal int SessionId { get; private set; }
        internal bool IsActive
        {
            get
            {
                // If the socket has been closed by your own actions (disposing the socket, 
                // calling methods to disconnect), Socket.Connected will return false. If 
                // the socket has been disconnected by other means, the property will return 
                // true until the next attempt to send or receive information.
                // more info: http://stackoverflow.com/questions/2661764/how-to-check-if-a-socket-is-connected-disconnected-in-c
                // why not to use socket.Poll solution: it fails when the socket is being initialized
                // and introduces additional delay for connection check
                if ((_socket != null) && _socket.Connected)
                {
                    return true;
                }

                return false;
            }
        }
        internal ODocument Document { get; set; }

        internal Connection(string hostname, int port, string databaseName, ODatabaseType databaseType, string userName, string userPassword, string alias, bool isReusable)
        {
            Hostname = hostname;
            Port = port;
            Type = ConnectionType.Database;
            Alias = alias;
            IsReusable = isReusable;
            ProtocolVersion = 0;
            SessionId = -1;

            InitializeDatabaseConnection(databaseName, databaseType, userName, userPassword);
        }

        internal Connection(string hostname, int port, string userName, string userPassword)
        {
            Hostname = hostname;
            Port = port;
            Type = ConnectionType.Server;
            IsReusable = false;
            ProtocolVersion = 0;
            SessionId = -1;

            InitializeServerConnection(userName, userPassword);
        }

        internal ODocument ExecuteOperation<T>(T operation)
        {
            Request request = ((IOperation)operation).Request(SessionId);
            byte[] buffer;

            foreach (RequestDataItem item in request.DataItems)
            {
                switch (item.Type)
                {
                    case "byte":
                    case "short":
                    case "int":
                    case "long":
                        Send(item.Data);
                        break;
                    case "record":
                        buffer = new byte[2 + item.Data.Length];
                        Buffer.BlockCopy(BinarySerializer.ToArray(item.Data.Length), 0, buffer, 0, 2);
                        Buffer.BlockCopy(item.Data, 0, buffer, 2, item.Data.Length);
                        Send(buffer);
                        break;
                    case "bytes":
                    case "string":
                    case "strings":
                        //buffer = new byte[4 + item.Data.Length];
                        //Buffer.BlockCopy(BinarySerializer.ToArray(item.Data.Length), 0, buffer, 0, 4);
                        //Buffer.BlockCopy(item.Data, 0, buffer, 4, item.Data.Length);
                        //Send(buffer);

                        Send(BinarySerializer.ToArray(item.Data.Length));
                        Send(item.Data);
                        break;
                    default:
                        break;
                }
            }

            if (request.OperationMode == OperationMode.Synchronous)
            {
                Response response = new Response();

                response.Data = Receive();
                // parse standard response fields
                response.Status = (ResponseStatus)BinarySerializer.ToByte(response.Data.Take(1).ToArray());
                response.SessionId = BinarySerializer.ToInt(response.Data.Skip(1).Take(4).ToArray());

                if (response.Status == ResponseStatus.ERROR)
                {
                    ParseResponseError(response);
                }

                return ((IOperation)operation).Response(response);
            }
            else
            {
                return null;
            }
        }

        internal void Close()
        {
            SessionId = -1;

            if ((_networkStream != null) && (_socket != null))
            {
                _networkStream.Close();
                _socket.Close();
            }

            _networkStream = null;
            _socket = null;
        }

        public void Dispose()
        {
            Close();
        }

        #region Private methods

        private void InitializeDatabaseConnection(string databaseName, ODatabaseType databaseType, string userName, string userPassword)
        {
            _readBuffer = new byte[OClient.BufferLenght];

            // initiate socket connection
            try
            {
                _socket = new TcpClient(Hostname, Port);
            }
            catch (SocketException ex)
            {
                throw new OException(OExceptionType.Connection, ex.Message, ex.InnerException);
            }

            _networkStream = _socket.GetStream();
            _networkStream.Read(_readBuffer, 0, 2);

            ProtocolVersion = BinarySerializer.ToShort(_readBuffer.Take(2).ToArray());

            // execute db_open operation
            DbOpen operation = new DbOpen();
            operation.DatabaseName = databaseName;
            operation.DatabaseType = databaseType;
            operation.UserName = userName;
            operation.UserPassword = userPassword;

            Document = ExecuteOperation<DbOpen>(operation);
            SessionId = Document.GetField<int>("SessionId");
        }

        private void InitializeServerConnection(string userName, string userPassword)
        {
            _readBuffer = new byte[OClient.BufferLenght];

            // initiate socket connection
            try
            {
                _socket = new TcpClient(Hostname, Port);
            }
            catch (SocketException ex)
            {
                throw new OException(OExceptionType.Connection, ex.Message, ex.InnerException);
            }

            _networkStream = _socket.GetStream();
            _networkStream.Read(_readBuffer, 0, 2);

            ProtocolVersion = BinarySerializer.ToShort(_readBuffer.Take(2).ToArray());

            // execute connect operation
            Connect operation = new Connect();
            operation.UserName = userName;
            operation.UserPassword = userPassword;

            Document = ExecuteOperation<Connect>(operation);
            SessionId = Document.GetField<int>("SessionId");
        }

        private void Send(byte[] rawData)
        {
            if ((_networkStream != null) && _networkStream.CanWrite)
            {
                try
                {
                    _networkStream.Write(rawData, 0, rawData.Length);
                }
                catch (Exception ex)
                {
                    throw new OException(OExceptionType.Connection, ex.Message, ex.InnerException);
                }
            }
        }

        private byte[] Receive()
        {
            MemoryStream memoryStream = new MemoryStream();

            if ((_networkStream != null) && _networkStream.CanRead)
            {
                try
                {
                    do
                    {
                        int bytesRead = _networkStream.Read(_readBuffer, 0, _readBuffer.Length);

                        memoryStream.Write(_readBuffer, 0, bytesRead);
                    }
                    while (_networkStream.DataAvailable);
                }
                catch (Exception ex)
                {
                    throw new OException(OExceptionType.Connection, ex.Message, ex.InnerException);
                }
            }

            return memoryStream.ToArray();
        }

        private void ParseResponseError(Response response)
        {
            int offset = 5;
            string exceptionString = "";

            byte followByte = BinarySerializer.ToByte(response.Data.Skip(offset).Take(1).ToArray());
            offset += 1;

            while (followByte == 1)
            {
                int exceptionClassLength = BinarySerializer.ToInt(response.Data.Skip(offset).Take(4).ToArray());
                offset += 4;

                exceptionString += BinarySerializer.ToString(response.Data.Skip(offset).Take(exceptionClassLength).ToArray()) + ": ";
                offset += exceptionClassLength;

                int exceptionMessageLength = BinarySerializer.ToInt(response.Data.Skip(offset).Take(4).ToArray());
                offset += 4;

                // don't read exception message string if it's null
                if (exceptionMessageLength != -1)
                {
                    exceptionString += BinarySerializer.ToString(response.Data.Skip(offset).Take(exceptionMessageLength).ToArray()) + "\n";
                    offset += exceptionMessageLength;
                }

                followByte = BinarySerializer.ToByte(response.Data.Skip(offset).Take(1).ToArray());
                offset += 1;
            }

            throw new OException(OExceptionType.Operation, exceptionString);
        }

        #endregion
    }
}
