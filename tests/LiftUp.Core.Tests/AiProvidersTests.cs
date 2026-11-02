using LiftUp.Core.Ai;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class AiProvidersTests
{
    [Fact]
    public void Groq_steht_an_erster_Stelle()
    {
        Assert.Equal("groq", AiProviders.All[0].Key);
        Assert.StartsWith("https://api.groq.com", AiProviders.All[0].BaseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Unbekannter_Schluessel_faellt_auf_den_ersten_Anbieter_zurueck()
    {
        Assert.Equal("groq", AiProviders.Find("gibt-es-nicht").Key);
        Assert.Equal("groq", AiProviders.Find(null).Key);
    }

    [Theory]
    [InlineData("groq")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    [InlineData("mistral")]
    [InlineData("openrouter")]
    [InlineData("deepseek")]
    [InlineData("ollama")]
    public void Jeder_Anbieter_hat_Adresse_und_Modell(string key)
    {
        var provider = AiProviders.Find(key);

        Assert.Equal(key, provider.Key);
        Assert.False(string.IsNullOrWhiteSpace(provider.Name));
        Assert.StartsWith("http", provider.BaseUrl, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(provider.DefaultModel));
        Assert.Contains(provider.DefaultModel, provider.Models);
    }

    [Fact]
    public void Nur_das_lokale_Modell_braucht_keinen_Schluessel()
    {
        Assert.False(AiProviders.Find("ollama").NeedsKey);
        Assert.All(
            AiProviders.All.Where(provider => provider.Key != "ollama"),
            provider => Assert.True(provider.NeedsKey));
    }

    [Fact]
    public void Die_fuenf_Aktionen_haben_eigene_Beschriftungen_und_Anweisungen()
    {
        var actions = Enum.GetValues<AiTextAction>();

        Assert.Equal(5, actions.Length);
        Assert.Equal(5, actions.Select(AiTextActions.Label).Distinct().Count());
        Assert.Equal(5, actions.Select(AiTextActions.Instruction).Distinct().Count());
        Assert.All(actions, action => Assert.False(string.IsNullOrWhiteSpace(AiTextActions.Instruction(action))));
    }

    [Fact]
    public void Stichworte_stehen_als_Aktion_mit_eigener_Anweisung_bereit()
    {
        Assert.Equal("Aus Stichworten Text erstellen", AiTextActions.Label(AiTextAction.FromKeywords));
        Assert.Contains("Stichworten", AiTextActions.Instruction(AiTextAction.FromKeywords), StringComparison.Ordinal);
    }

    [Fact]
    public void Stichworte_werden_als_ganze_Saetze_im_professionellen_Ton_verlangt()
    {
        var instruction = AiTextActions.Instruction(AiTextAction.FromKeywords);

        Assert.Contains("ganze Sätze", instruction, StringComparison.Ordinal);
        Assert.Contains("professionellen Ton", instruction, StringComparison.Ordinal);
        Assert.Contains("mindestens zwei Sätzen", instruction, StringComparison.Ordinal);
        Assert.Contains("eigenen Absatz", instruction, StringComparison.Ordinal);
    }
}
