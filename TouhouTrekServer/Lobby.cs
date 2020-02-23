using System;
using System.IO;
using System.Net;
using System.Xml;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using TouhouTrek.Server.Shared;

namespace TouhouTrek.Server
{
    class Room
    {
        public int id { get; }
        public NetPeer host { get; }
        NetPeer[] clientArray { get; } = new NetPeer[8];
        public Room(int id, NetPeer host)
        {
            this.id = id;
            this.host = host;
            clientArray[0] = host;
        }
        public int addClient(NetPeer client)
        {
            int emptySeat = -1;
            for (int i = 0; i < clientArray.Length; i++)
            {
                if (emptySeat < 0 && clientArray[i] == null)
                {
                    emptySeat = i;
                }
                else if (clientArray[i] == client)
                {
                    Console.WriteLine(client.EndPoint.Address.ToString() + "已经在房间" + id + "中了");
                    return -1;
                }
            }
            if (emptySeat < 0)
            {
                Console.WriteLine(client.EndPoint.Address.ToString() + "加入房间" + id + "失败，目标房间已满");
                return -1;
            }
            else
            {
                clientArray[emptySeat] = client;
                return emptySeat;
            }
        }
        public bool removeClient(NetPeer client)
        {
            for (int i = 0; i < clientArray.Length; i++)
            {
                if (clientArray[i] == client)
                {
                    clientArray[i] = null;
                    return true;
                }
            }
            return false;
        }
        public NetPeer[] getClients()
        {
            return clientArray.Where(c => c != null).ToArray();
        }
        public NetPeer getClientById(int id)
        {
            if (-1 < id && id < clientArray.Length)
                return clientArray[id];
            else
                return null;
        }
        public int getClientID(NetPeer client)
        {
            for (int i = 0; i < clientArray.Length; i++)
            {
                if (clientArray[i] == client)
                    return i;
            }
            return -1;
        }
    }
    class Lobby : INetEventListener
    {
        NetManager net { get; set; } = null;
        CancellationTokenSource cts { get; } = new CancellationTokenSource();
        public void start()
        {
            net = new NetManager(this)
            {
                UnconnectedMessagesEnabled = true
            };
            int port = 9050;
            FileInfo configFile = new FileInfo("Config.xml");
            if (configFile.Exists)
            {
                using (FileStream stream = configFile.Open(FileMode.Open))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(stream);
                    int.TryParse(doc["Config"]["Port"].InnerText, out port);
                }
            }
            net.Start(port);
            Console.WriteLine("运行服务端，端口：" + port);
            Task.Run(() =>
            {
                while (true)
                {
                    net.PollEvents();
                }
            }, cts.Token);
        }
        public void OnConnectionRequest(ConnectionRequest request)
        {
            //TODO:验证一下，不要随便什么人都接进来。
            request.Accept();
        }
        public void OnPeerConnected(NetPeer peer)
        {
            Console.WriteLine(peer.EndPoint.Address.ToString() + "进入了大厅");
        }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            PacketType head = (PacketType)reader.GetInt();
            switch (head)
            {
                case PacketType.discoveryRequest://DiscoveryRequest
                    Console.WriteLine("收到来自" + remoteEndPoint.Address + "的发现请求");
                    NetDataWriter writer = new NetDataWriter();
                    writer.Put((int)PacketType.discoveryResponse);//DiscoveryResponse
                    writer.Put(roomDic.Count);
                    foreach (Room room in roomDic.Values)
                    {
                        writer.Put(room.host.EndPoint.Address.ToString());
                    }
                    net.SendUnconnectedMessage(writer, remoteEndPoint);
                    break;
                default:
                    break;
            }
        }
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            PacketType head = (PacketType)reader.GetInt();
            switch (head)
            {
                case PacketType.createGameRequest:
                    Room room = createRoom(peer);
                    Console.WriteLine(peer.EndPoint.Address.ToString() + "创建了房间" + room.id);
                    NetDataWriter writer = new NetDataWriter();
                    writer.Put((int)PacketType.createGameResponse);
                    writer.Put(room.id);
                    writer.Put(0);//主机第一个进房间，Id理所应当的是0。
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    break;
                case PacketType.joinGameRequest:
                    string address = reader.GetString();
                    room = getRoomByAddress(address);
                    int clientID = room.addClient(peer);
                    writer = new NetDataWriter();
                    writer.Put((int)PacketType.joinGameResponse);
                    if (room == null)
                    {
                        Console.WriteLine(peer.EndPoint.Address.ToString() + "请求加入房间" + address.ToString() + "失败，没有找到房间");
                        writer.Put(0);
                        writer.Put("RoomNotFound");
                        peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    }
                    else if (clientID < 0)
                    {
                        writer.Put(room.id);
                        writer.Put(clientID);
                        peer.Send(writer, DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        Console.WriteLine(peer.EndPoint.Address.ToString() + "加入房间" + room.id);
                        writer.Put(room.id);
                        writer.Put(clientID);
                        peer.Send(writer, DeliveryMethod.ReliableOrdered);
                        writer.Reset();
                        writer.Put((int)PacketType.joinGameRequest);
                        writer.Put(room.id);
                        writer.Put(clientID);
                        room.host.Send(writer, DeliveryMethod.ReliableOrdered);
                    }
                    break;
                case PacketType.quitGameRequest:
                    int roomID = reader.GetInt();
                    room = getRoomByID(roomID);
                    if (room != null)
                    {
                        if (peer == room.host)
                            destroyRoom(room);
                        else
                        {
                            clientID = room.getClientID(peer);
                            if (clientID > -1)
                            {
                                Console.WriteLine("玩家" + peer.EndPoint.Address.ToString() + "退出房间" + roomID);
                                writer = new NetDataWriter();
                                writer.Put((int)PacketType.quitGameResponse);
                                writer.Put(room.id);
                                writer.Put(clientID);
                                room.host.Send(writer, DeliveryMethod.ReliableOrdered);
                                room.removeClient(peer);
                            }
                            else
                                Console.WriteLine("房间" + roomID + "中不存在玩家" + peer.EndPoint.Address.ToString());
                        }
                    }
                    break;
                case PacketType.clientSend:
                    roomID = reader.GetInt();
                    room = getRoomByID(roomID);
                    if (room != null)
                    {
                        clientID = reader.GetInt();
                        writer = new NetDataWriter();
                        writer.Put((int)PacketType.clientSend);
                        writer.Put(roomID);
                        writer.Put(clientID);
                        string type = reader.GetString();
                        string json = reader.GetString();
                        writer.Put(type);
                        writer.Put(json);
                        room.host.Send(writer, DeliveryMethod.ReliableOrdered);
                        Console.WriteLine("房间" + roomID + "转发客户端" + clientID + "的单发给主机" + "(" + type + ")");
                    }
                    break;
                case PacketType.serverSend:
                    roomID = reader.GetInt();
                    room = getRoomByID(roomID);
                    if (room != null)
                    {
                        clientID = reader.GetInt();
                        NetPeer client = room.getClientById(clientID);
                        if (client != null)
                        {
                            writer = new NetDataWriter();
                            writer.Put((int)PacketType.serverSend);
                            writer.Put(roomID);
                            writer.Put(clientID);
                            string type = reader.GetString();
                            string json = reader.GetString();
                            writer.Put(type);
                            writer.Put(json);
                            client.Send(writer, DeliveryMethod.ReliableOrdered);
                            Console.WriteLine("房间" + roomID + "转发主机" + room.getClientID(peer) + "的单发给客户端" + room.getClientID(client) + "(" + type + ")");
                        }
                        else
                            Console.WriteLine("房间" + room.id + "中不存在玩家" + clientID);
                    }
                    break;
                case PacketType.serverBroadcast:
                    roomID = reader.GetInt();
                    room = getRoomByID(roomID);
                    if (room != null)
                    {
                        clientID = reader.GetInt();
                        string type = reader.GetString();
                        string json = reader.GetString();
                        foreach (NetPeer client in room.getClients())
                        {
                            writer = new NetDataWriter();
                            writer.Put((int)PacketType.serverBroadcast);
                            writer.Put(roomID);
                            writer.Put(clientID);
                            writer.Put(type);
                            writer.Put(json);
                            client.Send(writer, DeliveryMethod.ReliableOrdered);
                            Console.WriteLine("房间" + roomID + "转发主机" + room.getClientID(peer) + "的广播给客户端" + room.getClientID(client) + "(" + type + ")");
                        }
                    }
                    break;
                default:
                    break;
            }
        }
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
        }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine(peer.EndPoint.Address.ToString() + "退出了大厅");
            Room destroyRoom = null;
            foreach (Room room in roomDic.Values)
            {
                if (room.host == peer)
                {
                    destroyRoom = room;
                    break;
                }
                else
                {
                    foreach (NetPeer client in room.getClients())
                    {
                        if (client == peer)
                        {
                            Console.WriteLine("玩家" + peer.EndPoint.Address.ToString() + "退出房间" + room.id);
                            NetDataWriter writer = new NetDataWriter();
                            writer.Put((int)PacketType.quitGameResponse);
                            writer.Put(room.id);
                            writer.Put(room.getClientID(client));
                            room.host.Send(writer, DeliveryMethod.ReliableOrdered);
                            room.removeClient(peer);
                            return;
                        }
                    }
                }
            }
            if (destroyRoom != null)
            {
                this.destroyRoom(destroyRoom);
            }
        }
        public void stop()
        {
            Console.WriteLine("停止运行服务端");
            cts.Cancel();
            net.Stop();
        }
        Dictionary<int, Room> roomDic { get; } = new Dictionary<int, Room>();
        int allocatedRoomID { get; set; } = 0;
        Stack<int> deallocatedRoomIDStack { get; } = new Stack<int>();
        Room createRoom(NetPeer peer)
        {
            int id;
            if (deallocatedRoomIDStack.Count > 1)
                id = deallocatedRoomIDStack.Pop();
            else
            {
                allocatedRoomID++;
                id = allocatedRoomID;
            }
            Room room = new Room(id, peer);
            roomDic.Add(room.id, room);
            return room;
        }
        Room getRoomByAddress(string address)
        {
            foreach (Room room in roomDic.Values)
            {
                if (room.host.EndPoint.Address.ToString() == address)
                    return room;
            }
            return null;
        }
        Room getRoomByID(int id)
        {
            return roomDic.ContainsKey(id) ? roomDic[id] : null;
        }
        void destroyRoom(Room room)
        {
            Console.WriteLine("房间" + room.id + "被关闭");
            foreach (NetPeer client in room.getClients())
            {
                client.Disconnect();
            }
            if (roomDic.Remove(room.id))
                deallocatedRoomIDStack.Push(room.id);
        }
    }
}
