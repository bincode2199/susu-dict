using Susu.Net;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05.3: the digest/hmac primitives and the TC3/AWS SigV4 named signers, checked against fixed
/// request samples whose expected values were computed independently of this implementation - with
/// PowerShell 5.1 (.NET Framework's SHA256/HMACSHA256, a different CLR/crypto stack than this
/// NativeAOT .NET 10 build) driving the exact same canonical-request/derived-key algorithm by hand.
/// The script that produced these constants is not part of the build; the constants are the fixture.
/// No vendor account exists in this environment, so these are fixed samples, not officially-published
/// vendor test vectors - DEV-PLAN F05.3 accepts either.
/// </summary>
public class SignersTests
{
    [Fact]
    public void Digest_sha256_hex_matches_an_independently_computed_reference()
    {
        Assert.Equal("2de9d353bd5a4be7d6ed04d6b8551b465184525fbcc4dc87548fca86198fc7e9", DigestSigner.Compute("sha256", "appKeyhelloWorld12345appSecret"));
    }

    [Fact]
    public void Digest_md5_hex_matches_an_independently_computed_reference()
    {
        Assert.Equal("53d43efd1fbdf42f1748b0ecfeab8846", DigestSigner.Compute("md5", "appKeyhelloWorld12345appSecret"));
    }

    [Fact]
    public void Digest_base64_encoding_round_trips_the_same_bytes_as_hex()
    {
        string hex = DigestSigner.Compute("sha256", "x", "hex");
        string base64 = DigestSigner.Compute("sha256", "x", "base64");
        Assert.Equal(hex, Convert.ToHexStringLower(Convert.FromBase64String(base64)));
    }

    [Fact]
    public void Hmac_sha256_hex_matches_an_independently_computed_reference()
    {
        Assert.Equal("f487208c3f58b390d485088c673b78e6b1b67a50ae7987795817a8eb0baca0a6",
            HmacSigner.Compute("sha256", "hmac-secret-key"u8.ToArray(), "the-string-to-sign"));
    }

    [Fact]
    public void Digest_rejects_an_unsupported_algorithm()
    {
        Assert.Throws<ArgumentException>(() => DigestSigner.Compute("sha512", "x"));
    }

    /// <summary>Fixed sample: 腾讯翻译君 TextTranslate, service "tmt", ap-guangzhou, a synthetic key pair.</summary>
    [Fact]
    public void Tencent_tc3_authorization_matches_an_independently_computed_reference()
    {
        var request = new SignableRequest("POST", new Uri("https://tmt.tencentcloudapi.com/"),
            [new("content-type", "application/json; charset=utf-8"), new("host", "tmt.tencentcloudapi.com")],
            "{\"SourceText\":\"hello\",\"Source\":\"en\",\"Target\":\"zh\",\"ProjectId\":0}"u8.ToArray());
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        var signed = TencentTc3Signer.Sign(request, "tmt", "ap-guangzhou", "TextTranslate", "2018-03-21", "AKIDexampleSecretId0123456789", "exampleSecretKey0123456789abcdef", timestamp);

        Assert.Equal("TC3-HMAC-SHA256 Credential=AKIDexampleSecretId0123456789/2023-11-14/tmt/tc3_request, SignedHeaders=content-type;host, Signature=02dcec9f8080927c210c877a70bbc08a94f9e495bf884b4b510878a028e106d1",
            Header(signed, "Authorization"));
        Assert.Equal("1700000000", Header(signed, "X-TC-Timestamp"));
        Assert.Equal("TextTranslate", Header(signed, "X-TC-Action"));
        Assert.Equal("2018-03-21", Header(signed, "X-TC-Version"));
        Assert.Equal("ap-guangzhou", Header(signed, "X-TC-Region"));
    }

    [Fact]
    public void Tencent_tc3_changes_signature_when_the_final_body_changes()
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        NamedSignature Sign(string body) => TencentTc3Signer.Sign(
            new SignableRequest("POST", new Uri("https://tmt.tencentcloudapi.com/"), [new("content-type", "application/json"), new("host", "tmt.tencentcloudapi.com")], System.Text.Encoding.UTF8.GetBytes(body)),
            "tmt", "ap-guangzhou", "TextTranslate", "2018-03-21", "id", "key", timestamp);
        Assert.NotEqual(Header(Sign("{\"a\":1}"), "Authorization"), Header(Sign("{\"a\":2}"), "Authorization")); // signs final bytes, not a pre-signing snapshot
    }

    /// <summary>Fixed sample: Amazon Translate TranslateText over the JSON 1.1 protocol.</summary>
    [Fact]
    public void Aws_sigv4_authorization_matches_an_independently_computed_reference()
    {
        var request = new SignableRequest("POST", new Uri("https://translate.us-east-1.amazonaws.com/"),
            [new("content-type", "application/x-amz-json-1.1"), new("host", "translate.us-east-1.amazonaws.com"),
             new("x-amz-target", "AWSShineFrontendService_20170701.TranslateText")],
            "{\"Text\":\"hello\",\"SourceLanguageCode\":\"en\",\"TargetLanguageCode\":\"zh\"}"u8.ToArray());
        var timestamp = new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero);

        var signed = AwsSigV4Signer.Sign(request, "translate", "us-east-1", "AKIAEXAMPLEACCESSKEY0000", "exampleAwsSecretAccessKey0123456789abcdef", timestamp);

        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLEACCESSKEY0000/20231114/us-east-1/translate/aws4_request, SignedHeaders=content-type;host;x-amz-date;x-amz-target, Signature=5e5f276e7beee2f4110ff1cc6db16dc89a65d398c3569eaaa4f1cca628a05bf3",
            Header(signed, "Authorization"));
        Assert.Equal("20231114T221320Z", Header(signed, "X-Amz-Date"));
    }

    [Fact]
    public void Aws_sigv4_never_includes_the_secret_key_in_its_output()
    {
        var request = new SignableRequest("POST", new Uri("https://translate.us-east-1.amazonaws.com/"),
            [new("content-type", "application/x-amz-json-1.1"), new("host", "translate.us-east-1.amazonaws.com")], "{}"u8.ToArray());
        var signed = AwsSigV4Signer.Sign(request, "translate", "us-east-1", "AKID", "top-secret-value", DateTimeOffset.UtcNow);
        Assert.DoesNotContain(signed.Headers, h => h.Value.Contains("top-secret-value"));
    }

    private static string Header(NamedSignature signature, string name)
        => signature.Headers.First(h => h.Key == name).Value;
}
