type UnknownRecord = Record<string, unknown>;

export type AdminUser = {
  userId: number;
  name: string;
  email: string;
  active: boolean;
  admin: boolean;
};

export type UserCreatePayload = {
  name: string;
  email: string;
  password: string;
  active: boolean;
  admin: boolean;
};

export type UserUpdatePayload = {
  name: string;
  email: string;
  password?: string;
  active: boolean;
  admin: boolean;
};

export type UserFormState = {
  name: string;
  email: string;
  password: string;
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
      if (userId === null || typeof nameRaw !== "string" || typeof emailRaw !== "string") {
        return null;
      }

      return {
        userId,
        name: nameRaw.trim(),
        email: emailRaw.trim().toLowerCase(),
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
      active: true,
      admin: false
    };
  }

  return {
    name: user.name,
    email: user.email,
    password: "",
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

  return errors;
}
