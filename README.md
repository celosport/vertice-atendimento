# Vértice Atendimento

Robô de **atendimento e agendamento por WhatsApp** para negócios de serviço (salões, clínicas, estúdios). É a primeira automação
vendável da Vértice Studio: responde perguntas, agenda, remarca, cancela, lembra do horário e passa para uma pessoa quando precisa.

.NET 8 + PostgreSQL. Um serviço só atende **vários clientes** (cada negócio tem serviços, horários e respostas próprios).

## Testar de graça, agora (sem WhatsApp, sem conta da Meta)

```bash
dotnet run --project src/Vertice.Atendimento.Api
```

Abra a URL que o comando mostrar. Aparece um celular de mentira: escreva **oi** e converse. Nenhuma mensagem real é enviada.
É também a melhor forma de **demonstrar para um cliente** antes de ligar o número dele.

```bash
dotnet test        # 51 testes (cálculo de horários, conversa, webhook, lembretes, API)
```

## Como funciona

| Peça | O que faz |
|---|---|
| `MotorDeAtendimento` | Lê a mensagem e decide a resposta pelo ponto em que a conversa parou (menu, escolher serviço, dia, horário, confirmar, cancelar, perguntas frequentes, falar com uma pessoa). Sem IA: previsível e testável. |
| `CalculadoraDeHorarios` | Quais horários estão livres para um serviço (janela de atendimento, duração, o que já está ocupado, o que já passou). |
| `ServicoDeLembretes` | Manda um lembrete por agendamento, nas 24 h antes. Só marca como enviado depois de enviar. |
| `IMensageria` | Como o robô fala com o cliente. Hoje: `SimuladorMensageria` (grátis) e `MetaCloudMensageria` (API oficial). Outro provedor = implementar a interface. |
| Agenda no banco | Dois agendamentos confirmados do mesmo negócio nunca se sobrepõem: quem decide é uma restrição do PostgreSQL (`database/schema.sql`), não o código. |

## Cadastrar um cliente (negócio)

Edite `src/Vertice.Atendimento.Api/negocios.json`: serviços (nome, duração, preço), horários por dia da semana e perguntas frequentes
(palavras que disparam cada resposta). `numeroDoResponsavel` recebe um aviso quando um cliente pede para falar com uma pessoa.

## Ligar o WhatsApp oficial (Meta Cloud API)

A Meta oferece um **número de teste gratuito** no painel de desenvolvedor (com poucos destinatários permitidos), bom para validar o
fluxo real antes de pagar qualquer coisa. Confira as regras e preços vigentes na documentação da Meta: eles mudam.

1. Crie um app em developers.facebook.com, adicione o produto **WhatsApp** e anote o `Phone number ID` do número.
2. Preencha `whatsAppPhoneNumberId` do negócio em `negocios.json`.
3. Defina no Railway (variáveis de ambiente, **nunca no Git**):

| Variável | Valor |
|---|---|
| `Mensageria__Provedor` | `Meta` |
| `Meta__AccessToken` | token de acesso (usuário do sistema) |
| `Meta__AppSecret` | segredo do app (confere a assinatura dos webhooks) |
| `Meta__VerifyToken` | um texto que você inventa |
| `Armazenamento__Tipo` | `Postgres` |
| `ConnectionStrings__Postgres` | conexão do PostgreSQL |

4. No painel da Meta, cadastre o webhook `https://SEU-DOMINIO/webhook/whatsapp` com o mesmo `VerifyToken` e assine o campo **messages**.
5. Cadastre na Meta o **modelo de mensagem** `lembrete_agendamento` (idioma pt_BR), com 3 variáveis no corpo:
   `Olá! Lembrete: {{1}} em {{2}} às {{3}}.` É a única forma de o WhatsApp deixar mandar mensagem quando o cliente não escreveu nas últimas 24 h.

Com `Provedor = Meta` o simulador deixa de existir (as rotas `/simulador` não são criadas).

## Segurança

- Todo webhook é conferido pela assinatura HMAC (`X-Hub-Signature-256`) com o segredo do app; sem assinatura válida, 401.
- O serviço não sobe com `Provedor = Meta` sem token, segredo e verify token.
- A mesma mensagem reenviada pela Meta é ignorada (por 30 min). Falha ao responder não vira erro para a Meta, para ela não reenviar.
- O log não grava o texto das mensagens nem o token.
- Dados pessoais (número do cliente) ficam só no banco do próprio serviço. Para clientes reais, veja a cláusula de LGPD do contrato.

## O que ainda não está pronto

- **Os repositórios PostgreSQL ainda não foram testados contra um banco real** (não havia banco na máquina onde foram escritos). O código compila e o
  schema é idempotente, mas rode um teste de integração com Postgres antes do primeiro cliente.
- **Mensagem de lembrete real:** depende do modelo aprovado pela Meta (passo 5). No simulador ela aparece como texto.
- **Uma agenda por negócio.** Vários profissionais, com agenda própria cada um, é a evolução natural.
- **Sem IA ainda.** Frases livres fora do roteiro caem no "não entendi" ou nas perguntas frequentes. Uma camada com a Claude para entender
  pedidos livres ("quero cortar o cabelo sexta à tarde") encaixa no motor, mantendo as regras de agenda no código.
- **Painel do cliente** (ver agenda, ajustar serviços e horários sem editar JSON) e **relatório mensal** do pacote.
- Só mensagens de texto são tratadas (áudio e imagem recebem um aviso).
