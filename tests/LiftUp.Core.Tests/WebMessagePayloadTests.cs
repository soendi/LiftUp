using LiftUp.Core.Documents;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class WebMessagePayloadTests
{
    [Fact]
    public void Objekt_nachricht_bleibt_unveraendert()
    {
        Assert.Equal(
            """{"type":"snippetQuery","requestId":1}""",
            WebMessagePayload.Unwrap("""{"type":"snippetQuery","requestId":1}"""));
    }

    [Fact]
    public void Als_string_verpackte_nachricht_wird_entpackt()
    {
        // Genau diese Form liefert WebView2 für window.chrome.webview.postMessage(text).
        const string raw = """ "{\"type\":\"snippetQuery\",\"requestId\":7,\"query\":\"lorem\"}" """;

        Assert.Equal(
            """{"type":"snippetQuery","requestId":7,"query":"lorem"}""",
            WebMessagePayload.Unwrap(raw));
    }

    [Fact]
    public void Leere_nachrichten_liefern_null()
    {
        Assert.Null(WebMessagePayload.Unwrap(null));
        Assert.Null(WebMessagePayload.Unwrap(string.Empty));
        Assert.Null(WebMessagePayload.Unwrap("   "));
        Assert.Null(WebMessagePayload.Unwrap("\"\""));
    }
}
