# Desplegar catálogos y ofertas globales

Última actualización: 2026-10-17. Entrega con limitaciones de prueba aceptadas por el usuario. No se ha desplegado desde este entorno.

## 1. Preparar servidor y base de datos

- PostgreSQL accesible por la API y red externa Docker `server-data` existente.
- Conservar secretos y configuración actuales; no reemplazar el archivo privado por una plantilla.
- Respaldar la base antes de aplicar migraciones y comprobar cuáles están aplicadas.
- Para bundles se requieren las migraciones anteriores `2026-09-22_itad_bundles.sql`, `2026-09-23_itad_bundles_metadata.sql`; los jobs requieren `2026-10-01_job_runs.sql`.
- Aplicar manualmente la nueva `SQL/migrations/2026-10-17_global_deals.sql` antes de usar `/deals`, aunque su job esté deshabilitado. Crear el esquema de una base nueva con `SQL/schema.sql`, no reemplazar datos existentes.
- Para ntfy, si todavía falta, aplicar `SQL/migrations/2026-10-17_ntfy_deal_alerts.sql` antes de activarlo.

No se ejecutó ninguna migración en esta sesión. Los checks de identidad no sustituyen revisar estas tablas; esta entrega no reasigna identidades canónicas.

## 2. Configuración privada

Reutilizar `ITAD__ApiKey`, `GgDeals__ApiKey`, región gg.deals y configuración de FX existentes. Las claves no deben ir en chat, documentación, logs ni Git.

Para primera validación mantener los jobs nuevos apagados:

```dotenv
GlobalBundles__Enabled=false
GgDealsBundles__Enabled=false
GlobalDeals__Enabled=false
Ntfy__Enabled=false
```

Atención: el default de `GlobalBundles__Enabled` en Compose/código es **true**; establecerlo explícitamente en false si se quiere arrancar sin ingesta. Los otros tres switches tienen default false.

Opciones completas:
- [ITAD bundles](2026-10-17_GLOBAL_BUNDLE_SETTINGS.md).
- [gg.deals bundles](2026-10-17_GGDEALS_BUNDLE_SETTINGS.md).
- [Ofertas ITAD](2026-10-17_GLOBAL_DEAL_SETTINGS.md).
- [ntfy](NTFY_SETTINGS.md).

Para ntfy: BaseUrl `https://ntfy.amitzi.xyz/`, Topic `Deals`, `Ntfy__AuthToken` solo token, sin prefijo Bearer. Activar puede notificar ofertas ya persistidas, no solo futuras. El feed `global_deals` es independiente: el notificador actual lee `game_offers`, no este nuevo feed ni bundles.

## 3. Build y arranque

En un checkout de la entrega con las herramientas y dependencias del proyecto:

```bash
dotnet build Deals.sln
cd Deals.Web
pnpm build
pnpm lint
cd ..
docker compose build api frontend
docker compose up -d api frontend
```

La imagen frontend instala desde el lockfile; un fallo debe resolverse, no ignorarse. No incluir `.verify-t6/` ni `.worktrees/` en artefactos de entrega. En este entorno no se ejecutaron build/lint frontend, navegador, Docker de despliegue ni PostgreSQL real; build .NET y pruebas deterministas sí aprobaron.

## 4. Smoke y activación gradual

Con sesión iniciada:
1. Abrir `/wishlist`: escribir, esperar 300ms, pulsar Enter y limpiar. Comprobar peticiones con `search`, reinicio a primera página y conservación de otros filtros aplicados. Los filtros pendientes del panel no deben aplicarse solos.
2. Abrir `/search`, buscar al menos dos caracteres y probar sugerencias.
3. Abrir `/bundles`: paginar y comprobar que bundles sin wishlist permanecen visibles. El resaltado requiere identidad comprobada; gg.deals no aporta ITAD UUID y no se adivina coincidencia por título.
4. Activar primero `GlobalBundles__Enabled=true`, recrear API y observar `job_runs`/logs. Primer pase tras 120s por defecto, salvo gate de ejecución reciente. Una página fallida debe conservar snapshot anterior.
5. Validar respuestas reales y consultas PostgreSQL. Después activar `GgDealsBundles__Enabled=true` (espera inicial300s) y `GlobalDeals__Enabled=true` (300s). Recrear API tras cambios de entorno. Las ventanas de recuperación pueden omitir el pase de arranque si ya hubo uno reciente.
6. Revisar `/deals`, monedas originales, estimación FX y exclusión de wishlist solo cuando se conoce identidad exacta.
7. Configurar y probar ntfy de forma controlada únicamente al desear los envíos; no habilitarlo como parte accidental del smoke.

No prometer todos los bundles de todas las tiendas: se muestran los catálogos de las fuentes sincronizadas. gg.deals no soporta México; su región configurada es referencia y no garantiza compra desde México. Monedas distintas no se comparan directamente. El promedio de un tier exige comprar el tier completo; en selección divide entre juegos elegidos, no todas las opciones.

## 5. Límites y recuperación

- Epic Path of Exile 2 sigue pendiente: obtener payload desde imagen runtime autorizada; no usar curl del host como prueba equivalente.
- Ante fallo de ingesta, apagar el switch afectado y revisar logs sanitizados y `job_runs`; no borrar snapshots ni aplicar rollback destructivo por iniciativa automática.
- No revertir migraciones con borrado de tablas sin plan de datos autorizado.
- Revisión nativa del candidato acumulado excedió presupuesto sin crear autoridad; el commit de búsqueda tuvo consentimiento declinado en el host. No hay aprobación nativa general de esta entrega. Verificadores independientes sí ejecutaron las pruebas deterministas por unidad.
