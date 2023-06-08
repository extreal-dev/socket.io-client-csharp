﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketIO.Serializer.Core;
using SocketIOClient.Extensions;
using SocketIO.Core;

namespace SocketIOClient.Transport
{
    public abstract class BaseTransport : ITransport
    {
        protected BaseTransport(TransportOptions options, ISerializer serializer)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Serializer = serializer;
            _messageQueue = new ConcurrentQueue<IMessage2>();
        }

        protected const string DirtyMessage = "Invalid object's current state, may need to create a new object.";

        DateTime _pingTime;
        readonly ConcurrentQueue<IMessage2> _messageQueue;
        protected TransportOptions Options { get; }
        protected ISerializer Serializer { get; }

        public Action<IMessage2> OnReceived { get; set; }

        protected abstract TransportProtocol Protocol { get; }
        protected CancellationTokenSource PingTokenSource { get; private set; }
        protected IMessage2 OpenedMessage { get; private set; }

        public string Namespace { get; set; }
        public Action<Exception> OnError { get; set; }

        public abstract Task SendAsync(IList<SerializedItem> items, CancellationToken cancellationToken);

        protected virtual async Task OpenAsync(IMessage2 message)
        {
            OpenedMessage = message;
            if (Options.EIO == EngineIO.V3 && string.IsNullOrEmpty(Namespace))
            {
                return;
            }

            var connectedMessage = Serializer.SerializeConnectedMessage(
                Namespace,
                Options.EIO,
                Options.Auth,
                Options.Query);

            await SendAsync(new List<SerializedItem>
            {
                connectedMessage
            }, CancellationToken.None).ConfigureAwait(false);
        }

        private void StartPing(CancellationToken cancellationToken)
        {
            Task.Factory.StartNew(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(OpenedMessage.PingInterval, cancellationToken);
                    try
                    {
                        using (var cts = new CancellationTokenSource(OpenedMessage.PingTimeout))
                        {
                            Debug.WriteLine("Ping");
                            await SendAsync(new List<SerializedItem>
                            {
                                Serializer.SerializePingMessage()
                            }, cts.Token).ConfigureAwait(false);
                        }

                        _pingTime = DateTime.Now;
                        OnReceived.TryInvoke(Serializer.NewMessage(MessageType.Ping));
                    }
                    catch (Exception e)
                    {
                        OnError.TryInvoke(e);
                        break;
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        public abstract Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

        public abstract Task DisconnectAsync(CancellationToken cancellationToken);

        public abstract void AddHeader(string key, string val);
        public abstract void SetProxy(IWebProxy proxy);

        public virtual void Dispose()
        {
            if (PingTokenSource != null && !PingTokenSource.IsCancellationRequested)
            {
                PingTokenSource.Cancel();
                PingTokenSource.Dispose();
            }
        }

        private async Task<bool> HandleEio3Messages(IMessage2 message)
        {
            if (Options.EIO != EngineIO.V3) return false;
            if (message.Type == MessageType.Pong)
            {
                message.Duration = DateTime.Now - _pingTime;
            }
            else if (message.Type == MessageType.Connected)
            {
                var ms = 0;
                while (OpenedMessage is null)
                {
                    await Task.Delay(10);
                    ms += 10;
                    if (ms <= Options.ConnectionTimeout.TotalMilliseconds) continue;
                    OnError.TryInvoke(new TimeoutException());
                    return true;
                }

                message.Sid = OpenedMessage.Sid;
                // if ((string.IsNullOrEmpty(Namespace) && string.IsNullOrEmpty(msg.Namespace)) ||
                //     msg.Namespace == Namespace)
                if (message.Namespace == Namespace)
                {
                    PingTokenSource?.Cancel();

                    PingTokenSource = new CancellationTokenSource();
                    StartPing(PingTokenSource.Token);
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        protected async Task OnTextReceived(string text)
        {
            Debug.WriteLine($"[{Protocol}⬇] {text}");
            var message = Serializer.Deserialize(Options.EIO, text);
            if (message == null) return;

            if (message.BinaryCount > 0)
            {
                _messageQueue.Enqueue(message);
                return;
            }

            if (message.Type == MessageType.Opened)
            {
                await OpenAsync(message).ConfigureAwait(false);
            }

            if (await HandleEio3Messages(message)) return;

            OnReceived.TryInvoke(message);

            if (message.Type == MessageType.Ping)
            {
                _pingTime = DateTime.Now;
                try
                {
                    await SendAsync(new List<SerializedItem>
                    {
                        Serializer.SerializePingMessage()
                    }, CancellationToken.None).ConfigureAwait(false);
                    var pong = Serializer.NewMessage(MessageType.Pong);
                    pong.Duration = DateTime.Now - _pingTime;
                    OnReceived.TryInvoke(pong);
                }
                catch (Exception e)
                {
                    OnError.TryInvoke(e);
                }
            }
        }

        protected void OnBinaryReceived(byte[] bytes)
        {
            Debug.WriteLine($"[{Protocol}⬇]0️⃣1️⃣0️⃣1️⃣");

            if (_messageQueue.Count <= 0)
                return;
            if (!_messageQueue.TryPeek(out var msg))
                return;

            msg.ReceivedBinary.Add(bytes);

            if (msg.ReceivedBinary.Count < msg.BinaryCount)
                return;

            _messageQueue.TryDequeue(out var result);
            OnReceived.TryInvoke(result);
        }
    }
}