import "server-only";
import { cookies } from "next/headers";
import { EncryptJWT, jwtDecrypt } from "jose";

export const SESSION_COOKIE_SECURE = process.env.NODE_ENV === "production";

export const SESSION_COOKIE_NAME = SESSION_COOKIE_SECURE
  ? "__Host-app_session"
  : "app_session_dev";

type SessionUser = {
  id: number;
  username: string;
  role?: string;
};

export type AuthSession = {
  accessToken: string;
  expiresAt: string;
  refreshToken?: string;
  refreshExpiresAt?: string;
  user: SessionUser;
};

function toTime(value: string | undefined): number {
  if (!value) {
    return 0;
  }
  const timestamp = new Date(value).getTime();
  return Number.isFinite(timestamp) ? timestamp : 0;
}

export function isSessionUsable(session: AuthSession): boolean {
  const now = Date.now();
  const accessValid = toTime(session.expiresAt) > now;
  const refreshValid = toTime(session.refreshExpiresAt) > now;
  return accessValid || refreshValid;
}

function getSessionSecret(): Uint8Array {
  const secret = process.env.SESSION_SECRET;

  if (!secret || secret.length < 32) {
    throw new Error("SESSION_SECRET must be configured with at least 32 characters");
  }

  return new TextEncoder().encode(secret);
}

export async function encryptSession(session: AuthSession): Promise<string> {
  const secret = getSessionSecret();

  return await new EncryptJWT({
    accessToken: session.accessToken,
    user: session.user,
    expiresAt: session.expiresAt,
    refreshToken: session.refreshToken,
    refreshExpiresAt: session.refreshExpiresAt
  })
    .setProtectedHeader({ alg: "dir", enc: "A256GCM" })
    .setIssuedAt()
    .setExpirationTime(new Date(session.refreshExpiresAt ?? session.expiresAt))
    .encrypt(secret);
}

export async function decryptSession(token: string): Promise<AuthSession | null> {
  try {
    const secret = getSessionSecret();
    const { payload } = await jwtDecrypt(token, secret);

    const accessToken = payload.accessToken;
    const expiresAt = payload.expiresAt;
    const refreshToken = payload.refreshToken;
    const refreshExpiresAt = payload.refreshExpiresAt;
    const user = payload.user;

    if (
      typeof accessToken !== "string" ||
      typeof expiresAt !== "string" ||
      !user ||
      typeof user !== "object"
    ) {
      return null;
    }

    const u = user as { id?: unknown; username?: unknown; role?: unknown };

    if (typeof u.id !== "number" || typeof u.username !== "string") {
      return null;
    }

    return {
      accessToken,
      expiresAt,
      refreshToken: typeof refreshToken === "string" ? refreshToken : undefined,
      refreshExpiresAt: typeof refreshExpiresAt === "string" ? refreshExpiresAt : undefined,
      user: {
        id: u.id,
        username: u.username,
        role: typeof u.role === "string" ? u.role : undefined
      }
    };
  } catch {
    return null;
  }
}

export async function getServerSession(): Promise<AuthSession | null> {
  const cookieStore = await cookies();
  const encryptedToken = cookieStore.get(SESSION_COOKIE_NAME)?.value;

  if (!encryptedToken) {
    return null;
  }

  const session = await decryptSession(encryptedToken);
  if (!session) {
    return null;
  }

  if (!isSessionUsable(session)) {
    return null;
  }

  return session;
}
