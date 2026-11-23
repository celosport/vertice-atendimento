using System.Text.Json;
using System.Text.Json.Serialization;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Infra;

/// <summary>
/// Lê os negócios (clientes da Vértice) de um arquivo JSON: serviços, horários e respostas de cada um. Para a primeira versão basta;
/// quando houver muitos clientes, o mesmo contrato passa a ler de uma tabela.
/// </summary>
public sealed class NegocioRepositorioJson : INegocioRepositorio
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyList<Negocio> _negocios;

    private NegocioRepositorioJson(IReadOnlyList<Negocio> negocios) => _negocios = negocios;

    public static NegocioRepositorioJson DeArquivo(string caminho) => DeJson(File.ReadAllText(caminho));

    public static NegocioRepositorioJson DeJson(string json) =>
        new(JsonSerializer.Deserialize<List<Negocio>>(json, Opcoes) ?? throw new InvalidOperationException("Arquivo de negócios vazio."));

    public Task<Negocio?> ObterAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_negocios.FirstOrDefault(n => n.Id == id));

    public Task<Negocio?> ObterPorPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken) =>
        Task.FromResult(_negocios.FirstOrDefault(n => n.WhatsAppPhoneNumberId == phoneNumberId));

    public Task<IReadOnlyList<Negocio>> ListarAsync(CancellationToken cancellationToken) => Task.FromResult(_negocios);
}
