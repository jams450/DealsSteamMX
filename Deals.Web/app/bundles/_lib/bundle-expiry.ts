// Etiqueta de vencimiento de un bundle. Módulo puro: recibe la fecha y el reloj, así que se puede probar
// con `node --test` y no introduce diferencias de hidratación (nada de `Date.now()` aquí dentro).
//
// Reglas del usuario (S1): rojo hasta 3 días, amarillo de más de 3 hasta 7, verde más de 7. Los días
// restantes son `ceil((expiresAt - ahora) / 24h)`, redondeo hacia arriba porque un bundle que vence en 23
// horas sigue siendo «1 día» y no «0 días»: el 0 se leería como expirado.
//
// El color nunca es el único canal: cada estado trae su texto con los días explícitos y una palabra de
// estado («Urgente»/«Pronto»/«Con tiempo»), y el badge conserva los tokens semánticos del tema
// (`danger`/`warning`/`success`/`neutral`) en vez de colores literales.

const DAY_MS = 24 * 60 * 60 * 1000;

/** Último día que se pinta en rojo. */
export const EXPIRY_CRITICAL_MAX_DAYS = 3;
/** Último día que se pinta en amarillo. El verde empieza en el 8. */
export const EXPIRY_WARNING_MAX_DAYS = 7;

export type BundleExpiryStatus =
  | "expired"
  | "critical"
  | "soon"
  | "comfortable"
  | "missing"
  | "invalid";

export type BundleExpiryTone = "danger" | "warning" | "success" | "neutral";

export type BundleExpiryView = {
  readonly status: BundleExpiryStatus;
  readonly tone: BundleExpiryTone;
  /** Días restantes redondeados hacia arriba; `null` cuando no hay cuenta (expirado o sin fecha). */
  readonly daysRemaining: number | null;
  /** Texto completo del badge: días explícitos + estado. Nunca vacío ni solo color. */
  readonly label: string;
  /** Clases del badge existente, con el token semántico del tema. */
  readonly badgeClass: string;
};

const BADGE_CLASS: Record<BundleExpiryTone, string> = {
  danger: "tabler-badge tabler-badge-danger",
  warning: "tabler-badge tabler-badge-warning",
  success: "tabler-badge tabler-badge-success",
  neutral: "tabler-badge tabler-badge-muted"
};

// `null` = la fecha falta; `NaN` = la fecha llegó pero no es legible. Son dos causas distintas del mismo
// «no hay cuenta» y el usuario merece saber cuál, así que no comparten etiqueta.
function parseExpiry(expiresAt: string | null | undefined): number | null {
  if (typeof expiresAt !== "string") return null;
  const trimmed = expiresAt.trim();
  if (trimmed.length === 0) return null;
  const parsed = Date.parse(trimmed);
  return Number.isNaN(parsed) ? null : parsed;
}

function view(status: BundleExpiryStatus, tone: BundleExpiryTone, daysRemaining: number | null, label: string): BundleExpiryView {
  return { status, tone, daysRemaining, label, badgeClass: BADGE_CLASS[tone] };
}

function unknownView(expiresAt: string | null | undefined): BundleExpiryView {
  const isMissing = typeof expiresAt !== "string" || expiresAt.trim().length === 0;
  return isMissing
    ? view("missing", "neutral", null, "Sin fecha de expiración")
    : view("invalid", "neutral", null, "Fecha de expiración inválida");
}

const STATUS_TEXT: Record<"critical" | "soon" | "comfortable", string> = {
  critical: "Urgente",
  soon: "Pronto",
  comfortable: "Con tiempo"
};

function dayText(days: number): string {
  return days === 1 ? "Expira en 1 día" : `Expira en ${days} días`;
}

/**
 * Días restantes hacia arriba, o `null` si la fecha falta, es ilegible o ya pasó. El expirado no cuenta
 * hacia atrás: no queda día que anunciar.
 */
export function expiryDaysRemaining(expiresAt: string | null | undefined, now: number): number | null {
  const expires = parseExpiry(expiresAt);
  if (expires === null || !Number.isFinite(now)) return null;
  const delta = expires - now;
  return delta > 0 ? Math.ceil(delta / DAY_MS) : null;
}

/**
 * Vista de vencimiento de un bundle contra un reloj dado.
 *
 * - `delta <= 0` ⇒ expirado: ya no está activo, así que no se pinta como cuenta regresiva urgente;
 *   etiqueta propia y tono neutro.
 * - `days <= 3` ⇒ rojo; `4..7` ⇒ amarillo; `>= 8` ⇒ verde.
 * - fecha ausente o ilegible ⇒ neutro, nunca verde (no se puede afirmar que quede tiempo).
 */
export function bundleExpiryView(expiresAt: string | null | undefined, now: number): BundleExpiryView {
  const expires = parseExpiry(expiresAt);
  if (expires === null || !Number.isFinite(now)) return unknownView(expiresAt);

  const delta = expires - now;
  if (delta <= 0) return view("expired", "neutral", null, "Expirado");

  const days = Math.ceil(delta / DAY_MS);
  const status: BundleExpiryStatus =
    days <= EXPIRY_CRITICAL_MAX_DAYS ? "critical" : days <= EXPIRY_WARNING_MAX_DAYS ? "soon" : "comfortable";
  const tone: BundleExpiryTone = status === "critical" ? "danger" : status === "soon" ? "warning" : "success";

  return view(status, tone, days, `${dayText(days)} · ${STATUS_TEXT[status]}`);
}
