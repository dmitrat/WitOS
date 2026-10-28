using System;
using System.Text;
using OutWit.OS.FileSystems.Smb;

namespace OutWit.OS.Tests.FileSystems.Smb;

/// <summary>
/// What the SMB client computes by hand (plan step E2), against the published vectors: MD4 (RFC 1320 A.5), RC4, AES-CMAC
/// (RFC 4493 4), NTLMv2's key, proof and session key (MS-NLMP 4.2.4), and the SPNEGO tokens read back.
/// </summary>
[TestFixture]
public sealed class SmbCryptoTests
{
    #region Tests

    [TestCase("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [TestCase("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [TestCase("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [TestCase("message digest", "d9130a8164549fe818874806e1c7014b")]
    [TestCase("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4")]
    [TestCase("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Md4MatchesRfc1320Test(string message, string digest) =>
        Assert.That(Convert.ToHexStringLower(Md4.Hash(Encoding.ASCII.GetBytes(message))), Is.EqualTo(digest));

    [TestCase("Key", "Plaintext", "BBF316E8D940AF0AD3")]
    [TestCase("Wiki", "pedia", "1021BF0420")]
    [TestCase("Secret", "Attack at dawn", "45A01F645FC35B383552544B9BF5")]
    public void Rc4MatchesItsVectorsTest(string key, string plain, string cipher)
    {
        Assert.That(Convert.ToHexString(Rc4.Transform(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(plain))), Is.EqualTo(cipher));

        // A stream continues where its last call ended.
        var stream = new Rc4(Encoding.ASCII.GetBytes(key));
        var bytes = Encoding.ASCII.GetBytes(plain);
        stream.Transform(bytes.AsSpan(0, 2));
        stream.Transform(bytes.AsSpan(2));
        Assert.That(Convert.ToHexString(bytes), Is.EqualTo(cipher));
    }

    [TestCase(0, "bb1d6929e95937287fa37d129b756746")]
    [TestCase(16, "070a16b46b4d4144f79bdd9dd04a287c")]
    [TestCase(40, "dfa66747de9ae63030ca32611497c827")]
    [TestCase(64, "51f0bebf7e3b9d92fc49741779363cfe")]
    public void AesCmacMatchesRfc4493Test(int length, string tag)
    {
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var message = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        using var cmac = new AesCmac(key);
        Assert.That(Convert.ToHexStringLower(cmac.Compute(message.AsSpan(0, length))), Is.EqualTo(tag));
    }

    // MS-NLMP 4.2.4: User, Domain, Password; the server's challenge 0123456789abcdef, the client's aa..aa, time zero, the
    // target information of the domain and server names.
    [Test]
    public void NtlmV2MatchesMsNlmpTest()
    {
        Assert.That(Convert.ToHexStringLower(NtlmClient.NtHash("Password")), Is.EqualTo("a4f49c406510bdcab6824ee7c30fd852"));
        var responseKey = NtlmClient.NtOwfV2("Password", "User", "Domain");
        Assert.That(Convert.ToHexStringLower(responseKey), Is.EqualTo("0c868a403bfd7a93a3001ef22ef02e3f"));

        // MsvAvNbDomainName "Domain", MsvAvNbComputerName "Server", MsvAvEOL.
        var targetInfo = Convert.FromHexString("02000c00" + "44006f006d00610069006e00" + "01000c00" + "530065007200760065007200" + "00000000");
        var temp = NtlmClient.Temp(0, Convert.FromHexString("aaaaaaaaaaaaaaaa"), targetInfo);
        Assert.That(temp, Has.Length.EqualTo(68));
        var proof = NtlmClient.ProofString(responseKey, Convert.FromHexString("0123456789abcdef"), temp);
        Assert.That(Convert.ToHexStringLower(proof), Is.EqualTo("68cd0ab851e51c96aabc927bebef6a1c"));
        Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.HMACMD5.HashData(responseKey, proof)),
            Is.EqualTo("8de40ccadbc14a82f15cb0ad0de95ca3"), "the session base key");
    }

    // A server's negTokenResp carries NTLM's message as its responseToken; the client's initial token offers NTLM alone.
    [Test]
    public void SpnegoTokensReadBackTest()
    {
        var challenge = new byte[300];
        new Random(5).NextBytes(challenge);
        Assert.That(Spnego.ReadResponseToken(Spnego.ResponseToken(challenge, null)), Is.EqualTo(challenge));
        Assert.That(Spnego.ReadResponseToken(Spnego.ResponseToken(challenge, new byte[16])), Is.EqualTo(challenge), "a mechListMIC after the token");

        // The server's answer that accepts with nothing more: negState accept-completed alone.
        Assert.That(Spnego.ReadResponseToken(Convert.FromHexString("a1073005a0030a0100")), Is.Null);

        var initial = Spnego.InitialToken([1, 2, 3]);
        Assert.That(initial[0], Is.EqualTo(0x60));
        Assert.That(Convert.ToHexStringLower(initial), Does.Contain(Convert.ToHexStringLower(Spnego.MechanismList)));
        Assert.That(Convert.ToHexStringLower(Spnego.MechanismList), Is.EqualTo("300c060a2b06010401823702020a"));
    }

    #endregion
}
