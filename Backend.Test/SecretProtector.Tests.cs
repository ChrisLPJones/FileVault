using Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Test
{
    // SecretProtector holds the authenticator keys and share links' tokens and passwords that are
    // already in people's databases, so these check it still reads values written by earlier
    // versions (the ShareSecrets class it replaced, and itself before that), and keeps each
    // use and context apart.
    public class SecretProtectorTests
    {
        private static readonly Guid ShareId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        // A fixed test key (bytes 1 to 32), only used here
        private static SecretProtector Protector() => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
            })
            .Build());

        [Fact]
        public void ReadsShareLinkSecrets_WrittenByEarlierVersions()
        {
            var protector = Protector();

            protector.TryUnprotectString("V4j3NV1raxRzSIkmnPjCu2FnqQIBS7GNDxWfyuC2D0VLYr/pgur3BfGcu+43gSk=",
                $"share:{ShareId:D}:token", SecretProtector.Purpose.ShareLinks).Should().Be("example-token-value");
            protector.TryUnprotectString("t+Qjf1BRKlrgjTZ2Eb+1JWTAyHjXKV9iPvp59MuUsNZodNfkWHlNaxK+",
                $"share:{ShareId:D}:password", SecretProtector.Purpose.ShareLinks).Should().Be("sunny-beach-42");
        }

        [Fact]
        public void ReadsAuthenticatorSecrets_AndMatchesRecoveryCodeHashes_FromEarlierVersions()
        {
            var protector = Protector();

            Encoding.UTF8.GetString(protector.Unprotect("l2OFDoBkxpVdfr4W7BFkRRHlLkx2Ngu/NAtuHP1JVAr6YEMhw5Di7rfc15w=", "totp:user-123"))
                .Should().Be("JBSWY3DPEHPK3PXP");
            protector.Hash("ABCD-EFGH", "recovery:user-123")
                .Should().Be("3B7F694A2494CC4BFC39E048DE3827DEC1D8B301DA5A60CFA2E9EAEA2681A4AD");
        }

        [Fact]
        public void Values_RoundTrip_AndAreDifferentEachTime()
        {
            var protector = Protector();
            var first = protector.Protect("secret", "ctx", SecretProtector.Purpose.ShareLinks);
            var second = protector.Protect("secret", "ctx", SecretProtector.Purpose.ShareLinks);

            first.Should().NotBe(second);
            protector.TryUnprotectString(first, "ctx", SecretProtector.Purpose.ShareLinks).Should().Be("secret");
            protector.TryUnprotectString(second, "ctx", SecretProtector.Purpose.ShareLinks).Should().Be("secret");
        }

        [Fact]
        public void Values_OnlyOpen_WithTheirOwnContextAndPurpose()
        {
            var protector = Protector();
            var value = protector.Protect("secret", "share:a:token", SecretProtector.Purpose.ShareLinks);

            protector.TryUnprotectString(value, "share:a:password", SecretProtector.Purpose.ShareLinks).Should().BeNull();
            protector.TryUnprotectString(value, "share:b:token", SecretProtector.Purpose.ShareLinks).Should().BeNull();
            protector.TryUnprotectString(value, "share:a:token", SecretProtector.Purpose.AccountSecrets).Should().BeNull();
            protector.Invoking(p => p.Unprotect(value, "share:a:token")).Should().Throw<CryptographicException>();
        }

        [Fact]
        public void TamperedOrMalformedValues_DontOpen()
        {
            var protector = Protector();
            var bytes = Convert.FromBase64String(protector.Protect("secret", "ctx", SecretProtector.Purpose.ShareLinks));
            bytes[^1] ^= 1;

            protector.TryUnprotectString(Convert.ToBase64String(bytes), "ctx", SecretProtector.Purpose.ShareLinks).Should().BeNull();
            protector.TryUnprotectString("not base64!", "ctx", SecretProtector.Purpose.ShareLinks).Should().BeNull();
            protector.TryUnprotectString("c2hvcnQ=", "ctx", SecretProtector.Purpose.ShareLinks).Should().BeNull();
            protector.TryUnprotectString(null, "ctx", SecretProtector.Purpose.ShareLinks).Should().BeNull();
        }

        [Fact]
        public void AMissingOrInvalidMasterKey_IsRefused()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = "too-short"
            }).Build();

            FluentActions.Invoking(() => new SecretProtector(config)).Should().Throw<InvalidOperationException>();
        }
    }
}
