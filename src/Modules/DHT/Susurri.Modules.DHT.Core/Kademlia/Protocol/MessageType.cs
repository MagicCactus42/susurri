namespace Susurri.Modules.DHT.Core.Kademlia.Protocol;

public enum MessageType : byte
{
    Ping = 0x01,
    Pong = 0x02,
    FindNode = 0x03,
    FindNodeResponse = 0x04,
    FindValue = 0x05,
    FindValueResponse = 0x06,
    Store = 0x07,
    StoreResponse = 0x08,
    OnionMessage = 0x10,
    DirectMessage = 0x11,
    StoreOfflineMessage = 0x12,
    GetOfflineMessages = 0x13,
    OfflineMessagesResponse = 0x14
}
