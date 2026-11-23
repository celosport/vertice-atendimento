-- Schema idempotente: roda a cada subida do serviço (IF NOT EXISTS / DO $$ ... EXCEPTION WHEN duplicate_object).
-- snake_case em português; enums como texto + CHECK; TIMESTAMPTZ em UTC.

CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Onde cada conversa parou (menu, escolhendo horário...). O estado é pequeno e muda a cada mensagem: fica num JSONB.
CREATE TABLE IF NOT EXISTS conversas (
    negocio_id    TEXT        NOT NULL,
    numero        TEXT        NOT NULL,
    estado        JSONB       NOT NULL,
    atualizado_em TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (negocio_id, numero)
);

CREATE TABLE IF NOT EXISTS agendamentos (
    agendamento_id  UUID        PRIMARY KEY,
    negocio_id      TEXT        NOT NULL,
    numero          TEXT        NOT NULL,
    servico_id      TEXT        NOT NULL,
    inicio          TIMESTAMPTZ NOT NULL,
    fim             TIMESTAMPTZ NOT NULL,
    status          TEXT        NOT NULL CHECK (status IN ('CONFIRMADO', 'CANCELADO')),
    lembrete_enviado BOOLEAN    NOT NULL DEFAULT FALSE,
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (fim > inicio)
);

-- Dois agendamentos confirmados do mesmo negócio não podem se sobrepor. É o banco que decide (atômico), não o código.
DO $$ BEGIN
    ALTER TABLE agendamentos ADD CONSTRAINT ex_agendamentos_sem_conflito
        EXCLUDE USING gist (negocio_id WITH =, tstzrange(inicio, fim) WITH &&) WHERE (status = 'CONFIRMADO');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS ix_agendamentos_cliente ON agendamentos (negocio_id, numero, inicio);
CREATE INDEX IF NOT EXISTS ix_agendamentos_lembrete ON agendamentos (inicio) WHERE status = 'CONFIRMADO' AND NOT lembrete_enviado;
