-- Outbox durable de los recordatorios de bundle en rojo (odd/tasks/bundle-expiry-acquisition.md, spec S2).
--
-- Tabla PROPIA, no una extensión de deal_alert_events: un bundle no es una oferta por juego. No tiene precio,
-- no tiene identidad canónica de juego y no pasa por las puertas de interés (favoritos + wishlist) de las
-- alertas de oferta. Compartir la tabla habría obligado a compartir su dedup_key, su revalidación y su filtro de
-- destinatario, es decir, a cambiar las puertas de las ofertas existentes para hacer sitio a otra familia.
--
-- Una fila = un ciclo de vencimiento de un bundle para el operador configurado (Ntfy__UserId):
--   dedup_key = "bundle-alert:v<policy>:<user_id>:<source_family>:<bundle_key>:<expires_at en microsegundos UTC>"
-- El índice único es lo que hace verdadero "un solo aviso por bundle y ciclo" aunque el job corra cada 15
-- minutos, y lo que hace que itad e itad_global (misma familia, mismo bundle) inserten UNA fila y gg.deals
-- (espacio de claves propio) inserte otra. El título nunca forma parte de la clave: un título no es identidad.
--
-- expires_at es el SNAPSHOT por el que se encoló el aviso. El despachador relee external_bundles antes de cada
-- envío y exige el mismo instante: si el proveedor movió la fecha, el bundle desapareció, expiró o se compró, la
-- fila se retira en lugar de enviarse obsoleta.
--
-- status: pending | sent | failed | stale | suppressed
--   pending    - insertada, esperando su primer o siguiente intento
--   sent       - ntfy respondió 2xx; sent_at queda puesto
--   failed     - rechazo permanente o presupuesto de intentos agotado
--   stale      - el bundle ya no coincide con el snapshot (fecha movida, expirado, sin fecha o desaparecido)
--   suppressed - el operador compró el bundle antes del envío; se descarta a propósito y NO es un fallo
-- user_id es el operador configurado (Ntfy__UserId) y lleva FK: si la cuenta desaparece, su outbox se va con
-- ella en vez de dejar filas huérfanas cuyas claves de dedup bloquearían el ciclo para siempre.
--
-- last_error guarda solo el nombre del resultado (Delivered/Retryable/Permanent/Ambiguous), nunca la URL, el
-- tema, el token ni el cuerpo de una excepción.

CREATE TABLE IF NOT EXISTS public.bundle_alert_events (
    bundle_alert_event_id BIGSERIAL PRIMARY KEY,
    dedup_key             VARCHAR(320) NOT NULL,
    user_id               INT          NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    source                VARCHAR(16)  NOT NULL,
    source_family         VARCHAR(16)  NOT NULL,
    bundle_key            VARCHAR(128) NOT NULL,
    expires_at            TIMESTAMPTZ  NOT NULL,
    trigger_version       INT          NOT NULL,
    title                 VARCHAR(512) NOT NULL,
    message               VARCHAR(1024) NOT NULL,
    click_url             VARCHAR(1024) NULL,
    status                VARCHAR(16)  NOT NULL DEFAULT 'pending',
    attempts              INT          NOT NULL DEFAULT 0,
    next_attempt_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_error            VARCHAR(256) NULL,
    sent_at               TIMESTAMPTZ  NULL,
    created_at            TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    updated_at            TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    created_by            VARCHAR(100),
    updated_by            VARCHAR(100),
    CONSTRAINT uq_bundle_alert_events_dedup UNIQUE (dedup_key),
    -- El vocabulario del ciclo de vida. 'suppressed' tiene su propio valor justamente para que una compra no se
    -- lea como fallo ni como bundle movido en la auditoría.
    CONSTRAINT ck_bundle_alert_events_status
        CHECK (status IN ('pending', 'sent', 'failed', 'stale', 'suppressed')),
    -- Un aviso sin fecha no existe: la política solo encola fechas futuras dentro de la ventana roja. El NOT NULL
    -- de expires_at es el que lo impide; esta consulta 2 de SQL/checks/bundle_alert_events.sql lo comprueba.
    CONSTRAINT ck_bundle_alert_events_attempts CHECK (attempts >= 0),
    -- El click es un enlace público HTTPS o nada. Un esquema raro no llega ni a la base.
    CONSTRAINT ck_bundle_alert_events_click_url
        CHECK (click_url IS NULL OR click_url LIKE 'https://%')
);

-- Barrido del despachador: solo filas pendientes cuyo plazo de reintento ya pasó, las más viejas primero.
CREATE INDEX IF NOT EXISTS idx_bundle_alert_events_due
    ON public.bundle_alert_events(status, next_attempt_at);

-- Supresión y auditoría por operador: "¿qué avisos tiene esta cuenta para este bundle?".
CREATE INDEX IF NOT EXISTS idx_bundle_alert_events_user_bundle
    ON public.bundle_alert_events(user_id, bundle_key);

ANALYZE public.bundle_alert_events;

COMMENT ON TABLE public.bundle_alert_events IS
    'Outbox de recordatorios rojos de bundle (spec S2). Una fila por operador + familia de fuente + bundle_key + expires_at.';
COMMENT ON COLUMN public.bundle_alert_events.expires_at IS
    'Snapshot del vencimiento por el que se encoló el aviso; se revalida contra external_bundles antes de enviar.';
COMMENT ON COLUMN public.bundle_alert_events.status IS
    'pending | sent | failed | stale | suppressed';
