using System.Buffers.Binary;
using System.Text;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// The host's change of a flattened device tree (plan step H7.2): a property set in /chosen before the node's other
/// properties, an old value replaced, the node added where the tree has none, and the rest of the tree — the memory
/// reservations, the other nodes and their properties — kept, as a reader of the format sees it.
/// </summary>
[TestFixture]
public sealed class DeviceTreeBlobTests
{
    #region Functions

    [Test]
    public void SeedJoinsChosenBeforeItsOtherPropertiesTest()
    {
        var tree = Tree(withChosen: true);
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var changed = DeviceTreeBlob.WithChosenProperty(tree, "rng-seed", seed);
        var properties = Properties(changed);
        Assert.That(properties["/chosen"].Select(p => p.Name), Is.EqualTo(new[] { "rng-seed", "bootargs" }));
        Assert.That(properties["/chosen"][0].Value, Is.EqualTo(seed));
        Assert.That(Encoding.ASCII.GetString(properties["/chosen"][1].Value), Is.EqualTo("witos\0"));
        Assert.That(properties["/memory@0"].Single(p => p.Name == "reg").Value, Is.EqualTo(Properties(tree)["/memory@0"].Single().Value));
        Assert.That(properties["/"].Select(p => p.Name), Is.EqualTo(new[] { "#address-cells" }));
        Assert.That(Reservations(changed), Is.EqualTo(new[] { (0UL, 0x1000UL) }));
    }

    [Test]
    public void SeedReplacesTheOldOneTest()
    {
        var once = DeviceTreeBlob.WithChosenProperty(Tree(withChosen: true), "rng-seed", [1, 2, 3, 4]);
        var twice = DeviceTreeBlob.WithChosenProperty(once, "rng-seed", [5, 6, 7, 8, 9]);
        var chosen = Properties(twice)["/chosen"];
        Assert.That(chosen.Count(p => p.Name == "rng-seed"), Is.EqualTo(1));
        Assert.That(chosen.Single(p => p.Name == "rng-seed").Value, Is.EqualTo(new byte[] { 5, 6, 7, 8, 9 }));
    }

    [Test]
    public void ChosenIsAddedWhereTheTreeHasNoneTest()
    {
        var changed = DeviceTreeBlob.WithChosenProperty(Tree(withChosen: false), "rng-seed", [9, 9, 9, 9]);
        var properties = Properties(changed);
        Assert.That(properties["/chosen"].Single().Name, Is.EqualTo("rng-seed"));
        Assert.That(properties.Keys, Is.EquivalentTo(new[] { "/", "/memory@0", "/chosen" }));
    }

    [Test]
    public void NotATreeIsRefusedTest()
    {
        Assert.Throws<InvalidDataException>(() => DeviceTreeBlob.Require(new byte[64]));
    }

    // A tree of the root (#address-cells), /memory@0 (reg) and, when asked, /chosen (bootargs), with one reservation.
    private static byte[] Tree(bool withChosen)
    {
        var strings = new List<byte>();
        uint Name(string name)
        {
            var offset = (uint)strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(name + "\0"));
            return offset;
        }
        var structure = new List<byte>();
        void Word(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            structure.AddRange(bytes);
        }
        void Begin(string name)
        {
            Word(1);
            var bytes = Encoding.ASCII.GetBytes(name + "\0");
            structure.AddRange(bytes);
            structure.AddRange(new byte[(4 - bytes.Length % 4) % 4]);
        }
        void Property(string name, byte[] value)
        {
            Word(3);
            Word((uint)value.Length);
            Word(Name(name));
            structure.AddRange(value);
            structure.AddRange(new byte[(4 - value.Length % 4) % 4]);
        }
        Begin("");
        Property("#address-cells", [0, 0, 0, 2]);
        Begin("memory@0");
        Property("reg", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10, 0]);
        Word(2);
        if (withChosen)
        {
            Begin("chosen");
            Property("bootargs", Encoding.ASCII.GetBytes("witos\0"));
            Word(2);
        }
        Word(2);
        Word(9);
        var reservations = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(reservations.AsSpan(8), 0x1000);
        var blob = new byte[40 + reservations.Length + structure.Count + strings.Count];
        uint[] header = [0xD00DFEED, (uint)blob.Length, (uint)(40 + reservations.Length), (uint)(40 + reservations.Length + structure.Count), 40, 17, 16, 0,
            (uint)strings.Count, (uint)structure.Count];
        for (var i = 0; i < header.Length; ++i)
            BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(i * 4), header[i]);
        reservations.CopyTo(blob, 40);
        structure.CopyTo(blob, 40 + reservations.Length);
        strings.CopyTo(blob, 40 + reservations.Length + structure.Count);
        return blob;
    }

    // Every node's properties by its path, in their order, as a reader of the format walks the structure.
    private static Dictionary<string, List<(string Name, byte[] Value)>> Properties(byte[] blob)
    {
        uint Word(int at) => BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at));
        var structure = (int)Word(8);
        var strings = (int)Word(12);
        var result = new Dictionary<string, List<(string, byte[])>>();
        var path = new List<string>();
        for (var at = structure; ;)
        {
            var token = Word(at);
            at += 4;
            if (token == 1)
            {
                var end = Array.IndexOf(blob, (byte)0, at);
                path.Add(Encoding.ASCII.GetString(blob, at, end - at));
                at = (end + 4) & ~3;
                result[Path(path)] = [];
            }
            else if (token == 2)
            {
                path.RemoveAt(path.Count - 1);
            }
            else if (token == 3)
            {
                var length = (int)Word(at);
                var nameAt = strings + (int)Word(at + 4);
                var name = Encoding.ASCII.GetString(blob, nameAt, Array.IndexOf(blob, (byte)0, nameAt) - nameAt);
                result[Path(path)].Add((name, blob[(at + 8)..(at + 8 + length)]));
                at = (at + 8 + length + 3) & ~3;
            }
            else if (token == 9)
            {
                return result;
            }
            else if (token != 4)
            {
                throw new InvalidDataException($"token {token}");
            }
        }
    }

    private static string Path(List<string> path) => path.Count == 1 ? "/" : "/" + string.Join('/', path.Skip(1));

    private static (ulong, ulong)[] Reservations(byte[] blob)
    {
        var list = new List<(ulong, ulong)>();
        for (var at = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(16)); ; at += 16)
        {
            var address = BinaryPrimitives.ReadUInt64BigEndian(blob.AsSpan(at));
            var size = BinaryPrimitives.ReadUInt64BigEndian(blob.AsSpan(at + 8));
            if (address == 0 && size == 0)
                return list.ToArray();
            list.Add((address, size));
        }
    }

    #endregion
}
