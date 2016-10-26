﻿using AntShares.Core;
using AntShares.Cryptography;
using AntShares.IO;
using AntShares.Network.Payloads;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AntShares.Network
{
    public class RemoteNode : IDisposable
    {
        public event EventHandler<bool> Disconnected;
        internal event EventHandler<IInventory> InventoryReceived;
        internal event EventHandler<IPEndPoint[]> PeersReceived;

        private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

        private Queue<Message> message_queue = new Queue<Message>();
        private static HashSet<UInt256> missions_global = new HashSet<UInt256>();
        private HashSet<UInt256> missions = new HashSet<UInt256>();

        private LocalNode localNode;
        private Thread protocolThread;
        private Thread sendThread;
        private Socket socket;
        private NetworkStream stream;
        private bool connected = false;
        private int disposed = 0;
        private BloomFilter bloom_filter;

        internal VersionPayload Version { get; private set; }
        public IPEndPoint RemoteEndpoint { get; private set; }
        public IPEndPoint ListenerEndpoint { get; private set; }

        private RemoteNode(LocalNode localNode)
        {
            this.localNode = localNode;
            this.protocolThread = new Thread(RunProtocol) { IsBackground = true };
            this.sendThread = new Thread(SendLoop) { IsBackground = true };
        }

        internal RemoteNode(LocalNode localNode, IPEndPoint remoteEndpoint)
            : this(localNode)
        {
            this.socket = new Socket(remoteEndpoint.Address.IsIPv4MappedToIPv6 ? AddressFamily.InterNetwork : remoteEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            this.ListenerEndpoint = remoteEndpoint;
        }

        internal RemoteNode(LocalNode localNode, Socket socket)
            : this(localNode)
        {
            this.socket = socket;
            OnConnected();
        }

        internal async Task ConnectAsync()
        {
            IPAddress address = ListenerEndpoint.Address;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            try
            {
                await socket.ConnectAsync(address, ListenerEndpoint.Port);
                OnConnected();
            }
            catch (SocketException)
            {
                Disconnect(false);
                return;
            }
            StartProtocol();
        }

        public void Disconnect(bool error)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                if (stream != null) stream.Dispose();
                socket.Dispose();
                Disconnected?.Invoke(this, error);
                lock (missions_global)
                {
                    foreach (UInt256 hash in missions)
                    {
                        missions_global.Remove(hash);
                    }
                }
                if (protocolThread != Thread.CurrentThread && !protocolThread.ThreadState.HasFlag(ThreadState.Unstarted))
                    protocolThread.Join();
                if (!sendThread.ThreadState.HasFlag(ThreadState.Unstarted)) sendThread.Join();
            }
        }

        public void Dispose()
        {
            Disconnect(false);
        }

        private void EnqueueMessage(string command, ISerializable payload = null, bool is_single = false)
        {
            lock (message_queue)
            {
                if (!is_single || message_queue.All(p => p.Command != command))
                {
                    message_queue.Enqueue(Message.Create(command, payload));
                }
            }
        }

        private void OnAddrMessageReceived(AddrPayload payload)
        {
            IPEndPoint[] peers = payload.AddressList.Select(p => p.EndPoint).Where(p => p.Port != localNode.Port || !LocalNode.LocalAddresses.Contains(p.Address)).ToArray();
            if (peers.Length > 0) PeersReceived?.Invoke(this, peers);
        }

        private void OnConnected()
        {
            IPEndPoint remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint;
            RemoteEndpoint = new IPEndPoint(remoteEndpoint.Address.MapToIPv6(), remoteEndpoint.Port);
            protocolThread.Name = $"RemoteNode.RunProtocol@{RemoteEndpoint}";
            sendThread.Name = $"RemoteNode.SendLoop@{RemoteEndpoint}";
            socket.SendTimeout = 10000;
            stream = new NetworkStream(socket);
            connected = true;
        }

        private void OnFilterAddMessageReceived(FilterAddPayload payload)
        {
            if (bloom_filter != null)
                bloom_filter.Add(payload.Data);
        }

        private void OnFilterClearMessageReceived()
        {
            bloom_filter = null;
        }

        private void OnFilterLoadMessageReceived(FilterLoadPayload payload)
        {
            bloom_filter = new BloomFilter(payload.Filter.Length * 8, payload.K, payload.Tweak, payload.Filter);
        }

        private void OnGetAddrMessageReceived()
        {
            if (!localNode.ServiceEnabled) return;
            AddrPayload payload;
            lock (localNode.connectedPeers)
            {
                payload = AddrPayload.Create(localNode.connectedPeers.Where(p => p.ListenerEndpoint != null).Take(100).Select(p => NetworkAddressWithTime.Create(p.ListenerEndpoint, p.Version.Services, p.Version.Timestamp)).ToArray());
            }
            EnqueueMessage("addr", payload, true);
        }

        private void OnGetBlocksMessageReceived(GetBlocksPayload payload)
        {
            if (!localNode.ServiceEnabled) return;
            if (Blockchain.Default == null) return;
            if (!Blockchain.Default.Ability.HasFlag(BlockchainAbility.BlockIndexes)) return;
            UInt256 hash = payload.HashStart.Select(p => Blockchain.Default.GetHeader(p)).Where(p => p != null).OrderBy(p => p.Height).Select(p => p.Hash).FirstOrDefault();
            if (hash == null || hash == payload.HashStop) return;
            List<UInt256> hashes = new List<UInt256>();
            do
            {
                hash = Blockchain.Default.GetNextBlockHash(hash);
                if (hash == null) break;
                hashes.Add(hash);
            } while (hash != payload.HashStop && hashes.Count < 500);
            EnqueueMessage("inv", InvPayload.Create(InventoryType.Block, hashes.ToArray()));
        }

        private void OnGetDataMessageReceived(InvPayload payload)
        {
            foreach (UInt256 hash in payload.Hashes.Distinct())
            {
                IInventory inventory;
                if (!localNode.RelayCache.TryGet(hash, out inventory) && !localNode.ServiceEnabled)
                    continue;
                switch (payload.Type)
                {
                    case InventoryType.TX:
                        if (inventory == null)
                            inventory = LocalNode.GetTransaction(hash);
                        if (inventory == null && Blockchain.Default != null)
                            inventory = Blockchain.Default.GetTransaction(hash);
                        if (inventory != null)
                            EnqueueMessage("tx", inventory);
                        break;
                    case InventoryType.Block:
                        if (inventory == null && Blockchain.Default != null)
                            inventory = Blockchain.Default.GetBlock(hash);
                        if (inventory != null)
                        {
                            BloomFilter filter = bloom_filter;
                            if (filter == null)
                            {
                                EnqueueMessage("block", inventory);
                            }
                            else
                            {
                                Block block = (Block)inventory;
                                BitArray flags = new BitArray(block.Transactions.Select(p => TestFilter(filter, p)).ToArray());
                                EnqueueMessage("merkleblock", MerkleBlockPayload.Create(block, flags));
                            }
                        }
                        break;
                    case InventoryType.Consensus:
                        if (inventory != null)
                            EnqueueMessage("consensus", inventory);
                        break;
                }
            }
        }

        private void OnGetHeadersMessageReceived(GetBlocksPayload payload)
        {
            if (!localNode.ServiceEnabled) return;
            if (Blockchain.Default == null) return;
            if (!Blockchain.Default.Ability.HasFlag(BlockchainAbility.BlockIndexes)) return;
            UInt256 hash = payload.HashStart.Select(p => Blockchain.Default.GetHeader(p)).Where(p => p != null).OrderBy(p => p.Height).Select(p => p.Hash).FirstOrDefault();
            if (hash == null || hash == payload.HashStop) return;
            List<Header> headers = new List<Header>();
            do
            {
                hash = Blockchain.Default.GetNextBlockHash(hash);
                if (hash == null) break;
                headers.Add(Blockchain.Default.GetHeader(hash));
            } while (hash != payload.HashStop && headers.Count < 2000);
            EnqueueMessage("headers", HeadersPayload.Create(headers));
        }

        private void OnHeadersMessageReceived(HeadersPayload payload)
        {
            if (Blockchain.Default == null) return;
            Blockchain.Default.AddHeaders(payload.Headers);
            if (Blockchain.Default.HeaderHeight < Version.StartHeight)
            {
                EnqueueMessage("getheaders", GetBlocksPayload.Create(Blockchain.Default.GetLeafHeaderHashes()), true);
            }
        }

        private void OnInventoryReceived(IInventory inventory)
        {
            lock (missions_global)
            {
                missions_global.Remove(inventory.Hash);
            }
            missions.Remove(inventory.Hash);
            if (inventory is MinerTransaction) return;
            InventoryReceived?.Invoke(this, inventory);
        }

        private void OnInvMessageReceived(InvPayload payload)
        {
            if (payload.Type != InventoryType.TX && payload.Type != InventoryType.Block && payload.Type != InventoryType.Consensus)
                return;
            UInt256[] hashes = payload.Hashes.Distinct().ToArray();
            lock (LocalNode.KnownHashes)
            {
                hashes = hashes.Where(p => !LocalNode.KnownHashes.Contains(p)).ToArray();
            }
            if (hashes.Length == 0) return;
            lock (missions_global)
            {
                if (localNode.GlobalMissionsEnabled)
                    hashes = hashes.Where(p => !missions_global.Contains(p)).ToArray();
                foreach (UInt256 hash in hashes)
                {
                    missions_global.Add(hash);
                    missions.Add(hash);
                }
            }
            if (hashes.Length == 0) return;
            EnqueueMessage("getdata", InvPayload.Create(payload.Type, hashes));
        }

        private void OnMemPoolMessageReceived()
        {
            EnqueueMessage("inv", InvPayload.Create(InventoryType.TX, LocalNode.GetMemoryPool().Select(p => p.Hash).ToArray()));
        }

        private void OnMessageReceived(Message message)
        {
            switch (message.Command)
            {
                case "addr":
                    OnAddrMessageReceived(message.Payload.AsSerializable<AddrPayload>());
                    break;
                case "block":
                    OnInventoryReceived(message.Payload.AsSerializable<Block>());
                    break;
                case "consensus":
                    OnInventoryReceived(message.Payload.AsSerializable<ConsensusPayload>());
                    break;
                case "filteradd":
                    OnFilterAddMessageReceived(message.Payload.AsSerializable<FilterAddPayload>());
                    break;
                case "filterclear":
                    OnFilterClearMessageReceived();
                    break;
                case "filterload":
                    OnFilterLoadMessageReceived(message.Payload.AsSerializable<FilterLoadPayload>());
                    break;
                case "getaddr":
                    OnGetAddrMessageReceived();
                    break;
                case "getblocks":
                    OnGetBlocksMessageReceived(message.Payload.AsSerializable<GetBlocksPayload>());
                    break;
                case "getdata":
                    OnGetDataMessageReceived(message.Payload.AsSerializable<InvPayload>());
                    break;
                case "getheaders":
                    OnGetHeadersMessageReceived(message.Payload.AsSerializable<GetBlocksPayload>());
                    break;
                case "headers":
                    OnHeadersMessageReceived(message.Payload.AsSerializable<HeadersPayload>());
                    break;
                case "inv":
                    OnInvMessageReceived(message.Payload.AsSerializable<InvPayload>());
                    break;
                case "mempool":
                    OnMemPoolMessageReceived();
                    break;
                case "tx":
                    if (message.Payload.Length <= 1024 * 1024)
                        OnInventoryReceived(Transaction.DeserializeFrom(message.Payload));
                    break;
                case "alert":
                case "merkleblock":
                case "notfound":
                case "ping":
                case "pong":
                case "reject":
                    //暂时忽略
                    break;
                case "verack":
                case "version":
                default:
                    Disconnect(true);
                    break;
            }
        }

        private Message ReceiveMessage(TimeSpan timeout)
        {
            if (timeout == Timeout.InfiniteTimeSpan) timeout = TimeSpan.Zero;
            BinaryReader reader = null;
            try
            {
                reader = new BinaryReader(stream, Encoding.UTF8, true);
                socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                return reader.ReadSerializable<Message>();
            }
            catch (ArgumentException) { }
            catch (ObjectDisposedException) { }
            catch (FormatException)
            {
                Disconnect(true);
            }
            catch (IOException)
            {
                Disconnect(false);
            }
            finally
            {
                if (reader != null) reader.Dispose();
            }
            return null;
        }

        internal bool Relay(IInventory data)
        {
            if (!Version.Relay) return false;
            if (data.InventoryType == InventoryType.TX)
            {
                BloomFilter filter = bloom_filter;
                if (filter != null && !TestFilter(filter, (Transaction)data))
                    return false;
            }
            EnqueueMessage("inv", InvPayload.Create(data.InventoryType, data.Hash));
            return true;
        }

        internal void RequestMemoryPool()
        {
            EnqueueMessage("mempool", null, true);
        }

        internal void RequestPeers()
        {
            EnqueueMessage("getaddr", null, true);
        }

        private void RunProtocol()
        {
            if (!SendMessage(Message.Create("version", VersionPayload.Create(localNode.Port, localNode.Nonce, localNode.UserAgent))))
                return;
            Message message = ReceiveMessage(TimeSpan.FromSeconds(30));
            if (message == null) return;
            if (message.Command != "version")
            {
                Disconnect(true);
                return;
            }
            try
            {
                Version = message.Payload.AsSerializable<VersionPayload>();
            }
            catch (EndOfStreamException)
            {
                Disconnect(false);
                return;
            }
            catch (FormatException)
            {
                Disconnect(true);
                return;
            }
            if (Version.Nonce == localNode.Nonce)
            {
                Disconnect(true);
                return;
            }
            lock (localNode.pendingPeers)
            {
                lock (localNode.connectedPeers)
                {
                    if (localNode.connectedPeers.Any(p => p.RemoteEndpoint.Address.Equals(RemoteEndpoint.Address) && p.Version.Nonce == Version.Nonce))
                    {
                        Disconnect(false);
                        return;
                    }
                    localNode.connectedPeers.Add(this);
                }
                if (ListenerEndpoint != null)
                    localNode.pendingPeers.Remove(ListenerEndpoint);
            }
            if (ListenerEndpoint != null)
            {
                if (ListenerEndpoint.Port != Version.Port)
                {
                    Disconnect(true);
                    return;
                }
            }
            else if (Version.Port > 0)
            {
                ListenerEndpoint = new IPEndPoint(RemoteEndpoint.Address, Version.Port);
            }
            if (!SendMessage(Message.Create("verack"))) return;
            message = ReceiveMessage(TimeSpan.FromSeconds(30));
            if (message == null) return;
            if (message.Command != "verack")
            {
                Disconnect(true);
                return;
            }
            if (Blockchain.Default?.HeaderHeight < Version.StartHeight)
            {
                HashSet<UInt256> hashes = new HashSet<UInt256>(Blockchain.Default.GetLeafHeaderHashes());
                hashes.UnionWith(hashes.Select(p => Blockchain.Default.GetHeader(p).PrevBlock).ToArray());
                EnqueueMessage("getheaders", GetBlocksPayload.Create(hashes), true);
            }
            sendThread.Start();
            while (disposed == 0)
            {
                if (Blockchain.Default != null && !Blockchain.Default.IsReadOnly)
                {
                    if (missions.Count == 0 && Blockchain.Default.Height < Version.StartHeight)
                    {
                        EnqueueMessage("getblocks", GetBlocksPayload.Create(new[] { Blockchain.Default.CurrentBlockHash }), true);
                    }
                }
                TimeSpan timeout = missions.Count == 0 ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(60);
                message = ReceiveMessage(timeout);
                if (message == null) break;
                try
                {
                    OnMessageReceived(message);
                }
                catch (EndOfStreamException)
                {
                    Disconnect(false);
                    break;
                }
                catch (FormatException)
                {
                    Disconnect(true);
                    break;
                }
            }
        }

        private void SendLoop()
        {
            while (disposed == 0)
            {
                Message message = null;
                lock (message_queue)
                {
                    if (message_queue.Count > 0)
                    {
                        message = message_queue.Dequeue();
                    }
                }
                if (message == null)
                {
                    for (int i = 0; i < 10 && disposed == 0; i++)
                    {
                        Thread.Sleep(100);
                    }
                }
                else
                {
                    SendMessage(message);
                }
            }
        }

        private bool SendMessage(Message message)
        {
            if (!connected) throw new InvalidOperationException();
            if (disposed > 0) return false;
            byte[] buffer = message.ToArray();
            try
            {
                stream.Write(buffer, 0, buffer.Length);
                return true;
            }
            catch (ObjectDisposedException) { }
            catch (IOException)
            {
                Disconnect(false);
            }
            return false;
        }

        internal void StartProtocol()
        {
            protocolThread.Start();
        }

        private bool TestFilter(BloomFilter filter, Transaction tx)
        {
            if (filter.Check(tx.Hash.ToArray())) return true;
            if (tx.Outputs.Any(p => filter.Check(p.ScriptHash.ToArray()))) return true;
            if (tx.Inputs.Any(p => filter.Check(p.ToArray()))) return true;
            if (tx.Scripts.Any(p => filter.Check(p.RedeemScript.ToScriptHash().ToArray())))
                return true;
            if (tx.Type == TransactionType.RegisterTransaction)
            {
                RegisterTransaction asset = (RegisterTransaction)tx;
                if (filter.Check(asset.Admin.ToArray())) return true;
            }
            return false;
        }
    }
}