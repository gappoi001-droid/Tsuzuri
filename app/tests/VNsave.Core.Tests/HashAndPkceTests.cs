using System.Security.Cryptography;
using System.Text.Json;

namespace VNsave.Core.Tests;

public sealed class HashAndPkceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(4 * 1024 * 1024 + 1)]
    public async Task ContentHashMatchesIndependentReference(int size)
    {
        byte[] data = new byte[size];
        new Random(1234).NextBytes(data);
        using var input = new MemoryStream(data);
        var actual = await FileHash.ComputeAsync(input);
        Assert.Equal(ReferenceHash(data), actual.ContentHash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), actual.Sha256);
        Assert.Equal(size, actual.Size);
        if (size == 0) Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", actual.ContentHash);
    }

    [Fact]
    public async Task ShortStreamReadsDoNotChangeBlockBoundaries()
    {
        byte[] data = new byte[4 * 1024 * 1024 + 1];
        new Random(15).NextBytes(data);
        using var input = new ShortReadStream(data);
        Assert.Equal(ReferenceHash(data), (await FileHash.ComputeAsync(input)).ContentHash);
    }

    [Fact]
    public void PkceMatchesRfc7636AppendixB() => Assert.Equal(
        "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
        Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void PkceVerifierHasAllowedLengthAndAlphabet()
    {
        var values = Enumerable.Range(0, 100).Select(_ => Pkce.GenerateVerifier()).ToArray();
        foreach (string value in values)
        {
            Assert.InRange(value.Length, 43, 128);
            Assert.Matches("^[A-Za-z0-9._~-]+$", value);
        }
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void AuthorizationUsesOfflinePkceAndExactRedirect()
    {
        var uri = Pkce.AuthorizationUri(new AppSettings { AppKey = "testkey", Scopes = ["files.content.read"] },
            "http://127.0.0.1:53134/callback", "expected-state", "challenge");
        Assert.Equal("www.dropbox.com", uri.Host);
        Assert.Contains("code_challenge_method=S256", uri.Query);
        Assert.Contains("token_access_type=offline", uri.Query);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A53134%2Fcallback", uri.Query);
    }

    [Fact]
    public void HeaderJsonEscapesJapaneseAndDel()
    {
        const string path = "/日本語テスト\u007f.txt";
        string argument = JsonFormat.ApiArgument(new { path });
        Assert.All(argument, c => Assert.InRange((int)c, 0, 126));
        using var document = JsonDocument.Parse(argument);
        Assert.Equal(path, document.RootElement.GetProperty("path").GetString());
        Assert.Contains("\\u007F", argument);
    }

    internal static string ReferenceHash(byte[] data)
    {
        using var concatenated = new MemoryStream();
        for (int offset = 0; offset < data.Length; offset += 4_194_304)
            concatenated.Write(SHA256.HashData(data.AsSpan(offset, Math.Min(4_194_304, data.Length - offset))));
        return Convert.ToHexStringLower(SHA256.HashData(concatenated.ToArray()));
    }

    private sealed class ShortReadStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(1337, buffer.Length)], ct);
    }
}
