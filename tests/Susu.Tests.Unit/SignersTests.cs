using Susu.Net;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05.3: the digest/hmac primitives and the TC3/AWS SigV4 named signers.
///
/// <see cref="Aws_sigv4_matches_the_official_get_vanilla_test_suite_vector"/> and
/// <see cref="Tencent_tc3_matches_the_official_documented_worked_example"/> check against the actual
/// published vendor vectors (AWS's own aws-sig-v4-test-suite "get-vanilla" case; Tencent Cloud's
/// worked example at cloud.tencent.com/document/product/213/30654) - see each test's comment for the
/// exact source. Tencent's document masks its demo SecretKey with asterisks, so only the steps that do
/// not need it (canonical request, string-to-sign) and the final HMAC step (fed the document's own
/// published SecretSigning derived-key bytes) are checked against it end to end.
///
/// The remaining tests below are self-derived fixed request samples (no official vector exists for
/// them, or none with a usable key): expected values were computed independently of this
/// implementation with PowerShell 5.1 (.NET Framework's SHA256/HMACSHA256, a different CLR/crypto
/// stack than this NativeAOT .NET 10 build) driving the same algorithm by hand. DEV-PLAN F05.3 accepts
/// either an official vector or a fixed request sample; each test below is labelled with which it is.
/// </summary>
public class SignersTests
{
    /// <summary>Self-derived fixed sample (PowerShell/.NET Framework cross-check, see class comment).</summary>
    [Fact]
    public void Digest_sha256_hex_matches_an_independently_computed_reference()
    {
        Assert.Equal("2de9d353bd5a4be7d6ed04d6b8551b465184525fbcc4dc87548fca86198fc7e9", DigestSigner.Compute("sha256", "appKeyhelloWorld12345appSecret"));
    }

    /// <summary>Self-derived fixed sample (PowerShell/.NET Framework cross-check, see class comment).</summary>
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

    /// <summary>Self-derived fixed sample (PowerShell/.NET Framework cross-check, see class comment).</summary>
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

    /// <summary>
    /// Official worked example from cloud.tencent.com/document/product/213/30654 ("签名方法 v3"), fetched
    /// 2026-09-24. Request: POST https://cvm.tencentcloudapi.com/, DescribeInstances, version 2017-03-12,
    /// ap-guangzhou, X-TC-Timestamp 1551113065, body {"Limit": 1, "Filters": [{"Values": ["未命名"],
    /// "Name": "instance-name"}]}. The document's demo SecretId/SecretKey are masked with asterisks, so
    /// this checks the two steps that do not need the raw key: the CanonicalRequest and StringToSign the
    /// document publishes in full, and the final HMAC step fed the document's own published SecretSigning
    /// derived-key bytes (also printed in the document, in hex) - both against the document's published
    /// CanonicalRequest/StringToSign hashes and the final published Signature/Authorization.
    /// </summary>
    [Fact]
    public void Tencent_tc3_matches_the_official_documented_worked_example()
    {
        var request = new SignableRequest("POST", new Uri("https://cvm.tencentcloudapi.com/"),
            [new("content-type", "application/json; charset=utf-8"), new("host", "cvm.tencentcloudapi.com"), new("x-tc-action", "DescribeInstances")],
            "{\"Limit\": 1, \"Filters\": [{\"Values\": [\"\\u672a\\u547d\\u540d\"], \"Name\": \"instance-name\"}]}"u8.ToArray());
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1551113065);

        var pieces = TencentTc3Signer.BuildCanonicalPieces(request, "cvm", timestamp);
        Assert.Equal("POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:cvm.tencentcloudapi.com\nx-tc-action:describeinstances\n\ncontent-type;host;x-tc-action\n35e9c5b0e3ae67532d3c9f17ead6c90222632e5b1ff7f6e89887f1398934f064",
            pieces.CanonicalRequest);
        Assert.Equal("TC3-HMAC-SHA256\n1551113065\n2019-02-25/cvm/tc3_request\n7019a55be8395899b900fb5564e4200d984910f34794a27cb3fb7d10ff6a1e84",
            pieces.StringToSign);
        Assert.Equal("content-type;host;x-tc-action", pieces.SignedHeaders);
        Assert.Equal("2019-02-25/cvm/tc3_request", pieces.CredentialScope);

        // The document's own published SecretSigning derived key (hex), fed straight into the final
        // HMAC step - the one part of the pipeline that is otherwise unreachable without the masked key.
        byte[] secretSigning = Convert.FromHexString("b596b923aad85185e2d1f6659d2a062e0a86731226e021e61bfe06f7ed05f5af");
        string signature = TencentTc3Signer.SignFromDerivedKey(secretSigning, pieces.StringToSign);
        Assert.Equal("10b1a37a7301a02ca19a647ad722d5e43b4b3cff309d421d85b46093f6ab6c4f", signature);
    }

    /// <summary>Self-derived fixed sample (PowerShell/.NET Framework cross-check, see class comment); no
    /// official Tencent vector exists with a usable (unmasked) key, so the full Sign() path including the
    /// key-derivation steps can only be checked this way.</summary>
    [Fact]
    public void Tencent_tc3_authorization_matches_an_independently_computed_reference()
    {
        var request = new SignableRequest("POST", new Uri("https://tmt.tencentcloudapi.com/"),
            [new("content-type", "application/json; charset=utf-8"), new("host", "tmt.tencentcloudapi.com")],
            "{\"SourceText\":\"hello\",\"Source\":\"en\",\"Target\":\"zh\",\"ProjectId\":0}"u8.ToArray());
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        var signed = TencentTc3Signer.Sign(request, "tmt", "AKIDexampleSecretId0123456789", "exampleSecretKey0123456789abcdef", timestamp);

        Assert.Equal("TC3-HMAC-SHA256 Credential=AKIDexampleSecretId0123456789/2023-11-14/tmt/tc3_request, SignedHeaders=content-type;host, Signature=02dcec9f8080927c210c877a70bbc08a94f9e495bf884b4b510878a028e106d1",
            Header(signed, "Authorization"));
        Assert.Equal("1700000000", Header(signed, "X-TC-Timestamp"));
    }

    [Fact]
    public void Tencent_tc3_changes_signature_when_the_final_body_changes()
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        NamedSignature Sign(string body) => TencentTc3Signer.Sign(
            new SignableRequest("POST", new Uri("https://tmt.tencentcloudapi.com/"), [new("content-type", "application/json"), new("host", "tmt.tencentcloudapi.com")], System.Text.Encoding.UTF8.GetBytes(body)),
            "tmt", "id", "key", timestamp);
        Assert.NotEqual(Header(Sign("{\"a\":1}"), "Authorization"), Header(Sign("{\"a\":2}"), "Authorization")); // signs final bytes, not a pre-signing snapshot
    }

    /// <summary>
    /// The official AWS Signature Version 4 test suite's "get-vanilla" case, fetched 2026-09-24 from
    /// github.com/mongodb/libmongocrypt (kms-message/aws-sig-v4-test-suite/get-vanilla/*, a mirror of
    /// AWS's published test suite), with the AKIDEXAMPLE/"wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY" key
    /// pair the same repository's kms-message/test/test_kms_request.c uses for this exact fixture
    /// (Tencent's demo key is masked in its docs; AWS's is not, so this one runs the full Sign() path).
    /// GET / HTTP/1.1, Host: example.amazonaws.com, X-Amz-Date: 20150830T123600Z, empty body,
    /// region us-east-1, service "service".
    /// </summary>
    [Fact]
    public void Aws_sigv4_matches_the_official_get_vanilla_test_suite_vector()
    {
        var request = new SignableRequest("GET", new Uri("https://example.amazonaws.com/"), [new("host", "example.amazonaws.com")], []);
        var timestamp = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

        var signed = AwsSigV4Signer.Sign(request, "service", "us-east-1", "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", timestamp);

        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            Header(signed, "Authorization"));
        Assert.Equal("20150830T123600Z", Header(signed, "X-Amz-Date"));
    }

    /// <summary>Self-derived fixed sample (PowerShell/.NET Framework cross-check, see class comment):
    /// Amazon Translate TranslateText over the JSON 1.1 protocol, exercising content-type and x-amz-target
    /// in the signed-header set, which the official vanilla vector above does not carry.</summary>
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
