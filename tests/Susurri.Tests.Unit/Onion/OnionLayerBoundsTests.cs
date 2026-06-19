using Shouldly;
using Susurri.Modules.DHT.Core.Onion;
using Xunit;

namespace Susurri.Tests.Unit.Onion;

public class OnionLayerBoundsTests
{
    [Fact]
    public void Deserialize_HugeCiphertextLength_DoesNotAllocate_Throws()
    {
        // pubKeyLen=32, 32 bytes; nonceLen=12, 12 bytes; ciphertextLen=int.MaxValue.
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)32);
        writer.Write(new byte[32]);
        writer.Write((byte)12);
        writer.Write(new byte[12]);
        writer.Write(int.MaxValue);
        writer.Write(new byte[8]);

        Should.Throw<InvalidDataException>(() => OnionLayer.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void Deserialize_WrongPublicKeyLength_Throws()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)16);
        writer.Write(new byte[16]);
        writer.Write((byte)12);
        writer.Write(new byte[12]);
        writer.Write(4);
        writer.Write(new byte[4]);

        Should.Throw<InvalidDataException>(() => OnionLayer.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void Deserialize_NonceTooLong_Throws()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)32);
        writer.Write(new byte[32]);
        writer.Write((byte)200);
        writer.Write(new byte[200]);
        writer.Write(4);
        writer.Write(new byte[4]);

        Should.Throw<InvalidDataException>(() => OnionLayer.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void Deserialize_TruncatedCiphertext_Throws()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)32);
        writer.Write(new byte[32]);
        writer.Write((byte)12);
        writer.Write(new byte[12]);
        writer.Write(1024);
        writer.Write(new byte[10]);

        Should.Throw<InvalidDataException>(() => OnionLayer.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void Deserialize_ValidLayer_RoundTrips()
    {
        var layer = new OnionLayer
        {
            EphemeralPublicKey = new byte[32],
            Nonce = new byte[12],
            Ciphertext = new byte[64]
        };

        var restored = OnionLayer.Deserialize(layer.Serialize());

        restored.EphemeralPublicKey.Length.ShouldBe(32);
        restored.Nonce.Length.ShouldBe(12);
        restored.Ciphertext.Length.ShouldBe(64);
    }

    [Fact]
    public void OnionLayerContent_UnknownType_Throws()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)0x7F);
        writer.Write(0);
        writer.Write(0);

        Should.Throw<InvalidDataException>(() => OnionLayerContent.Deserialize(ms.ToArray()));
    }

    [Fact]
    public void OnionLayerContent_HugeInnerPayload_Throws()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((byte)OnionLayerType.Delivery);
        writer.Write(0);
        writer.Write(int.MaxValue);
        writer.Write(new byte[8]);

        Should.Throw<InvalidDataException>(() => OnionLayerContent.Deserialize(ms.ToArray()));
    }
}
