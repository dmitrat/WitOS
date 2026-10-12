using MessagePack;
using MessagePack.Resolvers;
using NUnit.Framework;

namespace RepresentativeLibraries;

/// <summary>
/// MessagePack-CSharp: contract types through its standard resolver, anonymous data through the contractless one, which
/// emits its formatters at run time, and LZ4 compression.
/// </summary>
[TestFixture]
public sealed class MessagePackTests
{
    [Test]
    public void ContractRoundTripTest()
    {
        var order = new Order { Id = 42, Customer = "WitOS", Lines = [new Line { Item = "disk", Quantity = 2 }, new Line { Item = "cable", Quantity = 5 }] };

        var bytes = MessagePackSerializer.Serialize(order);
        var back = MessagePackSerializer.Deserialize<Order>(bytes);

        Assert.That(back.Id, Is.EqualTo(42));
        Assert.That(back.Customer, Is.EqualTo("WitOS"));
        Assert.That(back.Lines.Select(line => (line.Item, line.Quantity)), Is.EqualTo(new[] { ("disk", 2), ("cable", 5) }));
    }

    [Test]
    public void ContractlessRoundTripTest()
    {
        var options = ContractlessStandardResolver.Options;
        var value = new Dictionary<string, object> { ["name"] = "volume", ["size"] = 22_020_096L, ["mounted"] = true };

        var back = MessagePackSerializer.Deserialize<Dictionary<string, object>>(MessagePackSerializer.Serialize(value, options), options);

        Assert.That(back["name"], Is.EqualTo("volume"));
        Assert.That(Convert.ToInt64(back["size"]), Is.EqualTo(22_020_096L));
        Assert.That(back["mounted"], Is.EqualTo(true));
    }

    [Test]
    public void CompressionTest()
    {
        var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
        var values = Enumerable.Repeat("a repeated line of text", 1000).ToArray();

        var bytes = MessagePackSerializer.Serialize(values, options);
        var back = MessagePackSerializer.Deserialize<string[]>(bytes, options);

        Assert.That(bytes.Length, Is.LessThan(values.Sum(value => value.Length) / 10));
        Assert.That(back, Is.EqualTo(values));
    }

    [MessagePackObject]
    public sealed class Order
    {
        [Key(0)]
        public int Id { get; set; }

        [Key(1)]
        public string Customer { get; set; } = "";

        [Key(2)]
        public List<Line> Lines { get; set; } = [];
    }

    [MessagePackObject]
    public sealed class Line
    {
        [Key(0)]
        public string Item { get; set; } = "";

        [Key(1)]
        public int Quantity { get; set; }
    }
}
