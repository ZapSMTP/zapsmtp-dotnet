using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ZapSMTP.Internal;

namespace ZapSMTP.Tests
{
    public sealed class codexSdkdotnetNumericTests
    {
        [Theory]
        [InlineData("1e999")]
        [InlineData("-1e999")]
        [InlineData("\"1e999\"")]
        [InlineData("\"NaN\"")]
        [InlineData("\"Infinity\"")]
        [InlineData("\"-Infinity\"")]
        public async Task TestCodexSdkdotnetUnrepresentableResponseNumberBecomesNull(string score)
        {
            var h = new FakeHandler().Answer(() => Http.Json(200, "{\"score\":" + score + ",\"threshold\":5}"));
            using var zap = Clients.Make(h);
            var result = await zap.Emails.SpamCheckAsync(new EmailsSpamCheckParams { From = "sender@example.test", To = new[] { "recipient@example.test" }, Subject = "test", Text = "test" });

            // README promises nullable, lenient fields; nonfinite CLR doubles
            // cannot be JSON response values and break reserialization as well.
            Assert.Null(result.Score);
            Assert.Equal(5d, result.Threshold);
            var json = JsonSerializer.SerializeToElement(result, ZapJson.Options);
            Assert.Equal(5d, json.GetProperty("threshold").GetDouble());
            Assert.Single(h.Calls);
        }
    }
}
