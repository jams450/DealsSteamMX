const CSRF_COOKIE_SECURE = process.env.NODE_ENV === "production";

export const CSRF_HEADER_NAME = "x-csrf-token";
export const CSRF_COOKIE_NAME = CSRF_COOKIE_SECURE ? "__Host-app_csrf" : "app_csrf_dev";
export const CSRF_SECURE = CSRF_COOKIE_SECURE;
