-- Invariantes del outbox de recordatorios rojos de bundle (odd/tasks/bundle-expiry-acquisition.md, spec S2).
-- Esperado: 0 filas en CADA consulta. Solo lectura: no modifica nada.
-- Correr tras aplicar SQL/migrations/2026-10-22_bundle_alert_events.sql y después de cualquier ciclo de
-- ntfy.alerts que se quiera auditar.

-- 1. Ningún estado fuera del vocabulario. El CHECK lo impide; si esta consulta devuelve algo, la restricción no
--    se aplicó y una fila quedó en un estado que ni el despachador ni la persona pueden interpretar.
SELECT bundle_alert_event_id, status
FROM public.bundle_alert_events
WHERE status NOT IN ('pending', 'sent', 'failed', 'stale', 'suppressed');

-- 2. Ningún aviso sin fecha de vencimiento. La política solo encola fechas futuras dentro de la ventana roja: una
--    fila con expires_at NULL significa que algo escribió por fuera de esa regla (y el NOT NULL falta).
SELECT bundle_alert_event_id, bundle_key, expires_at
FROM public.bundle_alert_events
WHERE expires_at IS NULL;

-- 3. Ninguna clave de deduplicación repetida. El índice único lo impide: si devuelve filas, el índice no existe y
--    el productor volvería a encolar el mismo ciclo cada 15 minutos.
SELECT dedup_key, count(*) AS filas
FROM public.bundle_alert_events
GROUP BY dedup_key
HAVING count(*) > 1;

-- 4. Ninguna familia de fuente incoherente con su origen. itad e itad_global son la misma familia; gg.deals es la
--    suya. Una fila que diga lo contrario rompe el dedup (dos avisos del mismo bundle) o lo funde de más.
SELECT bundle_alert_event_id, source, source_family, bundle_key
FROM public.bundle_alert_events
WHERE source_family IS DISTINCT FROM
      CASE WHEN source IN ('itad', 'itad_global') THEN 'itad' ELSE source END;

-- 5. Ningún origen fuera del catálogo de bundles que leemos. Si aparece otro, la política y el catálogo dejaron de
--    hablar de las mismas tarjetas.
SELECT bundle_alert_event_id, source
FROM public.bundle_alert_events
WHERE source NOT IN ('itad', 'itad_global', 'ggdeals');

-- 6. Ningún click_url no-HTTPS. El CHECK lo impide y el cliente ntfy solo manda enlaces HTTPS: una fila aquí es un
--    destino que la persona no puede abrir con seguridad.
SELECT bundle_alert_event_id, click_url
FROM public.bundle_alert_events
WHERE click_url IS NOT NULL AND click_url NOT LIKE 'https://%';

-- 7. Ningún error que arrastre una URL o un tema. La sanitización guarda solo el nombre del resultado; una fila con
--    "://" significa que algo guardó un mensaje de excepción o una URL en la base y en los logs.
SELECT bundle_alert_event_id, last_error
FROM public.bundle_alert_events
WHERE last_error IS NOT NULL AND last_error LIKE '%://%';

-- 8. Ninguna fila enviada sin sent_at, y ninguna sent_at en una fila que no se envió. Es la diferencia entre
--    "se avisó" y "se retiró", y es lo que la persona audita después.
SELECT bundle_alert_event_id, status, sent_at
FROM public.bundle_alert_events
WHERE (status = 'sent' AND sent_at IS NULL)
   OR (status <> 'sent' AND sent_at IS NOT NULL);

-- 9. Ningún intento negativo ni fila pendiente con presupuesto agotado: una fila pendiente con attempts >= 5 ya no
--    la puede rescatar ningún ciclo (Ntfy__MaxAttempts por defecto es 5), así que solo alarga el backlog.
SELECT bundle_alert_event_id, status, attempts
FROM public.bundle_alert_events
WHERE attempts < 0
   OR (status = 'pending' AND attempts >= 5);

-- 10. Ningún aviso de una cuenta inexistente. El ACK de operador lo impide; una fila huérfana significa que la
--     cuenta se borró sin limpiar su outbox y el dedup quedaría ocupado para siempre.
SELECT a.bundle_alert_event_id, a.user_id
FROM public.bundle_alert_events a
WHERE NOT EXISTS (SELECT 1 FROM public.users u WHERE u.user_id = a.user_id);
