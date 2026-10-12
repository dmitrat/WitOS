using MemoryPack;
using Xunit;

namespace RepresentativeLibraries;

/// <summary>
/// MemoryPack: types its source generator made formatters for, unions, and versions that add members.
/// </summary>
public sealed class MemoryPackTests
{
    [Fact]
    public void RoundTripTest()
    {
        var disk = new Disk { Device = "pci/00:04.0", Bytes = 33_554_432, Partitions = ["WitOS test", "WitOS FAT", "WitOS ext4"] };

        var back = MemoryPackSerializer.Deserialize<Disk>(MemoryPackSerializer.Serialize(disk));

        Assert.NotNull(back);
        Assert.Equal(disk.Device, back.Device);
        Assert.Equal(disk.Bytes, back.Bytes);
        Assert.Equal(disk.Partitions, back.Partitions);
    }

    [Fact]
    public void UnionTest()
    {
        IVolume[] volumes = [new Fat { Clusters = 4096 }, new Ext4 { Journal = true }];

        var back = MemoryPackSerializer.Deserialize<IVolume[]>(MemoryPackSerializer.Serialize(volumes));

        Assert.NotNull(back);
        Assert.Equal(4096, Assert.IsType<Fat>(back[0]).Clusters);
        Assert.True(Assert.IsType<Ext4>(back[1]).Journal);
    }

    [Fact]
    public void VersionToleranceTest()
    {
        var bytes = MemoryPackSerializer.Serialize(new Version1 { Name = "first" });

        var back = MemoryPackSerializer.Deserialize<Version2>(bytes);

        Assert.NotNull(back);
        Assert.Equal("first", back.Name);
        Assert.Equal(0, back.Added);
    }
}

[MemoryPackable]
public sealed partial class Disk
{
    public string Device { get; set; } = "";

    public long Bytes { get; set; }

    public string[] Partitions { get; set; } = [];
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(Fat))]
[MemoryPackUnion(1, typeof(Ext4))]
public partial interface IVolume;

[MemoryPackable]
public sealed partial class Fat : IVolume
{
    public int Clusters { get; set; }
}

[MemoryPackable]
public sealed partial class Ext4 : IVolume
{
    public bool Journal { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class Version1
{
    [MemoryPackOrder(0)]
    public string Name { get; set; } = "";
}

[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class Version2
{
    [MemoryPackOrder(0)]
    public string Name { get; set; } = "";

    [MemoryPackOrder(1)]
    public int Added { get; set; }
}
