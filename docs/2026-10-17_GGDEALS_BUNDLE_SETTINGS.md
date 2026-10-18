# Configuración de bundles globales gg.deals

Última actualización: 2026-10-17. Nueva integración deshabilitada por defecto. Reutiliza las credenciales privadas y el governor de `GgDeals`; no hay una clave adicional.

Agregar al entorno privado del despliegue sin reemplazar configuración existente:

```dotenv
GgDealsBundles__Enabled=false
GgDealsBundles__MaxPages=40
GgDealsBundles__CursorValidityMinutes=60
GgDealsBundles__RefreshHours=24
GgDealsBundles__JitterMinutes=15
GgDealsBundles__StartupDelaySeconds=300
GgDealsBundles__MinHoursBetweenRuns=12
```

Compose propaga las siete variables. Fuera de Compose deben exportarse al proceso API; este documento no se carga automáticamente.

- `MaxPages`: 1–200. Si no termina el catálogo dentro del presupuesto, conservar snapshot anterior.
- `CursorValidityMinutes`: 1–60; nunca ampliar el límite documental de una hora.
- `RefreshHours`: positivo. `JitterMinutes`: 0–1440.
- Espera inicial y ventana mínima entre pases de arranque: no negativas.

La región procede de `GgDeals__Region` existente, no del país de ITAD. México no está soportado por esta API; una región de referencia no demuestra disponibilidad ni precio final en México. La UI no debe afirmar una región fija distinta de la configuración.

Antes de habilitar: completar pruebas y revisar [la guía del contrato](2026-10-17_GGDEALS_BUNDLES_API.md) y [la implementación](2026-10-17_GGDEALS_BUNDLES_INTEGRATION.md). No se han validado llamadas autenticadas, PostgreSQL real ni compilación frontend en este entorno. El job no debe considerarse certificado en producción a partir de fixtures sintéticas.

No guardar claves en este archivo. Las URLs del proveedor y de paginación contienen la key: nunca registrarlas completas. Activar solo cuando la configuración privada y la región de referencia estén verificadas.
