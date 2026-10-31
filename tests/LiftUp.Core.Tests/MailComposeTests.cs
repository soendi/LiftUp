using System.Text;
using LiftUp.Core.Documents;
using LiftUp.Core.Domain;
using LiftUp.Core.Mail;
using LiftUp.Core.Services;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class MailComposeTests
{
    private sealed class FakeRenderer : IHtmlToPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, PdfPageSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.ASCII.GetBytes("%PDF-1.4"));
    }

    [Fact]
    public void Anmeldeadresse_enthaelt_pkce_und_zustand()
    {
        var account = new MailAccount { Provider = MailProvider.Google, ClientId = "client-123" };

        var (url, verifier, state) = MailAccountService.BuildAuthorizationRequest(account, "http://127.0.0.1:5000/");

        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("client_id=client-123", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains($"state={state}", url);
        Assert.Contains("googleapis.com", url);
        Assert.Contains($"code_challenge={MailAccountService.CreateChallenge(verifier)}", url);
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
        Assert.InRange(verifier.Length, 43, 128);
    }

    [Fact]
    public void Microsoft_Anmeldung_fordert_offline_zugriff_und_mailversand()
    {
        var account = new MailAccount { Provider = MailProvider.Microsoft365, ClientId = "app-456" };

        var (url, _, _) = MailAccountService.BuildAuthorizationRequest(account, "http://localhost:5000/");

        Assert.Contains("login.microsoftonline.com", url);
        Assert.Contains(Uri.EscapeDataString("offline_access"), url);
        Assert.Contains(Uri.EscapeDataString("https://graph.microsoft.com/Mail.Send"), url);
    }

    [Fact]
    public void MIME_Nachricht_enthaelt_html_text_und_anhang()
    {
        using var fixture = new TestDatabase();
        var path = Path.Combine(fixture.Root, "bericht.pdf");
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 test");
        File.WriteAllBytes(path, bytes);

        var raw = MailMimeBuilder.BuildRaw(
            new MailMessage
            {
                To = "kunde@example.com",
                Cc = "buero@example.com",
                Subject = "Bericht Grün",
                BodyHtml = "<p>Guten Tag</p>",
                AttachmentPath = path,
            },
            "ich@example.com");

        Assert.Contains("To: kunde@example.com", raw);
        Assert.Contains("Cc: buero@example.com", raw);
        Assert.Contains("Subject: =?UTF-8?B?", raw);
        Assert.Contains("Content-Type: application/pdf", raw);
        Assert.Contains(Uri.EscapeDataString("bericht.pdf"), raw);
        Assert.Contains(Convert.ToBase64String(bytes), raw);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Guten Tag</p>")), raw.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void Betreff_ohne_sonderzeichen_bleibt_lesbar()
    {
        Assert.Equal("Angebot 2026", MailMimeBuilder.EncodeHeader("Angebot 2026"));
        Assert.StartsWith("=?UTF-8?B?", MailMimeBuilder.EncodeHeader("Grüße"));
    }

    [Fact]
    public async Task Dokument_merkt_sich_seine_Abschnitte()
    {
        using var fixture = new TestDatabase();
        var session = fixture.LoginAsAdmin();
        var projects = new ProjectService(fixture.Database, session);
        var templates = new TemplateService(fixture.Database, session);
        var documents = new DocumentService(fixture.Database, session, new FakeRenderer());

        var project = projects.Create(new ProjectInput
        {
            Name = "Bericht Musterweg",
            FolderPath = Path.Combine(fixture.Root, "projekt"),
            CreateFolderIfMissing = true,
        });
        var template = templates.Create(new TemplateInput
        {
            Name = "Bericht",
            Html = "<html><body><h1>Titel</h1></body></html>",
        });

        var created = await documents.CreatePdfFromSectionsAsync(project.Id, template.Id,
        [
            new DocumentSectionContent("Einleitung", "<p>Text</p>"),
            new DocumentSectionContent("Fazit", "<p>Ende</p>"),
        ]);

        Assert.True(created.HasSections);

        var stored = documents.GetDocuments(project.Id).Single();
        var sections = DocumentSections.Deserialize(stored.SectionsJson);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Einleitung", sections[0].Title);
        Assert.Equal("<p>Text</p>", sections[0].Html);
        Assert.Equal("Fazit", sections[1].Title);
    }
}
