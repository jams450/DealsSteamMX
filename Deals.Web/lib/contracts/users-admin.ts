type UnknownRecord = Record<string, unknown>;

export type AdminUser = {
  userId: number;
  name: string;
  email: string;
  steamId64: string | null;
  active: boolean;
  admin: boolean;
};

export type UserCreatePayload = {
  name: string;
  email: string;
  password: string;
  steamId64: string | null;
  active: boolean;
  admin: boolean;
};

export type UserUpdatePayload = {
  name: string;
  email: string;
  password?: string;
  steamId64: string | null;
  active: boolean;
  admin: boolean;
};

export type UserFormState = {
  name: string;
  email: string;
  password: string;
  steamId64: string;
  active: boolean;
  admin: boolean;
};

export type UserFormErrors = Partial<Record<keyof UserFormState, string>>;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function toBool(value: unknown, fallback = false): boolean {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") return value.toLowerCase() === "true";
  return fallback;
}

function toNumber(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export function normalizeUsers(input: unknown): AdminUser[] {
  if (!Array.isArray(input)) return [];

  return input
    .map((item) => {
      if (!isRecord(item)) return null;

      const userId = toNumber(item.userId ?? item.UserId);
      const nameRaw = item.name ?? item.Name;
      const emailRaw = item.email ?? item.Email;
      const steamId64Raw = item.steamId64 ?? item.SteamId64;
      if (userId === null || typeof nameRaw !== "string" || typeof emailRaw !== "string") {
        return null;
      }

      return {
        userId,
        name: nameRaw.trim(),
        email: emailRaw.trim().toLowerCase(),
        steamId64: typeof steamId64Raw === "string" && steamId64Raw.trim() ? steamId64Raw.trim() : null,
        active: toBool(item.active ?? item.Active, true),
        admin: toBool(item.admin ?? item.Admin, false)
      } satisfies AdminUser;
    })
    .filter((item): item is AdminUser => item !== null);
}

export function toUserFormState(user?: AdminUser): UserFormState {
  if (!user) {
    return {
      name: "",
      email: "",
      password: "",
      steamId64: "",
      active: true,
      admin: false
    };
  }

  return {
    name: user.name,
    email: user.email,
    password: "",
    steamId64: user.steamId64 ?? "",
    active: user.active,
    admin: user.admin
  };
}

export function validateUserForm(form: UserFormState, isEdit: boolean): UserFormErrors {
  const errors: UserFormErrors = {};

  if (!form.name.trim()) {
    errors.name = "Nombre requerido";
  }

  if (!form.email.trim()) {
    errors.email = "Correo requerido";
  } else if (!/^\S+@\S+\.\S+$/.test(form.email.trim())) {
    errors.email = "Correo inválido";
  }

  if (!isEdit && form.password.trim().length < 8) {
    errors.password = "Password mínimo 8 caracteres";
  }

  if (isEdit && form.password.trim().length > 0 && form.password.trim().length < 8) {
    errors.password = "Si cambias password, mínimo 8 caracteres";
  }

  const steamId64 = form.steamId64.trim();
  if (steamId64 && !/^[0-9]{17}$/.test(steamId64)) {
    errors.steamId64 = "SteamID64 debe tener 17 dígitos";
  }

  return errors;
}
