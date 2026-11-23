using System.Text.Json;
using Dapper;
using Npgsql;
using Vertice.Atendimento.Core;

namespace Vertice.Atendimento.Infra.Postgres;

public static class SchemaPostgres
{
    /// <summary>Aplica o schema.sql (idempotente). Chamado na subida do serviço.</summary>
    public static async Task AplicarAsync(NpgsqlDataSource dados, string caminho, CancellationToken cancellationToken)
    {
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        await conexao.ExecuteAsync(new CommandDefinition(await File.ReadAllTextAsync(caminho, cancellationToken), cancellationToken: cancellationToken));
    }
}

public sealed class ConversaRepositorioPostgres(NpgsqlDataSource dados) : IConversaRepositorio
{
    private static readonly JsonSerializerOptions Json = new();

    public async Task<EstadoConversa> ObterAsync(string negocioId, string numero, CancellationToken cancellationToken)
    {
        const string sql = "SELECT estado::text FROM conversas WHERE negocio_id = @NegocioId AND numero = @Numero;";
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        var texto = await conexao.QuerySingleOrDefaultAsync<string>(new CommandDefinition(sql, new { NegocioId = negocioId, Numero = numero }, cancellationToken: cancellationToken));
        return texto is null ? new EstadoConversa() : JsonSerializer.Deserialize<EstadoConversa>(texto, Json) ?? new EstadoConversa();
    }

    public async Task SalvarAsync(string negocioId, string numero, EstadoConversa estado, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO conversas (negocio_id, numero, estado, atualizado_em)
            VALUES (@NegocioId, @Numero, @Estado::jsonb, now())
            ON CONFLICT (negocio_id, numero) DO UPDATE SET estado = EXCLUDED.estado, atualizado_em = now();
            """;
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        await conexao.ExecuteAsync(new CommandDefinition(sql,
            new { NegocioId = negocioId, Numero = numero, Estado = JsonSerializer.Serialize(estado, Json) }, cancellationToken: cancellationToken));
    }
}

public sealed class AgendamentoRepositorioPostgres(NpgsqlDataSource dados) : IAgendamentoRepositorio
{
    private const string ViolacaoDeExclusao = "23P01";

    private sealed class Linha
    {
        public Guid AgendamentoId { get; set; }
        public string NegocioId { get; set; } = "";
        public string Numero { get; set; } = "";
        public string ServicoId { get; set; } = "";
        public DateTimeOffset Inicio { get; set; }
        public DateTimeOffset Fim { get; set; }
        public string Status { get; set; } = "CONFIRMADO";
        public bool LembreteEnviado { get; set; }

        public Agendamento ParaDominio() => new(AgendamentoId, NegocioId, Numero, ServicoId, Inicio, Fim,
            Status == "CANCELADO" ? StatusAgendamento.Cancelado : StatusAgendamento.Confirmado, LembreteEnviado);
    }

    private const string Colunas = "agendamento_id AS AgendamentoId, negocio_id AS NegocioId, numero AS Numero, servico_id AS ServicoId, inicio AS Inicio, fim AS Fim, status AS Status, lembrete_enviado AS LembreteEnviado";

    public async Task<IReadOnlyList<Agendamento>> ListarConfirmadosAsync(string negocioId, DateTimeOffset de, DateTimeOffset ate, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Colunas} FROM agendamentos WHERE negocio_id = @NegocioId AND status = 'CONFIRMADO' AND inicio < @Ate AND fim > @De;";
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        var linhas = await conexao.QueryAsync<Linha>(new CommandDefinition(sql,
            new { NegocioId = negocioId, De = de.ToUniversalTime(), Ate = ate.ToUniversalTime() }, cancellationToken: cancellationToken));
        return linhas.Select(l => l.ParaDominio()).ToList();
    }

    public async Task<bool> TentarCriarAsync(Agendamento a, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO agendamentos (agendamento_id, negocio_id, numero, servico_id, inicio, fim, status, lembrete_enviado)
            VALUES (@Id, @NegocioId, @Numero, @ServicoId, @Inicio, @Fim, 'CONFIRMADO', FALSE);
            """;
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        try
        {
            await conexao.ExecuteAsync(new CommandDefinition(sql,
                new { a.Id, a.NegocioId, a.Numero, a.ServicoId, Inicio = a.Inicio.ToUniversalTime(), Fim = a.Fim.ToUniversalTime() }, cancellationToken: cancellationToken));
            return true;
        }
        catch (PostgresException e) when (e.SqlState == ViolacaoDeExclusao)
        {
            return false; // o banco recusou: outro cliente ficou com o horário
        }
    }

    public async Task<Agendamento?> ProximoDoClienteAsync(string negocioId, string numero, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Colunas} FROM agendamentos WHERE negocio_id = @NegocioId AND numero = @Numero AND status = 'CONFIRMADO' AND inicio > @Agora ORDER BY inicio LIMIT 1;";
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        var linha = await conexao.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition(sql,
            new { NegocioId = negocioId, Numero = numero, Agora = agora.ToUniversalTime() }, cancellationToken: cancellationToken));
        return linha?.ParaDominio();
    }

    public async Task CancelarAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE agendamentos SET status = 'CANCELADO' WHERE agendamento_id = @Id;";
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        await conexao.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Agendamento>> ListarParaLembreteAsync(DateTimeOffset de, DateTimeOffset ate, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Colunas} FROM agendamentos WHERE status = 'CONFIRMADO' AND NOT lembrete_enviado AND inicio > @De AND inicio <= @Ate ORDER BY inicio;";
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        var linhas = await conexao.QueryAsync<Linha>(new CommandDefinition(sql,
            new { De = de.ToUniversalTime(), Ate = ate.ToUniversalTime() }, cancellationToken: cancellationToken));
        return linhas.Select(l => l.ParaDominio()).ToList();
    }

    public async Task MarcarLembreteEnviadoAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE agendamentos SET lembrete_enviado = TRUE WHERE agendamento_id = @Id;";
        await using var conexao = await dados.OpenConnectionAsync(cancellationToken);
        await conexao.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
    }
}
