using System.Security.Cryptography;
using System.Text;
using Vertice.Atendimento.Infra.Mensageria;

namespace Vertice.Atendimento.Tests;

public sealed class MetaWebhookTests
{
    internal const string Segredo = "segredo-do-app-de-teste";

    internal const string PayloadTexto = """
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp",
          "metadata":{"display_phone_number":"5521999990000","phone_number_id":"1111"},
          "contacts":[{"profile":{"name":"Maria"},"wa_id":"5521911112222"}],
          "messages":[{"from":"5521911112222","id":"wamid.AAA","timestamp":"1","type":"text","text":{"body":"oi"}}]}}]}]}
        """;

    internal static string Assinar(string corpo, string segredo = Segredo) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(corpo))).ToLowerInvariant();

    [Fact]
    public void Extrai_a_mensagem_de_texto_com_numero_negocio_e_id()
    {
        var m = Assert.Single(MetaWebhook.Extrair(PayloadTexto));

        Assert.Equal("5521911112222", m.Numero);
        Assert.Equal("oi", m.Texto);
        Assert.Equal("1111", m.PhoneNumberId);
        Assert.Equal("wamid.AAA", m.IdExterno);
    }

    [Fact]
    public void Ignora_status_de_entrega_imagens_e_payload_sem_mensagens()
    {
        const string status = """{"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"1111"},"statuses":[{"id":"x","status":"delivered"}]}}]}]}""";
        const string imagem = """{"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"1111"},"messages":[{"from":"55","id":"y","type":"image"}]}}]}]}""";

        Assert.Empty(MetaWebhook.Extrair(status));
        Assert.Empty(MetaWebhook.Extrair(imagem));
        Assert.Empty(MetaWebhook.Extrair("{}"));
    }

    [Fact]
    public void Assinatura_correta_e_aceita()
    {
        var corpo = PayloadTexto;

        Assert.True(MetaWebhook.AssinaturaValida(Segredo, Encoding.UTF8.GetBytes(corpo), Assinar(corpo)));
    }

    [Theory]
    [InlineData("")] // sem cabeçalho
    [InlineData("sha256=zzzz")] // hexadecimal inválido
    [InlineData("md5=abc")] // algoritmo errado
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")] // assinatura errada
    public void Assinatura_ausente_malformada_ou_errada_e_recusada(string cabecalho)
    {
        Assert.False(MetaWebhook.AssinaturaValida(Segredo, Encoding.UTF8.GetBytes(PayloadTexto), cabecalho));
    }

    [Fact]
    public void Corpo_alterado_ou_segredo_diferente_invalidam_a_assinatura()
    {
        var assinatura = Assinar(PayloadTexto);

        Assert.False(MetaWebhook.AssinaturaValida(Segredo, Encoding.UTF8.GetBytes(PayloadTexto + " "), assinatura));
        Assert.False(MetaWebhook.AssinaturaValida("outro-segredo", Encoding.UTF8.GetBytes(PayloadTexto), assinatura));
    }

    [Fact]
    public void Sem_segredo_configurado_nada_e_aceito()
    {
        Assert.False(MetaWebhook.AssinaturaValida("", Encoding.UTF8.GetBytes(PayloadTexto), Assinar(PayloadTexto, "")));
    }
}
