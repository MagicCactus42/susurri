using System.Text;
using Shouldly;
using Susurri.Modules.DHT.Core.Kademlia;
using Susurri.Modules.DHT.Core.Kademlia.Storage;
using Xunit;

namespace Susurri.Tests.Unit.Kademlia;

public class DhtStorageTests
{
    private readonly DhtStorage _sut = new();

    [Fact]
    public void Store_Get_RoundTrip_Succeeds()
    {
        // Arrange
        var key = KademliaId.Random();
        var value = Encoding.UTF8.GetBytes("test value");

        // Act
        _sut.Store(key, value);
        var retrieved = _sut.Get(key);

        // Assert
        retrieved.ShouldBe(value);
    }

    [Fact]
    public void Get_NonExistentKey_ReturnsNull()
    {
        // Arrange
        var key = KademliaId.Random();

        // Act
        var result = _sut.Get(key);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void Contains_StoredKey_ReturnsTrue()
    {
        // Arrange
        var key = KademliaId.Random();
        _sut.Store(key, new byte[] { 1, 2, 3 });

        // Act & Assert
        _sut.Contains(key).ShouldBeTrue();
    }

    [Fact]
    public void Contains_NonExistentKey_ReturnsFalse()
    {
        // Arrange
        var key = KademliaId.Random();

        // Act & Assert
        _sut.Contains(key).ShouldBeFalse();
    }

    [Fact]
    public void Remove_ExistingKey_ReturnsTrue()
    {
        // Arrange
        var key = KademliaId.Random();
        _sut.Store(key, new byte[] { 1, 2, 3 });

        // Act
        var result = _sut.Remove(key);

        // Assert
        result.ShouldBeTrue();
        _sut.Contains(key).ShouldBeFalse();
    }

    [Fact]
    public void Remove_NonExistentKey_ReturnsFalse()
    {
        // Arrange
        var key = KademliaId.Random();

        // Act
        var result = _sut.Remove(key);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void Store_OverwritesExistingValue()
    {
        // Arrange
        var key = KademliaId.Random();
        var value1 = new byte[] { 1, 2, 3 };
        var value2 = new byte[] { 4, 5, 6 };

        // Act
        _sut.Store(key, value1);
        _sut.Store(key, value2);
        var retrieved = _sut.Get(key);

        // Assert
        retrieved.ShouldBe(value2);
    }

    [Fact]
    public void StoreOfflineMessage_GetOfflineMessages_RoundTrip()
    {
        // Arrange
        var recipientKey = KademliaId.Random();
        var message1 = Encoding.UTF8.GetBytes("message 1");
        var message2 = Encoding.UTF8.GetBytes("message 2");

        // Act
        _sut.StoreOfflineMessage(recipientKey, message1);
        _sut.StoreOfflineMessage(recipientKey, message2);
        var messages = _sut.GetOfflineMessages(recipientKey);

        // Assert
        messages.Count.ShouldBe(2);
        messages[0].ShouldBe(message1);
        messages[1].ShouldBe(message2);
    }

    [Fact]
    public void GetOfflineMessages_RemovesMessages()
    {
        // Arrange
        var recipientKey = KademliaId.Random();
        _sut.StoreOfflineMessage(recipientKey, new byte[] { 1 });

        // Act
        _sut.GetOfflineMessages(recipientKey);
        var secondGet = _sut.GetOfflineMessages(recipientKey);

        // Assert
        secondGet.Count.ShouldBe(0);
    }

    [Fact]
    public void GetOfflineMessageCount_ReturnsCorrectCount()
    {
        // Arrange
        var recipientKey = KademliaId.Random();
        _sut.StoreOfflineMessage(recipientKey, new byte[] { 1 });
        _sut.StoreOfflineMessage(recipientKey, new byte[] { 2 });
        _sut.StoreOfflineMessage(recipientKey, new byte[] { 3 });

        // Act
        var count = _sut.GetOfflineMessageCount(recipientKey);

        // Assert
        count.ShouldBe(3);
    }

    [Fact]
    public void GetAllForRepublish_ReturnsStoredValues()
    {
        // Arrange
        var key1 = KademliaId.Random();
        var key2 = KademliaId.Random();
        var value1 = new byte[] { 1, 2, 3 };
        var value2 = new byte[] { 4, 5, 6 };

        _sut.Store(key1, value1);
        _sut.Store(key2, value2);

        // Act
        var all = _sut.GetAllForRepublish().ToList();

        // Assert
        all.Count.ShouldBe(2);
    }

    [Fact]
    public void GetStats_ReturnsCorrectStats()
    {
        // Arrange
        _sut.Store(KademliaId.Random(), new byte[100]);
        _sut.Store(KademliaId.Random(), new byte[200]);
        _sut.StoreOfflineMessage(KademliaId.Random(), new byte[50]);

        // Act
        var stats = _sut.GetStats();

        // Assert
        stats.ValueCount.ShouldBe(2);
        stats.OfflineMessageCount.ShouldBe(1);
        stats.TotalBytes.ShouldBe(350);
    }

    [Fact]
    public async Task Store_WithTtl_ExpiresAfterDuration()
    {
        // Arrange
        var key = KademliaId.Random();
        var value = new byte[] { 1, 2, 3 };

        // Act
        _sut.Store(key, value, TimeSpan.FromMilliseconds(50));
        var before = _sut.Get(key);

        await Task.Delay(100);

        var after = _sut.Get(key);

        // Assert
        before.ShouldBe(value);
        after.ShouldBeNull();
    }

    [Fact]
    public void Store_MultipleKeys_AllRetrievable()
    {
        // Arrange
        var keys = Enumerable.Range(0, 100).Select(_ => KademliaId.Random()).ToList();
        var values = keys.Select((k, i) => Encoding.UTF8.GetBytes($"value_{i}")).ToList();

        // Act
        for (int i = 0; i < keys.Count; i++)
        {
            _sut.Store(keys[i], values[i]);
        }

        // Assert
        for (int i = 0; i < keys.Count; i++)
        {
            var retrieved = _sut.Get(keys[i]);
            retrieved.ShouldBe(values[i]);
        }
    }
}
