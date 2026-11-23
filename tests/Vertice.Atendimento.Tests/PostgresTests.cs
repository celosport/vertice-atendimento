using Dapper;
using Npgsql;
using Vertice.Atendimento.Core;
using Vertice.Atendimento.Infra.Postgres;

namespace Vertice.Atendimento.Tests;

/// <summary>
/// Testes de integração contra um PostgreSQL de verdade. Rodam só quando a variável <c>VERTICE_TESTES_BANCO</c> tem uma connection string
/// (usuário com permissão de criar banco). Cada execução cria um banco <c>vertice_t_*</c> próprio e o apaga no fim: nunca mexe nos seus dados.
/// <code>VERTICE_TESTES_BANCO="Host=localhost;Username=postgres;Password=...;Database=postgres" dotnet test</code>
/// </summary>
public sealed class BancoDeTestes : IAsyncLifetime
{
    public static readonly string? ConexaoAdmin = Environment.GetEnvironmentVariable("VERTICE_TESTES_BANCO");

    private string? _banco;
    public NpgsqlDataSource Dados { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConexaoAdmin)) return;
        _banco = $"vertice_t_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConexaoAdmin))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE {_banco}");
        }
        Dados = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConexaoAdmin) { Database = _banco }.ConnectionString);
        await SchemaPostgres.AplicarAsync(Dados, CaminhoDoSchema(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_banco is null) return;
        await Dados.DisposeAsync();
        await using var admin = new NpgsqlConnection(ConexaoAdmin);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_banco} WITH (FORCE)");
    }

    internal static string CaminhoDoSchema()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidato = Path.Combine(dir.FullName, "database", "schema.sql");
            if (File.Exists(candidato)) return candidato;
        }
        throw new FileNotFoundException("database/schema.sql não encontrado acima da pasta dos testes.");
    }
}

/// <summary>Como [Fact], mas pulado quando não há banco de teste configurado.</summary>
public sealed class FatoComBancoAttribute : FactAttribute
{
    public FatoComBancoAttribute()
    {
        if (string.IsNullOrWhiteSpace(BancoDeTestes.ConexaoAdmin))
            Skip = "Defina VERTICE_TESTES_BANCO para rodar os testes contra o PostgreSQL.";
    }
}

public sealed class PostgresTests(BancoDeTestes banco) : IClassFixture<BancoDeTestes>
{
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);
    private static readonly DateTimeOffset Inicio = new(2026, 11, 24, 9, 0, 0, Brasilia);

    private static Agendamento Novo(string negocio, DateTimeOffset inicio, int minutos = 30, string numero = "5521911112222") =>
        new(Guid.NewGuid(), negocio, numero, "corte", inicio, inicio.AddMinutes(minutos));

    private static string NovoNegocio() => $"n-{Guid.NewGuid():N}";

    [FatoComBanco]
    public async Task Schema_pode_ser_aplicado_de_novo_sem_erro()
    {
        await SchemaPostgres.AplicarAsync(banco.Dados, BancoDeTestes.CaminhoDoSchema(), default);
        await SchemaPostgres.AplicarAsync(banco.Dados, BancoDeTestes.CaminhoDoSchema(), default);
    }

    [FatoComBanco]
    public async Task Conversa_guarda_e_devolve_o_estado_inteiro()
    {
        var repo = new ConversaRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();
        Assert.Equal(Passo.Inicio, (await repo.ObterAsync(n, "55", default)).Passo); // sem registro = começo

        var estado = new EstadoConversa(Passo.EscolhendoHorario, "corte", new DateOnly(2026, 11, 24), [new TimeOnly(9, 0), new TimeOnly(9, 30)]);
        await repo.SalvarAsync(n, "55", estado, default);
        await repo.SalvarAsync(n, "55", estado with { Passo = Passo.Confirmando, Opcoes = [new TimeOnly(9, 30)] }, default); // atualiza, não duplica

        var lido = await repo.ObterAsync(n, "55", default);
        Assert.Equal(Passo.Confirmando, lido.Passo);
        Assert.Equal("corte", lido.ServicoId);
        Assert.Equal(new DateOnly(2026, 11, 24), lido.Dia);
        Assert.Equal([new TimeOnly(9, 30)], lido.Opcoes);
    }

    [FatoComBanco]
    public async Task Cria_agendamento_com_deslocamento_de_fuso_e_lista_pelo_intervalo()
    {
        var repo = new AgendamentoRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();
        Assert.True(await repo.TentarCriarAsync(Novo(n, Inicio), default)); // -03:00: o Npgsql recusa offset diferente de zero se não converter

        var dia = await repo.ListarConfirmadosAsync(n, Inicio.AddHours(-1), Inicio.AddDays(1), default);
        var a = Assert.Single(dia);
        Assert.Equal(Inicio.ToUniversalTime(), a.Inicio.ToUniversalTime());
        Assert.Empty(await repo.ListarConfirmadosAsync(n, Inicio.AddDays(1), Inicio.AddDays(2), default));
    }

    [FatoComBanco]
    public async Task Horario_ocupado_e_recusado_pelo_banco_mas_encostar_e_permitido()
    {
        var repo = new AgendamentoRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();
        Assert.True(await repo.TentarCriarAsync(Novo(n, Inicio), default));

        Assert.False(await repo.TentarCriarAsync(Novo(n, Inicio), default)); // mesmo horário
        Assert.False(await repo.TentarCriarAsync(Novo(n, Inicio.AddMinutes(15)), default)); // sobrepõe no meio
        Assert.True(await repo.TentarCriarAsync(Novo(n, Inicio.AddMinutes(30)), default)); // começa quando o outro termina
        Assert.True(await repo.TentarCriarAsync(Novo(NovoNegocio(), Inicio), default)); // outro negócio: agenda separada
    }

    [FatoComBanco]
    public async Task Disputa_simultanea_pelo_mesmo_horario_tem_um_unico_vencedor()
    {
        var repo = new AgendamentoRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => repo.TentarCriarAsync(Novo(n, Inicio, numero: $"55{i}"), default)));

        Assert.Equal(1, resultados.Count(r => r));
        Assert.Single(await repo.ListarConfirmadosAsync(n, Inicio.AddHours(-1), Inicio.AddHours(2), default));
    }

    [FatoComBanco]
    public async Task Cancelar_libera_o_horario_e_some_do_proximo_do_cliente()
    {
        var repo = new AgendamentoRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();
        var a = Novo(n, Inicio);
        await repo.TentarCriarAsync(a, default);
        var agora = Inicio.AddDays(-1);

        Assert.Equal(a.Id, (await repo.ProximoDoClienteAsync(n, a.Numero, agora, default))!.Id);
        await repo.CancelarAsync(a.Id, default);

        Assert.Null(await repo.ProximoDoClienteAsync(n, a.Numero, agora, default));
        Assert.True(await repo.TentarCriarAsync(Novo(n, Inicio, numero: "outro"), default));
    }

    [FatoComBanco]
    public async Task Proximo_do_cliente_e_o_mais_cedo_dele_e_ignora_o_que_ja_passou()
    {
        var repo = new AgendamentoRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();
        await repo.TentarCriarAsync(Novo(n, Inicio.AddDays(3)), default);
        var cedo = Novo(n, Inicio.AddDays(1));
        await repo.TentarCriarAsync(cedo, default);
        await repo.TentarCriarAsync(Novo(n, Inicio.AddDays(-5)), default); // passado
        await repo.TentarCriarAsync(Novo(n, Inicio.AddDays(1).AddHours(2), numero: "outra-pessoa"), default);

        var proximo = await repo.ProximoDoClienteAsync(n, "5521911112222", Inicio, default);

        Assert.Equal(cedo.Id, proximo!.Id);
    }

    [FatoComBanco]
    public async Task Lembrete_lista_so_o_que_esta_na_janela_e_so_ate_ser_marcado()
    {
        var repo = new AgendamentoRepositorioPostgres(banco.Dados);
        var n = NovoNegocio();
        var dentro = Novo(n, Inicio);
        var longe = Novo(n, Inicio.AddDays(3));
        var cancelado = Novo(n, Inicio.AddHours(3));
        await repo.TentarCriarAsync(dentro, default);
        await repo.TentarCriarAsync(longe, default);
        await repo.TentarCriarAsync(cancelado, default);
        await repo.CancelarAsync(cancelado.Id, default);
        var agora = Inicio.AddHours(-10);

        var pendentes = (await repo.ListarParaLembreteAsync(agora, agora.AddHours(24), default)).Where(a => a.NegocioId == n).ToList();
        Assert.Equal([dentro.Id], pendentes.Select(a => a.Id));

        await repo.MarcarLembreteEnviadoAsync(dentro.Id, default);
        Assert.Empty((await repo.ListarParaLembreteAsync(agora, agora.AddHours(24), default)).Where(a => a.NegocioId == n));
    }
}
